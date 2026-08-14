using System.Diagnostics;
using MingSim.Agents.Audit;
using MingSim.Agents.Providers;
using MingSim.Agents.Runtime;
using MingSim.Domain.Intents;
using MingSim.Domain.Realtime;

namespace MingSim.Agents.Decision;

/// <summary>
/// 按需决策规划器：模型路径可选，规则（Utility AI）路径是默认回退（ADR-006）。
/// </summary>
/// <remarks>
/// 最小版人物决策流水线（doc 07 §3、§13）：
/// 1. 配置了 IModelProvider 时，先做预算闸门预检（预算耗尽→0 次调用，直接回退规则）；
/// 2. 通过闸门后尝试让模型产出白名单意图 JSON，计时并记录审计；
/// 3. 解析成功且结果未过期（AcceptedGameTime &lt; Deadline）→ 采用模型意图；
/// 4. 解析失败、解析器未预期异常、模型失败/超时或结果已过期 → 一律丢弃模型结果，回退规则决策。
///
/// 不配置 Provider（provider 为 null）时 0 次模型调用即可完整决策；
/// 模型故障只影响本次可选的模型路径，不会暂停世界主循环（doc 07 §13.4）。
/// 每次模型调用（或被预算拦截的调用）都写入 ModelAuditLog：只记固定摘要，绝不携带密钥。
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
    private readonly ModelBudgetTracker? _budget;
    private readonly ModelAuditLog? _auditLog;
    private readonly string _providerName;
    private readonly ModelDecisionParser _parser;

    /// <summary>
    /// 创建规划器。ruleSource 是规则回退（通常是 Utility AI）；
    /// provider 为空表示关闭模型路径，始终走规则决策；
    /// budget 为空表示不限预算；auditLog 为空表示不记录审计；providerName 仅用于审计摘要。
    /// parser 供契约测试注入"必然抛异常"的解析器，证明解析 try 回退防线（P2-1）；生产代码不传。
    /// </summary>
    public DecisionPlanner(
        IAgentDecisionSource ruleSource,
        IModelProvider? provider = null,
        ModelBudgetTracker? budget = null,
        ModelAuditLog? auditLog = null,
        string providerName = "model",
        ModelDecisionParser? parser = null)
    {
        _ruleSource = ruleSource ?? throw new ArgumentNullException(nameof(ruleSource));
        _provider = provider;
        _budget = budget;
        _auditLog = auditLog;
        _providerName = string.IsNullOrWhiteSpace(providerName) ? "model" : providerName;
        _parser = parser ?? new ModelDecisionParser();
    }

    /// <summary>
    /// 完成一次决策：预算闸门通过且模型结果有效未过期则采用模型意图，否则回退规则路径。
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

        if (_provider is null)
        {
            return RulesResult(request, context, acceptedGameTime, ModelFallbackReason.NotConfigured);
        }

        var modelRequest = BuildModelRequest(request, context);
        var estimatedRequestTokens = EstimateRequestTokens(modelRequest);

        // P1-AGENT-04：预算原子预留。TryReserve 在锁内一次性完成“检查+提交”，
        // 不足即拒绝（0 次调用，直接回退 Utility，doc 07 §13.3）；两个并发调用
        // 不可能同时通过同一额度的闸门。锁内只有整数累加，网络 await 绝不在锁内。
        var budget = _budget;
        var hasReservation = false;
        var reservation = new ModelBudgetReservation(0);
        if (budget is not null)
        {
            if (!budget.TryReserve(estimatedRequestTokens, out reservation))
            {
                AppendAudit(new ModelAuditEntry(
                    request.DecisionId,
                    _providerName,
                    ModelCallOutcome.BudgetExceeded,
                    estimatedRequestTokens,
                    0,
                    budget.CostFor(estimatedRequestTokens),
                    TimeSpan.Zero,
                    DateTimeOffset.UtcNow));
                return RulesResult(request, context, acceptedGameTime, ModelFallbackReason.BudgetExceeded);
            }

            hasReservation = true;
        }

        // 预留成功后到结算前，模型调用（网络 await）、解析等任何耗时操作都不持有预算锁；
        // 所有出口统一由 finally 结算预留：成功按 请求+响应 补记、失败按请求额消耗、
        // 取消全额返还——不允许任何路径把未结算的预留留在预算里。
        var stopwatch = Stopwatch.StartNew();
        long actualTokens = 0;
        try
        {
            ModelResponse response;
            try
            {
                response = await _provider.GenerateAsync(modelRequest, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 调用方取消必须原样传播，不能伪装成模型失败，也不记审计；预留全额返还。
                throw;
            }
            catch (Exception)
            {
                stopwatch.Stop();
                // 未预期异常（含断网、认证失败）只回退规则；异常文本可能携带认证细节，绝不外泄。
                actualTokens = estimatedRequestTokens;
                AppendAudit(FailureEntry(request.DecisionId, estimatedRequestTokens, 0, stopwatch.Elapsed));
                return RulesResult(request, context, acceptedGameTime, ModelFallbackReason.ProviderFailed);
            }

            stopwatch.Stop();
            if (!response.Succeeded)
            {
                // Provider 失败/超时：审计统一用固定类别，不依赖 Provider 的文案。
                actualTokens = estimatedRequestTokens;
                AppendAudit(FailureEntry(request.DecisionId, estimatedRequestTokens, 0, stopwatch.Elapsed));
                return RulesResult(request, context, acceptedGameTime, ModelFallbackReason.ProviderFailed);
            }

            var responseTokens = TokenEstimation.FromText(response.Content);
            actualTokens = estimatedRequestTokens + responseTokens;

            // 解析步骤整体纳入 try 回退（P2-1）：解析器是"模型输出不可信"的最后一道硬防线，
            // 任何未预期异常（未来 schema 版本、解析器缺陷等）都必须回退规则路径并记 ParseFailed 审计，
            // 绝不能把模型输出引发的异常抛给调用方阻塞世界（doc 07 §13.4）。
            ModelParseResult parsed;
            try
            {
                parsed = _parser.Parse(request, context, response.Content, acceptedGameTime);
            }
            catch (Exception)
            {
                AppendAudit(new ModelAuditEntry(
                    request.DecisionId,
                    _providerName,
                    ModelCallOutcome.ParseFailed,
                    estimatedRequestTokens,
                    responseTokens,
                    CostFor(estimatedRequestTokens + responseTokens),
                    stopwatch.Elapsed,
                    DateTimeOffset.UtcNow));
                return RulesResult(request, context, acceptedGameTime, ModelFallbackReason.ParseFailed);
            }

            if (parsed.Succeeded && !request.IsExpired(acceptedGameTime))
            {
                AppendAudit(new ModelAuditEntry(
                    request.DecisionId,
                    _providerName,
                    ModelCallOutcome.Accepted,
                    estimatedRequestTokens,
                    responseTokens,
                    CostFor(estimatedRequestTokens + responseTokens),
                    stopwatch.Elapsed,
                    DateTimeOffset.UtcNow));
                return new DecisionResult(request.DecisionId, DecisionSource.Model, parsed.Intents, acceptedGameTime);
            }

            // 模型结果解析失败或已过期：丢弃并回退规则路径，审计记录真实原因，不制造静默成功。
            var outcome = parsed.Succeeded ? ModelCallOutcome.Expired : ModelCallOutcome.ParseFailed;
            var reason = parsed.Succeeded ? ModelFallbackReason.Expired : ModelFallbackReason.ParseFailed;
            AppendAudit(new ModelAuditEntry(
                request.DecisionId,
                _providerName,
                outcome,
                estimatedRequestTokens,
                responseTokens,
                CostFor(estimatedRequestTokens + responseTokens),
                stopwatch.Elapsed,
                DateTimeOffset.UtcNow));
            return RulesResult(request, context, acceptedGameTime, reason);
        }
        finally
        {
            if (hasReservation)
            {
                budget!.Settle(reservation, actualTokens);
            }
        }
    }

    private DecisionResult RulesResult(
        DecisionRequest request,
        AgentContext context,
        GameTime acceptedGameTime,
        ModelFallbackReason reason) =>
        new(request.DecisionId, DecisionSource.Rules, _ruleSource.Decide(context), acceptedGameTime, reason);

    private long CostFor(long tokens) => _budget?.CostFor(tokens) ?? 0;

    private void AppendAudit(ModelAuditEntry entry) => _auditLog?.Append(entry);

    private ModelAuditEntry FailureEntry(
        string decisionId,
        long requestTokens,
        long responseTokens,
        TimeSpan duration) =>
        new(decisionId, _providerName, ModelCallOutcome.ProviderFailed,
            requestTokens, responseTokens, CostFor(requestTokens + responseTokens), duration, DateTimeOffset.UtcNow);

    private static long EstimateRequestTokens(ModelRequest request) =>
        TokenEstimation.FromText(request.SystemInstruction) +
        TokenEstimation.FromText(request.UserInput) +
        TokenEstimation.FromText(request.ExpectedOutputSchema);

    /// <summary>
    /// 编译最小决策上下文（DecisionPacket 的最小子集），不包含密钥或完整世界状态。
    /// 候选集（路线/军队/邻接目的地）进入提示，让模型只能从权威候选里选择（P1-AGENT-02）。
    /// </summary>
    private static ModelRequest BuildModelRequest(DecisionRequest request, AgentContext context)
    {
        var capabilities = string.Join(", ", context.Capabilities
            .OrderBy(item => item.ToString(), StringComparer.Ordinal));
        var armies = string.Join("; ", context.Armies
            .OrderBy(item => item.ArmyId.Value, StringComparer.Ordinal)
            .Select(item => $"{item.ArmyId.Value}(loc={item.LocationId.Value},aux={item.Auxiliaries},line={item.LineInfantry},training={item.TrainingDays},adjacent=[{string.Join(",", item.AdjacentDestinations.OrderBy(province => province.Value, StringComparer.Ordinal).Select(province => province.Value))}])"));
        var routes = string.Join("; ", context.Routes
            .OrderBy(item => item.RouteId.Value, StringComparer.Ordinal)
            .Select(item => $"{item.RouteId.Value}(from={item.From.Value},to={item.To.Value},sourceGrain={item.SourceGrain},headroom={item.DestinationHeadroom},capacity={item.RouteCapacity},inTransit={item.InTransitGrain},travelHours={item.TravelHours},lossPerThousand={item.LossPerThousand})"));
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
            $"routes: {routes}",
            "约束：route_id 只能从 routes 候选列表中选择；army_id 只能从 armies 候选列表中选择，" +
            "destination_id 必须是所选军队 adjacent 列表中的一个。",
        });

        return new ModelRequest(
            "你是大明朝廷的一位谨慎大臣。只能输出白名单内的一种结构化意图 JSON，不要输出任何说明文字。",
            input,
            WhitelistSchema);
    }
}
