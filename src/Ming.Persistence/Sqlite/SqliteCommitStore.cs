using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MingSim.Application.Ports;
using MingSim.Domain;
using MingSim.Domain.Common;
using MingSim.Domain.Events;
using MingSim.Domain.Realtime;
using MingSim.Simulation.Realtime;

namespace MingSim.Persistence.Sqlite;

/// <summary>
/// 单文件 SQLite 提交存储：把"正式提交（当前状态 + 事件日志追加 + 校验快照）"落成单个 SQLite 事务。
/// </summary>
/// <remarks>
/// 为什么三个端口由一个类实现：原子性要求"状态、事件日志、快照指针"必须在同一个事务提交，
/// 三个端口如果各持一个连接/事务就无法保证全有或全无；同一连接 + 暂存批次的语义是
/// 本适配器对端口契约的扩展，而不是新的存储抽象。
/// 为什么端口写方法只是"暂存"：正式提交的单位是"一次 SQLite 事务"，端口方法分别登记
/// 本次提交要写哪个状态、哪份事件日志、哪个快照清单，由 <see cref="CommitAll"/> 统一原子落盘；
/// 任一步失败整体回滚，调用方永远不会看到半提交。
/// 为什么恢复走只读静态入口：重复启动/重复恢复必须无副作用，恢复绝不写库；并且恢复会
/// 重算覆盖全库内容行的校验和，任何内容字节被篡改都会在返回快照前抛异常，不发布半状态。
/// 本类依赖 Microsoft.Data.Sqlite（MIT）；离线沙箱无法还原该包时由 csproj 条件编译排除，
/// CI（联网）启用后编译并执行 SqliteStoreAcceptance 验收。
/// </remarks>
public sealed class SqliteCommitStore : IWorldStore, IAuditJournal, ISnapshotStore, ICommitStore, IDisposable
{
    /// <summary>SQLite schema 版本；schema 变更必须迁移而不是原地改表。</summary>
    private const int SchemaVersion = 1;

    /// <summary>当前整库校验和布局（v2）：覆盖全部内容行元数据 + state_blob/event_blob/snapshot_blob 字节。</summary>
    private const string ChecksumMagicV2 = "mingsim-commit-v2";

    /// <summary>v1 时代整库校验和布局（#35 之前写入的旧档）：只覆盖元数据列，不覆盖任何 blob 字节。
    /// 恢复侧先按 v2 重算比对，失败再按 v1 重算比对——真实旧档必须能继续被读取（迁移路径）。</summary>
    private const string ChecksumMagicV1 = "mingsim-commit-v1";

    /// <summary>快照载荷格式版本（与 SnapshotCodec 的 FormatVersion/FormatVersionV1 一致；恢复路径用它选择解码或迁移）。</summary>
    private const byte SnapshotPayloadFormatV2 = 2;
    private const byte SnapshotPayloadFormatV1 = 1;

    private readonly SqliteConnection _connection;
    private readonly string _databasePath;
    private readonly WorldId _worldId;
    private readonly object _gate = new();
    private WorldState? _pendingState;
    private IReadOnlyList<DomainEvent>? _pendingEvents;
    private SnapshotPreparation? _pendingSnapshot;

    /// <summary>整库校验和分类结果：当前布局（含 blob）完好 / 旧版布局（真实旧档）完好 / 失配（篡改或损坏）。</summary>
    private enum ArchiveChecksumState
    {
        IntactNewLayout,
        LegacyLayout,
        Mismatched,
    }

    public SqliteCommitStore(string databasePath, WorldId worldId)
    {
        ArgumentNullException.ThrowIfNull(databasePath);
        _databasePath = databasePath;
        _worldId = worldId;
        // Pooling=false：Microsoft.Data.Sqlite 默认连接池会在 Dispose 后仍持有文件句柄，
        // 导致删除/导出 .db 失败；单写者存档不需要连接池，关闭它让 Dispose 立即释放句柄。
        _connection = new SqliteConnection($"Data Source={databasePath};Pooling=false");
        _connection.Open();
        InitializeSchema();
    }

    /// <summary>当前已提交快照（来自快照表的最新行；未提交过则为 null）。</summary>
    public SnapshotPreparation? Current
    {
        get
        {
            lock (_gate)
            {
                return ReadCurrentPreparation();
            }
        }
    }

    /// <summary>
    /// IWorldStore：读取当前已提交状态（快速加载路径）。与恢复路径一样受整库校验和门禁
    /// （v2 布局覆盖 state_blob 字节；旧版布局仅限真实 v1 旧档），并交叉验证解码状态的身份字段
    /// 与行/meta 一致（P1-PERSIST-05/06：任何内容字节被篡改都在返回状态前抛异常，fail-closed）。
    /// </summary>
    public WorldState Load(WorldId worldId)
    {
        lock (_gate)
        {
            var meta = ReadMetaRow(_connection, worldId);
            if (meta is null)
            {
                throw new KeyNotFoundException($"世界 {worldId} 不存在或尚未提交。");
            }

            var checksumState = ClassifyChecksum(_connection, worldId, meta.TotalChecksum);
            if (checksumState == ArchiveChecksumState.Mismatched)
            {
                throw new InvalidDataException("存档内容校验失败：数据库可能被篡改或损坏。");
            }

            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT ws.state_blob, ws.world_version, ws.commit_id
                FROM world_meta AS m
                JOIN world_state AS ws
                  ON ws.world_id = m.world_id AND ws.world_version = m.current_world_version
                WHERE m.world_id = $world;
                """;
            command.Parameters.AddWithValue("$world", worldId.Value);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new KeyNotFoundException($"世界 {worldId} 不存在或尚未提交。");
            }

            var state = SnapshotCodec.DeserializeWorld(reader.GetFieldValue<byte[]>(0));
            var rowWorldVersion = reader.GetInt64(1);
            var rowCommitId = reader.GetString(2);
            if (state.Id != worldId ||
                state.WorldVersion != rowWorldVersion ||
                !StringComparer.Ordinal.Equals(state.CommitId, rowCommitId) ||
                state.WorldVersion != meta.CurrentWorldVersion)
            {
                throw new InvalidDataException("状态行内容与版本元数据不一致，存档损坏。");
            }

            return state;
        }
    }

    /// <summary>IWorldStore：暂存本次提交要写入的当前状态（CommitAll 时原子落盘）。</summary>
    public void Commit(WorldState newState)
    {
        ArgumentNullException.ThrowIfNull(newState);
        lock (_gate)
        {
            _pendingState = newState;
        }
    }

    /// <summary>IAuditJournal：暂存本次提交的完整事件日志（含此前已提交部分，落盘时按序号只追加增量）。</summary>
    public void Append(WorldId worldId, IReadOnlyList<DomainEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        lock (_gate)
        {
            _pendingEvents = events;
        }
    }

    /// <summary>IAuditJournal：读回全部已提交事件，按 EventSequence 升序。</summary>
    public IReadOnlyList<DomainEvent> Read(WorldId worldId)
    {
        lock (_gate)
        {
            var events = new List<DomainEvent>();
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT event_blob FROM event_journal
                WHERE world_id = $world
                ORDER BY event_sequence;
                """;
            command.Parameters.AddWithValue("$world", worldId.Value);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                events.Add(SnapshotCodec.DeserializeEvent(reader.GetFieldValue<byte[]>(0)));
            }

            return events;
        }
    }

    /// <summary>ISnapshotStore：校验快照候选（世界一致、事件世界一致、hash 格式）。</summary>
    /// <remarks>
    /// 这里只能做端口签名内的轻量校验；完整的 canonical hash / payload checksum 校验
    /// 在恢复路径由 RealtimeSimulationRuntime.Restore 完成（快照捕获时 Runtime 已原子计算过二者）。
    /// </remarks>
    public SnapshotPreparation Prepare(WorldState state, IReadOnlyList<DomainEvent> events)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(events);
        var snapshotState = state.Clone();
        var stateHash = CanonicalStateHasher.Compute(
            snapshotState,
            [],
            0,
            0,
            [],
            "schema=1;streams=none",
            events,
            0m,
            state.GameTime,
            state.WorldVersion,
            0,
            false,
            1.0,
            []);
        var valid = stateHash.Length == 64 && events.All(domainEvent => domainEvent.WorldId == state.Id);
        return new SnapshotPreparation(
            state.Id,
            state.TurnNumber,
            stateHash,
            valid,
            snapshotState,
            events.ToArray());
    }

    /// <summary>ISnapshotStore：暂存快照清单（CommitAll 时连同完整快照载荷一起落盘并切换指针）。</summary>
    public void Promote(SnapshotPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (!preparation.IsValid)
        {
            throw new InvalidOperationException("不能提升一个未通过校验的快照。");
        }

        lock (_gate)
        {
            _pendingSnapshot = preparation;
        }
    }

    /// <summary>
    /// 单事务原子提交（ICommitStore 的真正唯一入口，P1-PERSIST-01）：从 CommitPackage 的权威快照
    /// 取 full snapshot 事实，JournalEvents 只作 delta append，拒绝结果（Outcome）一并落盘——
    /// state / delta events / snapshot / outcome / meta 在同一个 BEGIN IMMEDIATE ... COMMIT 中完成；
    /// 任何一步失败整体回滚，数据库保持上一个完整提交。本方法不再调用语义不同的旧 public staging API
    /// （Commit/Append/Prepare/Promote），也不把 JournalEvents 当作整本日志重复写入。
    /// </summary>
    public CommitReceipt CommitWorld(CommitPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        lock (_gate)
        {
            try
            {
                var state = SnapshotReflection.GetState(package.Snapshot);
                ValidateJournalDeltaMatchesOutbox(package.JournalEvents, SnapshotReflection.GetOutboxEvents(package.Snapshot));
                var version = CommitCore(package.Snapshot, state, package.JournalEvents, package.Outcome);
                RunCheckpoint();
                return new CommitReceipt(true, version, null);
            }
            catch (Exception exception)
            {
                return new CommitReceipt(false, -1, exception.Message);
            }
        }
    }

    /// <summary>
    /// 单事务提交核心：写状态行 + 事件日志增量 + 快照行 + meta（+ 可选拒绝结果），
    /// 计算并回写覆盖全部内容行与 blob 字节的整库校验和，然后 COMMIT。幂等重提交（同一版本
    /// 且快照字节完全相同）直接 no-op。提交序列（snapshot_seq）独立于 WorldVersion 单调推进。
    /// </summary>
    private long CommitCore(RealtimeSnapshot snapshot, WorldState state, IReadOnlyList<DomainEvent> journalEvents, InputOutcome? outcome)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(journalEvents);
        var payload = SnapshotCodec.Serialize(snapshot);
        var stateHash = snapshot.StateHash;
        var payloadChecksum = snapshot.PayloadChecksum;

        using var transaction = _connection.BeginTransaction(deferred: false);
        try
        {
            var current = ReadCurrentMeta();
            var currentVersion = current?.CurrentWorldVersion ?? -1;
            if (state.WorldVersion < currentVersion)
            {
                throw new InvalidOperationException(
                    $"提交版本回退：库中当前版本 {currentVersion}，试图写入 {state.WorldVersion}。");
            }

            var isIdenticalRecommit = current is not null &&
                state.WorldVersion == currentVersion &&
                IsIdenticalLatestSnapshot(payload, current!.CurrentSnapshotSeq);
            if (isIdenticalRecommit)
            {
                // 幂等重提交：同一版本且快照字节完全相同，什么都不写，直接结束本次提交。
                transaction.Commit();
                return state.WorldVersion;
            }

            WriteStateRow(state, stateHash);
            AppendJournalDelta(journalEvents);
            var snapshotSeq = WriteSnapshotRow(payload, state, stateHash, payloadChecksum);
            // 先写 meta 占位、再计算校验和、最后回写：校验和布局从不包含 total_checksum 列本身，
            // 避免"为了校验 meta 又要先知道 meta 里的校验和"的自引用（提交/恢复两侧布局必须完全一致）。
            WriteMeta(state, stateHash, payloadChecksum, snapshotSeq, totalChecksum: "");
            if (outcome is not null)
            {
                WriteOutcomeRow(outcome);
            }

            var totalChecksum = ComputeTotalChecksum();
            UpdateTotalChecksum(totalChecksum);
            transaction.Commit();
            return state.WorldVersion;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// 旧 public staging 路径（接口兼容 + 既有测试）：暂存 → 单事务落盘。语义与 CommitWorld 一致，
    /// 但走"先暂存再 CommitAll"的旧调用面；新代码应使用 CommitWorld 单入口。
    /// </summary>
    public void CommitAll(RealtimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (_pendingState is null || _pendingEvents is null || _pendingSnapshot is null)
            {
                throw new InvalidOperationException(
                    "提交前必须先通过端口暂存：Commit(状态)、Append(事件日志)、Prepare/Promote(快照清单)。");
            }

            try
            {
                VerifyStagedFacetsMatchSnapshot(snapshot);
                CommitCore(snapshot, _pendingState, _pendingEvents, outcome: null);
            }
            finally
            {
                ClearPending();
            }

            // COMMIT 之后做一次显式 checkpoint，让 .db 主文件自洽（等价于导出存档前的安全状态）。
            RunCheckpoint();
        }
    }

    /// <summary>COMMIT 之后做一次显式 checkpoint，让 .db 主文件自洽（等价于导出存档前的安全状态）。</summary>
    private void RunCheckpoint()
    {
        using var checkpoint = _connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        checkpoint.ExecuteNonQuery();
    }

    /// <summary>
    /// 崩溃恢复（只读）：先分类整库校验和（v2 含 blob 字节 / v1 旧布局 / 失配），再读取当前快照并
    /// 逐项交叉验证解码内容与行/meta 一致（P1-PERSIST-06），最后校验事件日志连续性并逐条比对
    /// 事件内容（P2-09）。任何内容字节被篡改都不会被静默发布；返回的快照仍需交给
    /// <see cref="RealtimeSimulationRuntime.Restore"/> 做权威哈希校验后才能使用。
    /// </summary>
    /// <remarks>
    /// fail-closed 语义（P1-PERSIST-05/06）：
    /// - 校验和失配 + 当前快照本体完好 → 篡改发生在事件/状态/历史行/元数据 → 抛异常，绝不发布；
    /// - 当前快照不可用（解码失败、迁移校验失败、或解码内容与行/meta 不一致——包括"另一份合法
    ///   payload 整体替换"）→ 按快照序列降序回退到上一个 READY 快照（doc 08 §15），
    ///   替换内容绝不作为 current 发布；没有可用 READY 则抛异常；
    /// - 旧版 v1 布局校验和（#35 之前写入的真实旧档）→ 允许继续读取并走 v1→v2 迁移；
    ///   其余任何校验和失配一律 fail-closed。
    /// 注：回退后的世界版本落后于 meta.current_world_version，可加载/恢复/检查，
    /// 但下一次提交会因"版本回退"守卫被拒——完整续玩需要显式修复存档指针，超出本方法职责
    /// （恢复必须只读，绝不覆盖原档）。
    /// </remarks>
    public static RealtimeSnapshot RestoreLatest(string databasePath, WorldId worldId)
    {
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("存档数据库不存在。", databasePath);
        }

        // Pooling=false：恢复是只读的一次性操作，Dispose 后必须立即释放文件句柄（与写路径一致）。
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=false");
        connection.Open();
        var meta = ReadMetaRow(connection, worldId)
            ?? throw new InvalidDataException($"世界 {worldId} 没有已提交的提交记录。");
        if (!StringComparer.Ordinal.Equals(meta.WorldId, worldId.Value))
        {
            throw new InvalidDataException("存档中的世界编号与请求不一致。");
        }

        var checksumState = ClassifyChecksum(connection, worldId, meta.TotalChecksum);
        var (snapshot, fellBack) = ReadCurrentOrFallback(connection, worldId, meta, checksumState);

        VerifyJournalContinuity(connection, worldId);
        VerifyJournalMatchesSnapshot(connection, worldId, snapshot, allowJournalLongerThanOutbox: fellBack);
        return snapshot;
    }

    /// <summary>整库校验和分类：v2（含 blob 字节）→ 旧版 v1 布局（真实旧档）→ 失配。</summary>
    private static ArchiveChecksumState ClassifyChecksum(SqliteConnection connection, WorldId worldId, string storedChecksum)
    {
        if (StringComparer.Ordinal.Equals(ComputeTotalChecksumV2(connection, worldId), storedChecksum))
        {
            return ArchiveChecksumState.IntactNewLayout;
        }

        if (StringComparer.Ordinal.Equals(ComputeLegacyTotalChecksum(connection, worldId), storedChecksum))
        {
            return ArchiveChecksumState.LegacyLayout;
        }

        return ArchiveChecksumState.Mismatched;
    }

    /// <summary>
    /// 读取当前快照并交叉验证；不可用时按序列降序回退到上一个 READY 快照（doc 08 §15）。
    /// 校验和失配 + 当前快照本体完好 = 篡改发生在其他内容 → fail-closed 抛异常。
    /// </summary>
    private static (RealtimeSnapshot Snapshot, bool FellBack) ReadCurrentOrFallback(
        SqliteConnection connection, WorldId worldId, MetaRowFull meta, ArchiveChecksumState checksumState)
    {
        var current = TryReadCandidate(connection, worldId, meta, meta.CurrentSnapshotSeq, validateAgainstMeta: true);
        if (current is not null)
        {
            if (checksumState == ArchiveChecksumState.Mismatched)
            {
                // 当前快照本体完好且与行/meta 一致，但整库校验和失配 → 篡改发生在
                // event_blob/state_blob/历史行/元数据 → 绝不发布（fail-closed）。
                throw new InvalidDataException("存档内容校验失败：数据库可能被篡改或损坏。");
            }

            return (current, false);
        }

        // 当前快照不可用（解码失败 / 迁移校验失败 / 内容与行或 meta 不一致）：回退到上一个 READY。
        for (var seq = meta.CurrentSnapshotSeq - 1; seq >= 0; seq--)
        {
            var candidate = TryReadCandidate(connection, worldId, meta, seq, validateAgainstMeta: false);
            if (candidate is not null)
            {
                return (candidate, true);
            }
        }

        throw new InvalidDataException("当前快照不可用且没有可用的历史 READY 快照，拒绝恢复（fail-closed）。");
    }

    /// <summary>
    /// 读取并解码第 seq 个快照，交叉验证解码内容与行/meta 一致（P1-PERSIST-06）：
    /// - 内容字段（WorldId/WorldVersion/CommitId/GameTime）必须与快照行一致；当前快照还须与 meta 一致；
    /// - StateHash/PayloadChecksum 与行/meta 一致（仅对非迁移载荷——v1 载荷迁移后按当前规则 re-seal，
    ///   与 v1 时代行值必然不同，其校验由迁移机制（<see cref="SnapshotCodec.MigrateV1ToV2"/>）
    ///   与 <see cref="RealtimeSimulationRuntime.Restore"/> 承担）。
    /// 任何不一致都视为该快照不可用（返回 null），绝不静默发布。
    /// </summary>
    private static RealtimeSnapshot? TryReadCandidate(
        SqliteConnection connection, WorldId worldId, MetaRowFull meta, long seq, bool validateAgainstMeta)
    {
        byte[] payload;
        try
        {
            payload = ReadSnapshotPayload(connection, worldId, seq);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        var row = ReadSnapshotRow(connection, worldId, seq);
        if (row is null)
        {
            return null;
        }

        if (!TryDecode(payload, out var snapshot, out var migrated))
        {
            return null;
        }

        var state = SnapshotReflection.GetState(snapshot);
        if (!StringComparer.Ordinal.Equals(state.Id.Value, worldId.Value) ||
            state.WorldVersion != row.WorldVersion ||
            !StringComparer.Ordinal.Equals(state.CommitId, row.CommitId))
        {
            return null;
        }

        if (validateAgainstMeta &&
            (state.WorldVersion != meta.CurrentWorldVersion ||
             !StringComparer.Ordinal.Equals(state.CommitId, meta.CurrentCommitId) ||
             state.GameTime.Value.UtcTicks != meta.CurrentGameTimeTicks))
        {
            return null;
        }

        if (!migrated)
        {
            if (!StringComparer.Ordinal.Equals(snapshot.StateHash, row.StateHash) ||
                !StringComparer.Ordinal.Equals(snapshot.PayloadChecksum, row.PayloadChecksum))
            {
                return null;
            }

            if (validateAgainstMeta &&
                (!StringComparer.Ordinal.Equals(snapshot.StateHash, meta.CurrentStateHash) ||
                 !StringComparer.Ordinal.Equals(snapshot.PayloadChecksum, meta.CurrentPayloadChecksum)))
            {
                return null;
            }
        }

        return snapshot;
    }

    /// <summary>解码快照载荷：按格式版本选择直接解码或先迁移 v1→v2；任何结构/校验失败都视为不可用。</summary>
    private static bool TryDecode(byte[] payload, out RealtimeSnapshot snapshot, out bool migrated)
    {
        snapshot = null!;
        migrated = false;
        try
        {
            var format = SnapshotCodec.PeekFormatVersion(payload);
            if (format == SnapshotPayloadFormatV2)
            {
                snapshot = SnapshotCodec.Deserialize(payload);
                migrated = false;
                return true;
            }

            if (format == SnapshotPayloadFormatV1)
            {
                snapshot = SnapshotCodec.Deserialize(SnapshotCodec.MigrateV1ToV2(payload));
                migrated = true;
                return true;
            }

            return false;
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            return false;
        }
    }

    /// <summary>
    /// ICommitStore 保留的独立写结果 API（Reject 已纯化，不再调用它；供外部/诊断显式记录结果使用）。
    /// 单条 INSERT，独立事务；世界版本不变。
    /// </summary>
    public CommitReceipt RecordOutcome(InputOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                WriteOutcomeRow(outcome);
                transaction.Commit();
                return new CommitReceipt(true, outcome.WorldVersion, null);
            }
            catch (Exception exception)
            {
                transaction.Rollback();
                return new CommitReceipt(false, outcome.WorldVersion, exception.Message);
            }
        }
    }

    /// <summary>把拒绝/过期结果写入 command_outcomes 表（必须在调用方事务内执行）。</summary>
    private void WriteOutcomeRow(InputOutcome outcome)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO command_outcomes (world_id, command_id, outcome_code, message, world_version)
            VALUES ($world, $commandId, $code, $message, $version);
            """;
        command.Parameters.AddWithValue("$world", _worldId.Value);
        command.Parameters.AddWithValue("$commandId", outcome.CommandId);
        command.Parameters.AddWithValue("$code", outcome.OutcomeCode);
        command.Parameters.AddWithValue("$message", outcome.Message);
        command.Parameters.AddWithValue("$version", outcome.WorldVersion);
        command.ExecuteNonQuery();
    }

    /// <summary>事件日志增量必须是快照 outbox 的尾部（防止调用方把无关/乱序事件当 delta 提交）。</summary>
    private static void ValidateJournalDeltaMatchesOutbox(
        IReadOnlyList<DomainEvent> journalDelta,
        IReadOnlyList<DomainEvent> snapshotOutbox)
    {
        if (journalDelta.Count > snapshotOutbox.Count)
        {
            throw new InvalidOperationException("事件日志增量不能超过快照 outbox 长度。");
        }

        for (var index = 0; index < journalDelta.Count; index++)
        {
            var journalEvent = journalDelta[index];
            var outboxEvent = snapshotOutbox[snapshotOutbox.Count - journalDelta.Count + index];
            if (journalEvent.EventSequence != outboxEvent.EventSequence ||
                !StringComparer.Ordinal.Equals(journalEvent.EventId, outboxEvent.EventId))
            {
                throw new InvalidOperationException("事件日志增量不是快照 outbox 的尾部，拒绝提交。");
            }
        }
    }

    /// <summary>
    /// ICommitStore：只读恢复最新完整提交。数据库不存在视为"还没有任何提交"（返回 null），
    /// 其余任何读取/校验失败原样抛出——恢复失败必须可见，绝不静默降级。
    /// </summary>
    public LoadedWorld? LoadCommittedWorld()
    {
        if (!File.Exists(_databasePath))
        {
            return null;
        }

        lock (_gate)
        {
            var snapshot = RestoreLatest(_databasePath, _worldId);
            return new LoadedWorld(snapshot, Read(_worldId));
        }
    }

    /// <summary>释放写连接。恢复路径使用独立的只读连接，不经过本实例。</summary>
    public void Dispose() => _connection.Dispose();

    private void InitializeSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            """;
        command.ExecuteNonQuery();

        long userVersion;
        using (var probe = _connection.CreateCommand())
        {
            probe.CommandText = "PRAGMA user_version;";
            var value = probe.ExecuteScalar();
            userVersion = value is null or DBNull ? 0 : Convert.ToInt64(value);
        }
        if (userVersion > SchemaVersion)
        {
            throw new InvalidDataException($"存档 schema 版本 {userVersion} 高于当前支持版本 {SchemaVersion}，拒绝读取。");
        }

        using var ddl = _connection.CreateCommand();
        ddl.CommandText = """
            CREATE TABLE IF NOT EXISTS world_meta (
                world_id                  TEXT    PRIMARY KEY,
                schema_version            INTEGER NOT NULL,
                current_world_version     INTEGER NOT NULL,
                current_commit_id         TEXT    NOT NULL,
                current_game_time_ticks   INTEGER NOT NULL,
                current_state_hash        TEXT    NOT NULL,
                current_payload_checksum  TEXT    NOT NULL,
                current_snapshot_seq      INTEGER NOT NULL,
                total_checksum            TEXT    NOT NULL
            );
            CREATE TABLE IF NOT EXISTS world_state (
                world_id        TEXT    NOT NULL,
                world_version   INTEGER NOT NULL,
                commit_id       TEXT    NOT NULL,
                game_time_ticks INTEGER NOT NULL,
                state_hash      TEXT    NOT NULL,
                state_blob      BLOB    NOT NULL,
                PRIMARY KEY (world_id, world_version)
            );
            CREATE TABLE IF NOT EXISTS event_journal (
                world_id       TEXT    NOT NULL,
                event_sequence INTEGER NOT NULL,
                event_id       TEXT    NOT NULL,
                event_blob     BLOB    NOT NULL,
                PRIMARY KEY (world_id, event_sequence)
            );
            CREATE TABLE IF NOT EXISTS snapshots (
                world_id         TEXT    NOT NULL,
                snapshot_seq     INTEGER NOT NULL,
                world_version    INTEGER NOT NULL,
                commit_id        TEXT    NOT NULL,
                state_hash       TEXT    NOT NULL,
                payload_checksum TEXT    NOT NULL,
                snapshot_blob    BLOB    NOT NULL,
                PRIMARY KEY (world_id, snapshot_seq)
            );
            CREATE TABLE IF NOT EXISTS command_outcomes (
                world_id      TEXT    NOT NULL,
                command_id    TEXT    NOT NULL,
                outcome_code  TEXT    NOT NULL,
                message       TEXT    NOT NULL,
                world_version INTEGER NOT NULL,
                PRIMARY KEY (world_id, command_id)
            );
            PRAGMA user_version=1;
            """;
        ddl.ExecuteNonQuery();
    }

    private void VerifyStagedFacetsMatchSnapshot(RealtimeSnapshot snapshot)
    {
        var snapshotState = SnapshotReflection.GetState(snapshot);
        if (_pendingState!.Id != snapshotState.Id ||
            _pendingState.WorldVersion != snapshotState.WorldVersion ||
            _pendingState.CommitId != snapshotState.CommitId ||
            _pendingState.GameTime != snapshotState.GameTime)
        {
            throw new InvalidOperationException("暂存的状态与快照不一致，拒绝提交。");
        }

        var snapshotOutbox = SnapshotReflection.GetOutboxEvents(snapshot);
        if (_pendingEvents!.Count != snapshotOutbox.Count ||
            !_pendingEvents.Select(item => item.EventSequence).SequenceEqual(snapshotOutbox.Select(item => item.EventSequence)))
        {
            throw new InvalidOperationException("暂存的事件日志与快照 outbox 不一致，拒绝提交。");
        }
    }

    private bool IsIdenticalLatestSnapshot(byte[] payload, long currentSnapshotSeq)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT snapshot_blob FROM snapshots WHERE world_id = $world AND snapshot_seq = $seq;";
        command.Parameters.AddWithValue("$world", _worldId.Value);
        command.Parameters.AddWithValue("$seq", currentSnapshotSeq);
        var latest = command.ExecuteScalar() as byte[];
        return latest is not null && payload.AsSpan().SequenceEqual(latest);
    }

    private void WriteStateRow(WorldState state, string stateHash)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO world_state (world_id, world_version, commit_id, game_time_ticks, state_hash, state_blob)
            VALUES ($world, $version, $commitId, $ticks, $hash, $blob);
            """;
        command.Parameters.AddWithValue("$world", state.Id.Value);
        command.Parameters.AddWithValue("$version", state.WorldVersion);
        command.Parameters.AddWithValue("$commitId", state.CommitId);
        command.Parameters.AddWithValue("$ticks", state.GameTime.Value.UtcTicks);
        command.Parameters.AddWithValue("$hash", stateHash);
        command.Parameters.AddWithValue("$blob", SnapshotCodec.SerializeWorld(state));
        command.ExecuteNonQuery();
    }

    private void AppendJournalDelta(IReadOnlyList<DomainEvent> stagedEvents)
    {
        var lastSequence = -1L;
        using (var lastCommand = _connection.CreateCommand())
        {
            lastCommand.CommandText = "SELECT MAX(event_sequence) FROM event_journal WHERE world_id = $world;";
            lastCommand.Parameters.AddWithValue("$world", _worldId.Value);
            var value = lastCommand.ExecuteScalar();
            if (value is not null && value is not DBNull)
            {
                lastSequence = Convert.ToInt64(value);
            }
        }

        var delta = stagedEvents.Where(item => item.EventSequence > lastSequence)
            .OrderBy(item => item.EventSequence).ToArray();
        if (delta.Length > 0 && delta[0].EventSequence != lastSequence + 1)
        {
            throw new InvalidDataException(
                $"事件日志出现缺号：库中最后序号 {lastSequence}，新增首条序号 {delta[0].EventSequence}。");
        }

        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO event_journal (world_id, event_sequence, event_id, event_blob)
            VALUES ($world, $sequence, $eventId, $blob);
            """;
        foreach (var domainEvent in delta)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$world", _worldId.Value);
            command.Parameters.AddWithValue("$sequence", domainEvent.EventSequence);
            command.Parameters.AddWithValue("$eventId", domainEvent.EventId);
            command.Parameters.AddWithValue("$blob", SnapshotCodec.SerializeEvent(domainEvent));
            command.ExecuteNonQuery();
        }
    }

    private long WriteSnapshotRow(byte[] payload, WorldState state, string stateHash, string payloadChecksum)
    {
        var snapshotSeq = -1L;
        using (var seqCommand = _connection.CreateCommand())
        {
            seqCommand.CommandText = "SELECT MAX(snapshot_seq) FROM snapshots WHERE world_id = $world;";
            seqCommand.Parameters.AddWithValue("$world", _worldId.Value);
            var value = seqCommand.ExecuteScalar();
            snapshotSeq = (value is null or DBNull ? 0L : Convert.ToInt64(value)) + 1;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO snapshots (world_id, snapshot_seq, world_version, commit_id, state_hash, payload_checksum, snapshot_blob)
            VALUES ($world, $seq, $version, $commitId, $hash, $checksum, $blob);
            """;
        command.Parameters.AddWithValue("$world", _worldId.Value);
        command.Parameters.AddWithValue("$seq", snapshotSeq);
        command.Parameters.AddWithValue("$version", state.WorldVersion);
        command.Parameters.AddWithValue("$commitId", state.CommitId);
        command.Parameters.AddWithValue("$hash", stateHash);
        command.Parameters.AddWithValue("$checksum", payloadChecksum);
        command.Parameters.AddWithValue("$blob", payload);
        command.ExecuteNonQuery();
        return snapshotSeq;
    }

    private void WriteMeta(WorldState state, string stateHash, string payloadChecksum, long snapshotSeq, string totalChecksum)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO world_meta (
                world_id, schema_version, current_world_version, current_commit_id,
                current_game_time_ticks, current_state_hash, current_payload_checksum,
                current_snapshot_seq, total_checksum)
            VALUES ($world, $schema, $version, $commitId, $ticks, $hash, $checksum, $seq, $total);
            """;
        command.Parameters.AddWithValue("$world", _worldId.Value);
        command.Parameters.AddWithValue("$schema", SchemaVersion);
        command.Parameters.AddWithValue("$version", state.WorldVersion);
        command.Parameters.AddWithValue("$commitId", state.CommitId);
        command.Parameters.AddWithValue("$ticks", state.GameTime.Value.UtcTicks);
        command.Parameters.AddWithValue("$hash", stateHash);
        command.Parameters.AddWithValue("$checksum", payloadChecksum);
        command.Parameters.AddWithValue("$seq", snapshotSeq);
        command.Parameters.AddWithValue("$total", totalChecksum);
        command.ExecuteNonQuery();
    }

    /// <summary>事务内回写 meta.total_checksum：CommitAll 先写占位 meta → 计算校验和 → 本方法回写真实值。</summary>
    private void UpdateTotalChecksum(string totalChecksum)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE world_meta SET total_checksum = $total WHERE world_id = $world;";
        command.Parameters.AddWithValue("$world", _worldId.Value);
        command.Parameters.AddWithValue("$total", totalChecksum);
        command.ExecuteNonQuery();
    }

    private void ClearPending()
    {
        _pendingState = null;
        _pendingEvents = null;
        _pendingSnapshot = null;
    }

    private MetaRow? ReadCurrentMeta()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT current_world_version, current_commit_id, current_snapshot_seq
            FROM world_meta WHERE world_id = $world;
            """;
        command.Parameters.AddWithValue("$world", _worldId.Value);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new MetaRow(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2));
    }

    private SnapshotPreparation? ReadCurrentPreparation()
    {
        var meta = ReadCurrentMeta();
        if (meta is null)
        {
            return null;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT s.snapshot_blob
            FROM snapshots AS s
            WHERE s.world_id = $world AND s.snapshot_seq = $seq;
            """;
        command.Parameters.AddWithValue("$world", _worldId.Value);
        command.Parameters.AddWithValue("$seq", meta.CurrentSnapshotSeq);
        var payload = command.ExecuteScalar() as byte[];
        if (payload is null)
        {
            throw new InvalidDataException("当前快照指针指向不存在的快照行。");
        }

        var snapshot = SnapshotCodec.Deserialize(payload);
        var state = SnapshotReflection.GetState(snapshot);
        var events = SnapshotReflection.GetOutboxEvents(snapshot);
        return new SnapshotPreparation(state.Id, state.TurnNumber, snapshot.StateHash, true, state, events);
    }

    /// <summary>覆盖本世界全部内容行的整库校验和（当前布局 v2：元数据列 + state_blob/event_blob/snapshot_blob 字节）。</summary>
    /// <remarks>
    /// 布局约定（提交与恢复必须完全一致）：meta 行的第 10 列固定为字面量 ''、第 11 列为零长 blob，
    /// 绝不写入 total_checksum 列本身——否则"恢复时用 meta 里的校验和去校验 meta"构成自引用，
    /// 未篡改的库也会因提交/恢复两侧布局不一致而永远失败（复审 P0）。CommitCore 先写占位
    /// meta、计算本哈希、再回写 total_checksum，恢复侧用同一布局重算比对。
    /// 为什么覆盖全部行而不是只覆盖当前行：恢复路径要求"篡改库中任一内容字节 → 恢复失败"，
    /// 历史行虽然不参与当前指针读取，但仍在库内，同样纳入校验。代价是恢复时 O(总行数)，
    /// MVP 规模可接受；日志量增长后应改为链式/分片校验（见 PR 剩余风险）。
    /// 为什么 v2 覆盖 blob 本体（P1-PERSIST-05）：旧布局只覆盖元数据列，"用另一份合法 blob
    /// 整体替换"不会被校验和发现（替换内容的内部哈希自洽，恢复会静默发布）；v2 把三个 blob
    /// 的原始字节纳入哈希，任何字节变化都会导致校验失配。v1 时代旧档（#35 之前写入）仍按
    /// <see cref="ComputeLegacyTotalChecksum"/> 的旧布局校验，保证迁移路径可读。
    /// </remarks>
    private string ComputeTotalChecksum() => ComputeTotalChecksumV2(_connection, _worldId);

    /// <summary>恢复侧重算整库校验和（v2 布局，含 blob 字节）：与提交侧完全一致。</summary>
    private static string ComputeTotalChecksumV2(SqliteConnection connection, WorldId worldId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'meta', world_id, schema_version, current_world_version, current_commit_id,
                   current_game_time_ticks, current_state_hash, current_payload_checksum,
                   current_snapshot_seq, '', x''
            FROM world_meta WHERE world_id = $world
            UNION ALL
            SELECT 'state', world_id, world_version, 0, commit_id, game_time_ticks, state_hash, '', 0, '', state_blob
            FROM world_state WHERE world_id = $world
            UNION ALL
            SELECT 'journal', world_id, event_sequence, 0, event_id, 0, '', '', 0, '', event_blob
            FROM event_journal WHERE world_id = $world
            UNION ALL
            SELECT 'snapshot', world_id, snapshot_seq, 0, commit_id, 0, state_hash, payload_checksum, 0, '', snapshot_blob
            FROM snapshots WHERE world_id = $world;
            """;
        command.Parameters.AddWithValue("$world", worldId.Value);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(ChecksumMagicV2);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            writer.Write(reader.GetString(0));
            writer.Write(reader.GetString(1));
            writer.Write(reader.GetInt64(2));
            writer.Write(reader.GetInt64(3));
            writer.Write(reader.GetString(4));
            writer.Write(reader.GetInt64(5));
            writer.Write(reader.GetString(6));
            writer.Write(reader.GetString(7));
            writer.Write(reader.GetInt64(8));
            writer.Write(reader.GetString(9));
            var blob = reader.GetFieldValue<byte[]>(10);
            writer.Write(blob.Length);
            writer.Write(blob);
        }

        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    /// <summary>
    /// v1 时代整库校验和（#35 之前写入的旧档布局）：只覆盖元数据列、不含任何 blob 字节。
    /// 恢复侧在 v2 失配时按本布局重算比对——真实旧档仍可读取并走迁移路径（fail-open 仅限
    /// 于"校验和布局可验证为旧版"这一种情况；其余任何失配都 fail-closed）。
    /// </summary>
    private static string ComputeLegacyTotalChecksum(SqliteConnection connection, WorldId worldId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'meta', world_id, schema_version, current_world_version, current_commit_id,
                   current_game_time_ticks, current_state_hash, current_payload_checksum,
                   current_snapshot_seq, ''
            FROM world_meta WHERE world_id = $world
            UNION ALL
            SELECT 'state', world_id, world_version, 0, commit_id, game_time_ticks, state_hash, '', 0, ''
            FROM world_state WHERE world_id = $world
            UNION ALL
            SELECT 'journal', world_id, event_sequence, 0, event_id, 0, '', '', 0, ''
            FROM event_journal WHERE world_id = $world
            UNION ALL
            SELECT 'snapshot', world_id, snapshot_seq, 0, commit_id, 0, state_hash, payload_checksum, 0, ''
            FROM snapshots WHERE world_id = $world;
            """;
        command.Parameters.AddWithValue("$world", worldId.Value);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(ChecksumMagicV1);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            writer.Write(reader.GetString(0));
            writer.Write(reader.GetString(1));
            writer.Write(reader.GetInt64(2));
            writer.Write(reader.GetInt64(3));
            writer.Write(reader.GetString(4));
            writer.Write(reader.GetInt64(5));
            writer.Write(reader.GetString(6));
            writer.Write(reader.GetString(7));
            writer.Write(reader.GetInt64(8));
            writer.Write(reader.GetString(9));
        }

        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static byte[] ReadSnapshotPayload(SqliteConnection connection, WorldId worldId, long snapshotSeq)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_blob FROM snapshots WHERE world_id = $world AND snapshot_seq = $seq;";
        command.Parameters.AddWithValue("$world", worldId.Value);
        command.Parameters.AddWithValue("$seq", snapshotSeq);
        return command.ExecuteScalar() as byte[]
            ?? throw new InvalidDataException("当前快照指针指向不存在的快照行。");
    }

    private static void VerifyJournalContinuity(SqliteConnection connection, WorldId worldId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_sequence, event_id FROM event_journal
            WHERE world_id = $world ORDER BY event_sequence;
            """;
        command.Parameters.AddWithValue("$world", worldId.Value);
        using var reader = command.ExecuteReader();
        var expected = 0L;
        while (reader.Read())
        {
            var actual = reader.GetInt64(0);
            if (actual != expected)
            {
                throw new InvalidDataException(
                    $"事件日志序号不连续：期望 {expected}，实际 {actual}（事件 {reader.GetString(1)}）。");
            }

            expected++;
        }
    }

    private static void VerifyJournalMatchesSnapshot(
        SqliteConnection connection, WorldId worldId, RealtimeSnapshot snapshot, bool allowJournalLongerThanOutbox)
    {
        var outbox = SnapshotReflection.GetOutboxEvents(snapshot);
        if (outbox.Count == 0)
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM event_journal WHERE world_id = $world;";
        command.Parameters.AddWithValue("$world", worldId.Value);
        var count = Convert.ToInt64(command.ExecuteScalar());
        if (allowJournalLongerThanOutbox)
        {
            // 回退路径：恢复的是旧 READY 快照，事件日志允许比其 outbox 更长
            //（快照之后提交的、快照已损坏的事件仍在审计日志中）；outbox 必须是日志前缀。
            if (count < outbox.Count)
            {
                throw new InvalidDataException("事件日志比回退快照的 outbox 还短，存档损坏。");
            }
        }
        else if (count != outbox.Count)
        {
            throw new InvalidDataException("事件日志与快照 outbox 数量不一致，存档损坏。");
        }

        for (var index = 0; index < outbox.Count; index++)
        {
            if (outbox[index].EventSequence != index)
            {
                throw new InvalidDataException("事件日志与快照 outbox 序号不一致，存档损坏。");
            }
        }

        // 真正的日志/outbox 交叉校验（独立审查 P2-1 + P2-09）：按 EventSequence 读回日志中与 outbox
        // 同序号的每条事件，逐条与快照 outbox 比对 EventSequence/EventId/完整事件内容
        // （解码后的规范化字节逐字节一致，覆盖 WorldId/EventType/Data/OccurredAt/WorldVersion/CommitId/
        // CausalCommandId 等全部字段）——仅 count + 序号连续校验不足以证明 outbox 确实是日志前缀，
        // 事件内容被替换/错位必须在这里暴露。
        using (var crossCommand = connection.CreateCommand())
        {
            crossCommand.CommandText = """
                SELECT event_blob, event_sequence
                FROM event_journal
                WHERE world_id = $world AND event_sequence < $count
                ORDER BY event_sequence;
                """;
            crossCommand.Parameters.AddWithValue("$world", worldId.Value);
            crossCommand.Parameters.AddWithValue("$count", outbox.Count);
            using var crossReader = crossCommand.ExecuteReader();
            var index = 0;
            while (crossReader.Read())
            {
                var journalSequence = crossReader.GetInt64(1);
                var journalEvent = SnapshotCodec.DeserializeEvent(crossReader.GetFieldValue<byte[]>(0));
                var outboxEvent = outbox[index];
                if (journalSequence != index ||
                    journalEvent.EventSequence != index ||
                    !StringComparer.Ordinal.Equals(journalEvent.EventId, outboxEvent.EventId) ||
                    !SnapshotCodec.SerializeEvent(journalEvent).AsSpan()
                        .SequenceEqual(SnapshotCodec.SerializeEvent(outboxEvent)))
                {
                    throw new InvalidDataException(
                        $"事件日志与快照 outbox 在第 {index} 条事件处不一致（EventSequence/EventId/事件内容），存档损坏。");
                }

                index++;
            }

            if (index != outbox.Count)
            {
                throw new InvalidDataException("事件日志与快照 outbox 数量不一致，存档损坏。");
            }
        }
    }

    /// <summary>读取完整 meta 行（恢复与快速加载用；不含行则返回 null）。</summary>
    private static MetaRowFull? ReadMetaRow(SqliteConnection connection, WorldId worldId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT world_id, current_world_version, current_commit_id, current_game_time_ticks,
                   current_state_hash, current_payload_checksum, current_snapshot_seq, total_checksum
            FROM world_meta WHERE world_id = $world;
            """;
        command.Parameters.AddWithValue("$world", worldId.Value);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new MetaRowFull(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetString(7));
    }

    /// <summary>读取快照行的身份字段（恢复交叉验证用；不含行则返回 null）。</summary>
    private static SnapshotRow? ReadSnapshotRow(SqliteConnection connection, WorldId worldId, long snapshotSeq)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT world_version, commit_id, state_hash, payload_checksum
            FROM snapshots WHERE world_id = $world AND snapshot_seq = $seq;
            """;
        command.Parameters.AddWithValue("$world", worldId.Value);
        command.Parameters.AddWithValue("$seq", snapshotSeq);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SnapshotRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    /// <summary>meta 行完整字段（恢复路径的交叉校验基准）。</summary>
    private sealed record MetaRowFull(
        string WorldId,
        long CurrentWorldVersion,
        string CurrentCommitId,
        long CurrentGameTimeTicks,
        string CurrentStateHash,
        string CurrentPayloadChecksum,
        long CurrentSnapshotSeq,
        string TotalChecksum);

    /// <summary>快照行身份字段（回退候选的交叉校验基准）。</summary>
    private sealed record SnapshotRow(long WorldVersion, string CommitId, string StateHash, string PayloadChecksum);

    private sealed record MetaRow(long CurrentWorldVersion, string CurrentCommitId, long CurrentSnapshotSeq);
}
