using MingSim.Agents.Providers;
using MingSim.Agents.Runtime;
using MingSim.Domain.Intents;
using MingSim.Domain.Realtime;

namespace MingSim.Agents.Decision;

/// <summary>
/// 按需决策规划器：模型路径可选，规则（Utility AI）路径是默认回退（ADR-006）。
/// </summary>
/// <remarks>
/// 最小版人物决策流水线（doc 07 §3）：
/// 1. 配置了 IModelProvider 时，先尝试让模型产出白名单意图 JSON；
/// 2. 解析成功且结果未过期（AcceptedGameTime &lt; Deadline）→ 采用模型意图；
/// 3. 解析失败、模型失败/超时或结果已过期 → 一律丢弃模型结果，回退规则决策。
///
/// 不配置 Provider（provider 为 null）时 0 次模型调用即可完整决策；
/// 模型故障只影响本次可选的模型路径，不会暂停世界主循环（doc 07 §13.4）。
/// 本类只产出意图，不提交：Agent 改写世界的唯一通道是 AgentRealtimeEntry。
/// </remarks>
public sealed class DecisionPlanner
{
    // 给模型的输出约束：只允许白名单内的两种意图；解析器按同一白名单做硬校验，
    // 因此这里的 schema 只是提示，真正的防线在 ModelDecisionParser。
    private const string WhitelistSchema = """
        {
          "type": "object",
          "properties": {
            "schema_version": { "type": "integer" },
            "intent_type": { "type": "string", "enum": ["logistics.request_shipment", "military.move_army"] },
            "parameters": { "type": "object" }
          },
          "required": ["schema_version", "intent_type", "parameters"]
        }
        """;

    private readonly IAgentDecisionSource _ruleSource;
    private readonly IModelProvider? _provider;
    private readonly ModelDecisionParser _parser = new();

    /// <summary>
    /// 创建规划器。ruleSource 是规则回退（通常是 Utility AI）；
    /// provider 为空表示关闭模型路径，始终走规则决策。
    /// </summary>
    public DecisionPlanner(IAgentDecisionSource ruleSource, IModelProvider? provider = null)
    {
        _ruleSource = ruleSource ?? throw new ArgumentNullException(nameof(ruleSource));
        _provider = provider;
    }

    /// <summary>
    /// 完成一次决策：模型结果有效且未过期则采用，否则回退规则路径。
    /// </summary>
    /// <param name="acceptedGameTime">结果被世界接受时的权威游戏时刻；用于半开区间过期判定。</param>
    public async Task<DecisionResult> PlanAsync(
        DecisionRequest request,
        AgentContext context,
        GameTime acceptedGameTime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (_provider is not null)
        {
            var modelIntents = await TryParseModelIntentsAsync(request, context, acceptedGameTime, cancellationToken)
                .ConfigureAwait(false);
            if (modelIntents is not null && !request.IsExpired(acceptedGameTime))
            {
                return new DecisionResult(request.DecisionId, DecisionSource.Model, modelIntents, acceptedGameTime);
            }

            // 模型结果解析失败、模型失败/超时或已过期：一律丢弃，回退规则路径。
        }

        return new DecisionResult(
            request.DecisionId,
            DecisionSource.Rules,
            _ruleSource.Decide(context),
            acceptedGameTime);
    }

    private async Task<IReadOnlyList<WorldIntent>?> TryParseModelIntentsAsync(
        DecisionRequest request,
        AgentContext context,
        GameTime acceptedGameTime,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _provider!.GenerateAsync(
                BuildModelRequest(request, context),
                cancellationToken).ConfigureAwait(false);
            if (!response.Succeeded)
            {
                return null;
            }

            var parsed = _parser.Parse(request, context, response.Content, acceptedGameTime);
            return parsed.Succeeded ? parsed.Intents : null;
        }
        catch (OperationCanceledException)
        {
            // 调用方取消必须原样传播，不能伪装成模型失败。
            throw;
        }
        catch (Exception)
        {
            // 模型路径是按需外围决策源：任何未预期异常都回退规则，不让世界主循环停下来。
            return null;
        }
    }

    /// <summary>编译最小决策上下文（DecisionPacket 的最小子集），不包含密钥或完整世界状态。</summary>
    private static ModelRequest BuildModelRequest(DecisionRequest request, AgentContext context)
    {
        var capabilities = string.Join(", ", context.Capabilities
            .OrderBy(item => item.ToString(), StringComparer.Ordinal));
        var armies = string.Join("; ", context.Armies
            .OrderBy(item => item.ArmyId.Value, StringComparer.Ordinal)
            .Select(item => $"{item.ArmyId.Value}(aux={item.Auxiliaries},line={item.LineInfantry},training={item.TrainingDays})"));
        var input = string.Join("\n", new[]
        {
            $"decision_request_id: {request.DecisionId}",
            $"actor_id: {request.ActorId}",
            $"observed_world_version: {request.ObservedWorldVersion}",
            $"observed_game_time: {context.GameTime}",
            $"decision_deadline_game_time: {request.Deadline}",
            $"treasury_silver: {context.TreasurySilver}",
            $"facility_count: {context.FacilityCount}",
            $"capabilities: {capabilities}",
            $"armies: {armies}",
        });

        return new ModelRequest(
            "你是大明朝廷的一位谨慎大臣。只能输出白名单内的一种结构化意图 JSON，不要输出任何说明文字。",
            input,
            WhitelistSchema);
    }
}
