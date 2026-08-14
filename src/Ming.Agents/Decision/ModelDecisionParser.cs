using System.Text.Json;
using MingSim.Agents.Realtime;
using MingSim.Agents.Runtime;
using MingSim.Domain.Common;
using MingSim.Domain.Intents;
using MingSim.Domain.Realtime;

namespace MingSim.Agents.Decision;

/// <summary>一次模型 JSON 解析的结果；失败时 Intents 为空、ErrorMessage 说明原因。</summary>
public sealed record ModelParseResult(
    bool Succeeded,
    IReadOnlyList<WorldIntent> Intents,
    string? ErrorMessage = null);

/// <summary>
/// 把模型产出的“白名单意图”JSON 严格解析成强类型 WorldIntent。
/// </summary>
/// <remarks>
/// 解析规则固定（doc 07 §10）：
/// - 只接受已登记的 schema_version == 1；
/// - intent_type 必须在本解析器的白名单内（粮运/行军），未知类型明确拒绝；
/// - 必需参数缺失、类型错误或范围非法一律解析失败；
/// - 多余字段（如 public_statement、request_id）固定忽略，不触发任何动作；
/// - 身份（ActorId）、世界版本、提交时刻都由 DecisionRequest/权威时间程序绑定，
///   模型 JSON 里写什么都没用，不能伪造身份；
/// - 意图幂等键由 DecisionId + 序号派生，重试解析不会生成新键，避免绕过内核幂等。
/// 解析失败由调用方（DecisionPlanner）回退规则决策；本类不修改任何状态。
/// </remarks>
public sealed class ModelDecisionParser
{
    private const int SupportedSchemaVersion = 1;

    /// <summary>解析模型文本；任何失败都返回 Succeeded == false 的结构化结果。</summary>
    public ModelParseResult Parse(
        DecisionRequest request,
        AgentContext context,
        string modelJson,
        GameTime acceptedGameTime)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(modelJson);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(modelJson);
        }
        catch (JsonException)
        {
            return Failure("模型输出不是合法 JSON。");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure("模型输出必须是 JSON 对象。");
            }

            if (!root.TryGetProperty("schema_version", out var schemaVersion) ||
                !schemaVersion.TryGetInt32(out var version) ||
                version != SupportedSchemaVersion)
            {
                return Failure($"只接受已登记 schema_version={SupportedSchemaVersion}。");
            }

            if (!root.TryGetProperty("intent_type", out var intentType) ||
                intentType.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(intentType.GetString()))
            {
                return Failure("缺少 intent_type。");
            }

            if (!root.TryGetProperty("parameters", out var parameters) ||
                parameters.ValueKind != JsonValueKind.Object)
            {
                return Failure("缺少 parameters 对象。");
            }

            // 意图序号从 1 开始：幂等键 = DecisionId-N，与 doc 07 §12 的稳定 CommandId 派生一致。
            const int intentIndex = 1;
            return intentType.GetString() switch
            {
                "logistics.request_shipment" => ParseLogistics(request, context, parameters, acceptedGameTime, intentIndex),
                "military.move_army" => ParseMove(request, context, parameters, acceptedGameTime, intentIndex),
                var unknown => Failure($"未知意图类型：{unknown}。"),
            };
        }
    }

    private static ModelParseResult ParseLogistics(
        DecisionRequest request,
        AgentContext context,
        JsonElement parameters,
        GameTime acceptedGameTime,
        int intentIndex)
    {
        if (!TryGetRequiredString(parameters, "route_id", out var routeId) ||
            !TryGetPositiveLong(parameters, "grain_quantity", out var quantity))
        {
            return Failure("logistics.request_shipment 缺少 route_id 或 grain_quantity 非法。");
        }

        return Success([
            new PlanLogisticsIntent(
                $"{request.DecisionId}-{intentIndex}",
                request.ActorId,
                context.TurnNumber,
                $"{request.DecisionId}-{intentIndex}",
                request.ObservedWorldVersion,
                new RouteId(routeId),
                quantity,
                acceptedGameTime.Value),
        ]);
    }

    private static ModelParseResult ParseMove(
        DecisionRequest request,
        AgentContext context,
        JsonElement parameters,
        GameTime acceptedGameTime,
        int intentIndex)
    {
        if (!TryGetRequiredString(parameters, "army_id", out var armyId) ||
            !TryGetRequiredString(parameters, "destination_id", out var destinationId))
        {
            return Failure("military.move_army 缺少 army_id 或 destination_id。");
        }

        var travelHours = 24; // 与 MoveArmyIntent 的默认值一致
        if (parameters.TryGetProperty("travel_hours", out var travelHoursElement))
        {
            if (!travelHoursElement.TryGetInt32(out var parsedTravelHours) || parsedTravelHours <= 0)
            {
                return Failure("military.move_army 的 travel_hours 必须是正整数。");
            }

            travelHours = parsedTravelHours;
        }

        return Success([
            new MoveArmyIntent(
                $"{request.DecisionId}-{intentIndex}",
                request.ActorId,
                context.TurnNumber,
                $"{request.DecisionId}-{intentIndex}",
                request.ObservedWorldVersion,
                new ArmyId(armyId),
                new ProvinceId(destinationId),
                acceptedGameTime.Value,
                travelHours),
        ]);
    }

    private static bool TryGetRequiredString(JsonElement parameters, string propertyName, out string value)
    {
        if (parameters.TryGetProperty(propertyName, out var element) &&
            element.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(element.GetString()))
        {
            value = element.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetPositiveLong(JsonElement parameters, string propertyName, out long value)
    {
        if (parameters.TryGetProperty(propertyName, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt64(out value) &&
            value > 0)
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static ModelParseResult Success(IReadOnlyList<WorldIntent> intents) =>
        new(true, intents, null);

    private static ModelParseResult Failure(string message) =>
        new(false, [], message);
}
