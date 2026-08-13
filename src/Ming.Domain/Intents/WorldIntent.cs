using MingSim.Domain.Common;
using MingSim.Domain.Economy;

namespace MingSim.Domain.Intents;

/// <summary>
/// 所有“想改变世界”的请求的共同基类。
/// </summary>
/// <remarks>
/// Intent 可以来自玩家、规则 AI 或 LLM，但它本身还不是结果。
/// 它必须经过权限、前置条件、资源和不变量检查，才能进入正式世界状态。
/// </remarks>
public abstract record WorldIntent(
    string IntentId,
    CharacterId ActorId,
    int ExpectedTurn,
    string IdempotencyKey,
    string? SourceDecreeId = null);

/// <summary>请求兴建一座工坊。</summary>
public sealed record BuildFacilityIntent(
    string IntentId,
    CharacterId ActorId,
    int ExpectedTurn,
    string IdempotencyKey,
    FacilityId FacilityId,
    ProvinceId LocationId,
    FacilityType FacilityType,
    long Budget,
    long BaseCapacity,
    int Workforce,
    string? SourceDecreeId = null)
    : WorldIntent(IntentId, ActorId, ExpectedTurn, IdempotencyKey, SourceDecreeId);

/// <summary>请求把一部分辅兵改编为列装步兵。</summary>
public sealed record ConvertArmyIntent(
    string IntentId,
    CharacterId ActorId,
    int ExpectedTurn,
    string IdempotencyKey,
    ArmyId ArmyId,
    long Count,
    string EquipmentType = "flintlock",
    string? SourceDecreeId = null)
    : WorldIntent(IntentId, ActorId, ExpectedTurn, IdempotencyKey, SourceDecreeId);

/// <summary>请求让一支军队进行一段时间训练。</summary>
public sealed record TrainArmyIntent(
    string IntentId,
    CharacterId ActorId,
    int ExpectedTurn,
    string IdempotencyKey,
    ArmyId ArmyId,
    int Days,
    long Budget,
    string? SourceDecreeId = null)
    : WorldIntent(IntentId, ActorId, ExpectedTurn, IdempotencyKey, SourceDecreeId);
