using MingSim.Domain.Common;
using MingSim.Domain.Intents;

namespace MingSim.Agents.Realtime;

/// <summary>
/// Agent 提出的粮运意图：只声明路线与数量，不携带任何可执行结果。
/// </summary>
/// <remarks>
/// 这是"受限命令意图"：ActorId 由决策管线绑定，IdempotencyKey 由调用方提供并
/// 作为内核稳定命令编号；入口只做权限预检和命令转换，真正的资源校验在内核安全点完成。
/// ExpectedWorldVersion 是决策时观察到的世界版本，过期版本会被内核以
/// STATE_VERSION_CONFLICT 拒绝，绝不"适配一下"悄悄执行。
/// </remarks>
public sealed record PlanLogisticsIntent(
    string IntentId,
    CharacterId ActorId,
    int ExpectedTurn,
    string IdempotencyKey,
    long ExpectedWorldVersion,
    RouteId RouteId,
    long GrainQuantity,
    DateTimeOffset SubmittedAt,
    string? SourceDecreeId = null)
    : WorldIntent(IntentId, ActorId, ExpectedTurn, IdempotencyKey, SourceDecreeId);

/// <summary>
/// Agent 提出的行军意图：命令一支军队向相邻省份行军。
/// </summary>
public sealed record MoveArmyIntent(
    string IntentId,
    CharacterId ActorId,
    int ExpectedTurn,
    string IdempotencyKey,
    long ExpectedWorldVersion,
    ArmyId ArmyId,
    ProvinceId DestinationId,
    DateTimeOffset SubmittedAt,
    int TravelHours = 24,
    string? SourceDecreeId = null)
    : WorldIntent(IntentId, ActorId, ExpectedTurn, IdempotencyKey, SourceDecreeId);
