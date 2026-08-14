using System.Reflection;
using MingSim.Domain;
using MingSim.Domain.Characters;
using MingSim.Domain.Economy;
using MingSim.Domain.Events;
using MingSim.Domain.Military;
using MingSim.Domain.Realtime;
using MingSim.Simulation.Realtime;

namespace MingSim.Persistence.Sqlite;

/// <summary>
/// RealtimeSnapshot 与 WorldState 的反射序列化桥。
/// </summary>
/// <remarks>
/// 为什么需要反射：RealtimeSnapshot 的属性、WorldState 的时间/版本写入口在 Domain 与 Simulation
/// 中都是 internal/private——这正是"WorldState 仅 Simulation 可写"红线的编译期表现。SQLite 层只承担
/// 序列化/反序列化与恢复校验，不拥有领域写权限；通过反射读写这些内部成员不会新增任何可写入口，
/// 外部调用方仍然只能通过 Simulation 的公开 API 修改世界。
/// 所有反射元数据在静态构造时缓存一次，避免每次提交都重新查找，也便于集中审查"访问了哪些内部成员"。
/// 注意：internal/private 成员不能使用 nameof（编译期不可见），这里统一用字符串字面量并集中列明。
/// </remarks>
internal static class SnapshotReflection
{
    private static readonly Type SnapshotType = typeof(RealtimeSnapshot);
    private static readonly ConstructorInfo SnapshotCtor = SnapshotType.GetConstructors(
        BindingFlags.Instance | BindingFlags.NonPublic).Single();

    private static readonly PropertyInfo StateProperty = GetInternalProperty("State");
    private static readonly PropertyInfo ScheduledEventsProperty = GetInternalProperty("ScheduledEvents");
    private static readonly PropertyInfo PendingCommandsProperty = GetInternalProperty("PendingCommands");
    private static readonly PropertyInfo NextCreationSequenceProperty = GetInternalProperty("NextCreationSequence");
    private static readonly PropertyInfo NextIngressSequenceProperty = GetInternalProperty("NextIngressSequence");
    private static readonly PropertyInfo CommandOutcomesProperty = GetInternalProperty("CommandOutcomes");
    private static readonly PropertyInfo RandomStateProperty = GetInternalProperty("RandomState");
    private static readonly PropertyInfo OutboxEventsProperty = GetInternalProperty("OutboxEvents");
    private static readonly PropertyInfo RealGameTickRemainderProperty = GetInternalProperty("RealGameTickRemainder");
    private static readonly PropertyInfo InitialGameTimeProperty = GetInternalProperty("InitialGameTime");
    private static readonly PropertyInfo InitialWorldVersionProperty = GetInternalProperty("InitialWorldVersion");
    private static readonly PropertyInfo ProcessedScheduledEventCountProperty = GetInternalProperty("ProcessedScheduledEventCount");
    private static readonly PropertyInfo IsPausedProperty = GetInternalProperty("IsPaused");
    private static readonly PropertyInfo SpeedProperty = GetInternalProperty("Speed");
    private static readonly PropertyInfo NextEventSequenceProperty = GetInternalProperty("NextEventSequence");

    private static readonly PropertyInfo WorldGameTimeProperty = typeof(WorldState).GetProperty(
        nameof(WorldState.GameTime), BindingFlags.Instance | BindingFlags.Public)!;
    private static readonly PropertyInfo WorldVersionProperty = typeof(WorldState).GetProperty(
        nameof(WorldState.WorldVersion), BindingFlags.Instance | BindingFlags.Public)!;
    private static readonly PropertyInfo WorldCommitIdProperty = typeof(WorldState).GetProperty(
        nameof(WorldState.CommitId), BindingFlags.Instance | BindingFlags.Public)!;
    private static readonly MethodInfo WorldSetMovementMethod = typeof(WorldState).GetMethod(
        "SetMovement", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly PropertyInfo CharacterLoyaltyProperty = typeof(CharacterState).GetProperty(
        nameof(CharacterState.Loyalty), BindingFlags.Instance | BindingFlags.Public)!;
    private static readonly PropertyInfo CharacterStressProperty = typeof(CharacterState).GetProperty(
        nameof(CharacterState.Stress), BindingFlags.Instance | BindingFlags.Public)!;
    private static readonly MethodInfo CharacterRememberMethod = typeof(CharacterState).GetMethod(
        "Remember", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly PropertyInfo ShipmentStatusProperty = typeof(ShipmentState).GetProperty(
        nameof(ShipmentState.Status), BindingFlags.Instance | BindingFlags.Public)!;
    private static readonly PropertyInfo ShipmentDepartedAtProperty = typeof(ShipmentState).GetProperty(
        nameof(ShipmentState.DepartedAt), BindingFlags.Instance | BindingFlags.Public)!;
    private static readonly PropertyInfo ShipmentArrivedAtProperty = typeof(ShipmentState).GetProperty(
        nameof(ShipmentState.ArrivedAt), BindingFlags.Instance | BindingFlags.Public)!;
    private static readonly PropertyInfo ShipmentDeliveredProperty = typeof(ShipmentState).GetProperty(
        nameof(ShipmentState.DeliveredGrain), BindingFlags.Instance | BindingFlags.Public)!;
    private static readonly PropertyInfo ShipmentLossProperty = typeof(ShipmentState).GetProperty(
        nameof(ShipmentState.LossGrain), BindingFlags.Instance | BindingFlags.Public)!;
    private static readonly MethodInfo LogisticsAddShipmentMethod = typeof(LogisticsState).GetMethod(
        "AddShipment", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly PropertyInfo FacilityStatusProperty = typeof(FacilityState).GetProperty(
        nameof(FacilityState.Status), BindingFlags.Instance | BindingFlags.Public)!;
    private static readonly PropertyInfo FacilityProducedProperty = typeof(FacilityState).GetProperty(
        nameof(FacilityState.ProducedThisTurn), BindingFlags.Instance | BindingFlags.Public)!;
    private static readonly MethodInfo IndustryAddMethod = typeof(IndustryState).GetMethod(
        "Add", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo ArmyAddTrainingDaysMethod = typeof(ArmyState).GetMethod(
        "AddTrainingDays", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static PropertyInfo GetInternalProperty(string name) =>
        SnapshotType.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMemberException(SnapshotType.FullName, name);

    private static T Read<T>(RealtimeSnapshot snapshot, PropertyInfo property) =>
        (T)(property.GetValue(snapshot) ?? throw new InvalidDataException($"快照字段 {property.Name} 为空。"));

    /// <summary>读取快照的世界状态。</summary>
    public static WorldState GetState(RealtimeSnapshot snapshot) => Read<WorldState>(snapshot, StateProperty);

    /// <summary>读取快照的调度队列。</summary>
    public static IReadOnlyList<ScheduledSimulationEvent> GetScheduledEvents(RealtimeSnapshot snapshot) =>
        Read<IReadOnlyList<ScheduledSimulationEvent>>(snapshot, ScheduledEventsProperty);

    /// <summary>读取快照的待处理收件箱命令。</summary>
    public static IReadOnlyList<RealtimeCommand> GetPendingCommands(RealtimeSnapshot snapshot) =>
        Read<IReadOnlyList<RealtimeCommand>>(snapshot, PendingCommandsProperty);

    public static long GetNextCreationSequence(RealtimeSnapshot snapshot) => Read<long>(snapshot, NextCreationSequenceProperty);

    public static long GetNextIngressSequence(RealtimeSnapshot snapshot) => Read<long>(snapshot, NextIngressSequenceProperty);

    /// <summary>读取命令终态记录。</summary>
    public static IReadOnlyList<CommandOutcome> GetCommandOutcomes(RealtimeSnapshot snapshot) =>
        Read<IReadOnlyList<CommandOutcome>>(snapshot, CommandOutcomesProperty);

    public static string GetRandomState(RealtimeSnapshot snapshot) => Read<string>(snapshot, RandomStateProperty);

    /// <summary>读取完整事件日志（outbox，即 EventJournal 的内存形态）。</summary>
    public static IReadOnlyList<DomainEvent> GetOutboxEvents(RealtimeSnapshot snapshot) =>
        Read<IReadOnlyList<DomainEvent>>(snapshot, OutboxEventsProperty);

    public static decimal GetRealGameTickRemainder(RealtimeSnapshot snapshot) => Read<decimal>(snapshot, RealGameTickRemainderProperty);

    public static GameTime GetInitialGameTime(RealtimeSnapshot snapshot) => Read<GameTime>(snapshot, InitialGameTimeProperty);

    public static long GetInitialWorldVersion(RealtimeSnapshot snapshot) => Read<long>(snapshot, InitialWorldVersionProperty);

    public static long GetProcessedScheduledEventCount(RealtimeSnapshot snapshot) => Read<long>(snapshot, ProcessedScheduledEventCountProperty);

    public static bool GetIsPaused(RealtimeSnapshot snapshot) => Read<bool>(snapshot, IsPausedProperty);

    public static double GetSpeed(RealtimeSnapshot snapshot) => Read<double>(snapshot, SpeedProperty);

    public static long GetNextEventSequence(RealtimeSnapshot snapshot) => Read<long>(snapshot, NextEventSequenceProperty);

    /// <summary>
    /// 通过内部构造函数重建 RealtimeSnapshot。
    /// 为什么用反射：构造函数是 internal（快照身份只能由 Runtime 创建），SQLite 层作为
    /// 反序列化边界需要重建它，但不会绕过 Runtime 的 Restore 校验——调用方仍必须调用
    /// <see cref="RealtimeSimulationRuntime.Restore"/> 才能得到可运行实例。
    /// </summary>
    public static RealtimeSnapshot CreateSnapshot(
        int schemaVersion,
        WorldState state,
        IReadOnlyList<ScheduledSimulationEvent> scheduledEvents,
        IReadOnlyList<RealtimeCommand> pendingCommands,
        long nextCreationSequence,
        long nextIngressSequence,
        IReadOnlyList<CommandOutcome> commandOutcomes,
        string randomState,
        IReadOnlyList<DomainEvent> outboxEvents,
        decimal realGameTickRemainder,
        string stateHash,
        string payloadChecksum,
        GameTime initialGameTime,
        long initialWorldVersion,
        long processedScheduledEventCount,
        bool isPaused,
        double speed,
        long nextEventSequence) =>
        (RealtimeSnapshot)SnapshotCtor.Invoke([
            schemaVersion,
            state,
            scheduledEvents,
            pendingCommands,
            nextCreationSequence,
            nextIngressSequence,
            commandOutcomes,
            randomState,
            outboxEvents,
            realGameTickRemainder,
            stateHash,
            payloadChecksum,
            initialGameTime,
            initialWorldVersion,
            processedScheduledEventCount,
            isPaused,
            speed,
            nextEventSequence,
        ]);

    /// <summary>
    /// 用重新计算的 StateHash/PayloadChecksum 重建同一内容的快照（迁移 re-seal）。
    /// 快照内容（世界、调度、收件箱、outbox、序列等）原样保留，只替换两个校验字段；
    /// 快照 schemaVersion 归一化为当前 <see cref="RealtimeSnapshotSchema.Version"/>——迁移重建的
    /// 对象就是当前布局（旧档携带的旧 schema 编号不再适用，否则 Runtime.Restore 的版本门禁会拒绝）。
    /// </summary>
    public static RealtimeSnapshot Reseal(RealtimeSnapshot snapshot, string stateHash, string payloadChecksum) =>
        CreateSnapshot(
            RealtimeSnapshotSchema.Version,
            GetState(snapshot),
            GetScheduledEvents(snapshot),
            GetPendingCommands(snapshot),
            GetNextCreationSequence(snapshot),
            GetNextIngressSequence(snapshot),
            GetCommandOutcomes(snapshot),
            GetRandomState(snapshot),
            GetOutboxEvents(snapshot),
            GetRealGameTickRemainder(snapshot),
            stateHash,
            payloadChecksum,
            GetInitialGameTime(snapshot),
            GetInitialWorldVersion(snapshot),
            GetProcessedScheduledEventCount(snapshot),
            GetIsPaused(snapshot),
            GetSpeed(snapshot),
            GetNextEventSequence(snapshot));

    /// <summary>
    /// 反序列化重建 WorldState 后，把时间、版本、CommitId 写回。
    /// WorldVersion/CommitId 是 private set，GameTime 由 private set 持有；这些写入口
    /// 在 Domain 中不公开，这里仅用于把规范化字节恢复成与提交时完全相同的对象。
    /// </summary>
    public static void SetWorldCommitState(WorldState world, GameTime gameTime, long worldVersion, string commitId)
    {
        WorldGameTimeProperty.SetValue(world, gameTime);
        WorldVersionProperty.SetValue(world, worldVersion);
        WorldCommitIdProperty.SetValue(world, commitId);
    }

    /// <summary>把军队行军状态恢复到 WorldState（internal SetMovement）。</summary>
    public static void AddMovement(WorldState world, MovementState movement) =>
        WorldSetMovementMethod.Invoke(world, [movement]);

    /// <summary>恢复角色忠诚/压力（private set）与私有记忆（internal Remember）。</summary>
    public static void SetCharacterDetails(CharacterState character, int loyalty, int stress, IReadOnlyList<MemoryNote> memories)
    {
        CharacterLoyaltyProperty.SetValue(character, loyalty);
        CharacterStressProperty.SetValue(character, stress);
        foreach (var memory in memories)
        {
            CharacterRememberMethod.Invoke(character, [memory]);
        }
    }

    /// <summary>恢复运输单的终态字段（private set，Clone 之外没有公开写入口）。</summary>
    public static void SetShipmentCompletion(ShipmentState shipment, ShipmentStatus status, GameTime? departedAt, GameTime? arrivedAt, long delivered, long loss)
    {
        ShipmentStatusProperty.SetValue(shipment, status);
        ShipmentDepartedAtProperty.SetValue(shipment, departedAt);
        ShipmentArrivedAtProperty.SetValue(shipment, arrivedAt);
        ShipmentDeliveredProperty.SetValue(shipment, delivered);
        ShipmentLossProperty.SetValue(shipment, loss);
    }

    /// <summary>恢复运输单到物流账本（internal AddShipment）。</summary>
    public static void AddShipment(LogisticsState logistics, ShipmentState shipment) =>
        LogisticsAddShipmentMethod.Invoke(logistics, [shipment]);

    /// <summary>恢复工坊状态（internal Add）与 private set 字段。</summary>
    public static void AddFacility(IndustryState industry, FacilityState facility, FacilityStatus status, long producedThisTurn)
    {
        FacilityStatusProperty.SetValue(facility, status);
        FacilityProducedProperty.SetValue(facility, producedThisTurn);
        IndustryAddMethod.Invoke(industry, [facility]);
    }

    /// <summary>恢复军队训练天数（internal AddTrainingDays，无公开 setter）。</summary>
    public static void SetArmyTrainingDays(ArmyState army, int trainingDays)
    {
        if (trainingDays > 0)
        {
            ArmyAddTrainingDaysMethod.Invoke(army, [trainingDays]);
        }
    }
}
