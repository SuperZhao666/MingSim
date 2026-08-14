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
public sealed class SqliteCommitStore : IWorldStore, IAuditJournal, ISnapshotStore, IDisposable
{
    /// <summary>SQLite schema 版本；schema 变更必须迁移而不是原地改表。</summary>
    private const int SchemaVersion = 1;

    private const string ChecksumMagic = "mingsim-commit-v1";

    private readonly SqliteConnection _connection;
    private readonly WorldId _worldId;
    private readonly object _gate = new();
    private WorldState? _pendingState;
    private IReadOnlyList<DomainEvent>? _pendingEvents;
    private SnapshotPreparation? _pendingSnapshot;

    public SqliteCommitStore(string databasePath, WorldId worldId)
    {
        ArgumentNullException.ThrowIfNull(databasePath);
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

    /// <summary>IWorldStore：读取当前已提交状态（快速加载路径；完整校验在恢复路径）。</summary>
    public WorldState Load(WorldId worldId)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT ws.state_blob, m.current_world_version
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
            if (state.Id != worldId)
            {
                throw new InvalidDataException("状态行中的世界编号与请求不一致。");
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
    /// 单事务原子提交：当前状态 + 事件日志增量 + 校验快照（含指针切换）在同一个
    /// BEGIN IMMEDIATE ... COMMIT 中完成；任何一步失败整体回滚，数据库保持上一个提交。
    /// </summary>
    /// <param name="snapshot">Runtime 原子捕获的完整快照；其规范化字节写入快照表，
    /// StateHash/PayloadChecksum 写入清单，供崩溃后完整恢复与校验。</param>
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

            VerifyStagedFacetsMatchSnapshot(snapshot);
            var payload = SnapshotCodec.Serialize(snapshot);
            var stateHash = snapshot.StateHash;
            var payloadChecksum = snapshot.PayloadChecksum;

            using var transaction = _connection.BeginTransaction(deferred: false);
            try
            {
                var current = ReadCurrentMeta();
                var currentVersion = current?.CurrentWorldVersion ?? -1;
                if (_pendingState.WorldVersion < currentVersion)
                {
                    throw new InvalidOperationException(
                        $"提交版本回退：库中当前版本 {currentVersion}，试图写入 {_pendingState.WorldVersion}。");
                }

                var isIdenticalRecommit = current is not null &&
                    _pendingState.WorldVersion == currentVersion &&
                    IsIdenticalLatestSnapshot(payload, current!.CurrentSnapshotSeq);
                if (isIdenticalRecommit)
                {
                    // 幂等重提交：同一版本且快照字节完全相同，什么都不写，直接结束本次提交。
                    transaction.Commit();
                    return;
                }

                WriteStateRow(_pendingState!, stateHash);
                AppendJournalDelta(_pendingEvents!);
                var snapshotSeq = WriteSnapshotRow(payload, _pendingState!, stateHash, payloadChecksum);
                var totalChecksum = ComputeTotalChecksum();
                WriteMeta(_pendingState, stateHash, payloadChecksum, snapshotSeq, totalChecksum);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
            finally
            {
                ClearPending();
            }

            // COMMIT 之后做一次显式 checkpoint，让 .db 主文件自洽（等价于导出存档前的安全状态）。
            using (var checkpoint = _connection.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                checkpoint.ExecuteNonQuery();
            }
        }
    }

    /// <summary>
    /// 崩溃恢复：只读打开库，重算覆盖全部内容行的校验和，校验事件日志连续性，
    /// 返回最新已提交快照。任何字节篡改都会抛异常，绝不返回半状态。
    /// 返回的快照仍需交给 <see cref="RealtimeSimulationRuntime.Restore"/> 做权威校验后才能使用。
    /// </summary>
    public static RealtimeSnapshot RestoreLatest(string databasePath, WorldId worldId)
    {
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("存档数据库不存在。", databasePath);
        }

        // Pooling=false：恢复是只读的一次性操作，Dispose 后必须立即释放文件句柄（与写路径一致）。
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=false");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT world_id, current_world_version, current_snapshot_seq, total_checksum FROM world_meta WHERE world_id = $world;";
            command.Parameters.AddWithValue("$world", worldId.Value);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidDataException($"世界 {worldId} 没有已提交的提交记录。");
            }

            var storedWorldId = reader.GetString(0);
            var currentWorldVersion = reader.GetInt64(1);
            var currentSnapshotSeq = reader.GetInt64(2);
            var storedChecksum = reader.GetString(3);
            if (storedWorldId != worldId.Value)
            {
                throw new InvalidDataException("存档中的世界编号与请求不一致。");
            }

            var payload = ReadSnapshotPayload(connection, worldId, currentSnapshotSeq);
            var actualChecksum = ComputeTotalChecksum(connection, worldId);
            if (!StringComparer.Ordinal.Equals(actualChecksum, storedChecksum))
            {
                throw new InvalidDataException("存档内容校验失败：数据库可能被篡改或损坏。");
            }

            VerifyJournalContinuity(connection, worldId);
            var snapshot = SnapshotCodec.Deserialize(payload);
            VerifyJournalMatchesSnapshot(connection, worldId, snapshot);
            return snapshot;
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

    /// <summary>覆盖本世界全部内容行的校验和：meta + 所有状态行 + 所有日志行 + 所有快照行。</summary>
    /// <remarks>
    /// 为什么覆盖全部行而不是只覆盖当前行：恢复路径要求"篡改库中任一内容字节 → 恢复失败"，
    /// 历史行虽然不参与当前指针读取，但仍在库内，同样纳入校验。代价是恢复时 O(总行数)，
    /// MVP 规模可接受；日志量增长后应改为链式/分片校验（见 PR 剩余风险）。
    /// </remarks>
    private string ComputeTotalChecksum()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT 'meta', world_id, schema_version, current_world_version, current_commit_id,
                   current_game_time_ticks, current_state_hash, current_payload_checksum,
                   current_snapshot_seq, total_checksum
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
        command.Parameters.AddWithValue("$world", _worldId.Value);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(ChecksumMagic);
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

    private static string ComputeTotalChecksum(SqliteConnection connection, WorldId worldId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'meta', world_id, schema_version, current_world_version, current_commit_id,
                   current_game_time_ticks, current_state_hash, current_payload_checksum,
                   current_snapshot_seq, total_checksum
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
        writer.Write(ChecksumMagic);
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

    private static void VerifyJournalMatchesSnapshot(SqliteConnection connection, WorldId worldId, RealtimeSnapshot snapshot)
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
        if (count != outbox.Count)
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
    }

    private sealed record MetaRow(long CurrentWorldVersion, string CurrentCommitId, long CurrentSnapshotSeq);
}
