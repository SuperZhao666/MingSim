using System;
using System.Collections.Generic;

namespace Ming.Godot;

/// <summary>
/// CommandOutcome.ErrorCodes → 中文解释（doc 03 §4 P0 事件与失败行 / doc 09 §8 结果与失败解释面板）。
/// 只做静态翻译，不包含任何业务判断；未知错误码原样露出，避免把程序错误包装成玩家可读的谎言。
/// </summary>
public static class CommandFailureText
{
    private static readonly IReadOnlyDictionary<string, string> Chinese = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // 命令收件箱层（RealtimeSimulationRuntime.Enqueue / ValidateAndApplyCommand）
        ["INVALID_COMMAND_ID"] = "命令编号不合法（须为 1..128 个字母、数字或 -_.:）。",
        ["NON_UTC_COMMAND_TIME"] = "命令提交时间必须使用 UTC。",
        ["INVALID_OBJECT_ID"] = "命令中的对象编号不合法。",
        ["STATE_VERSION_CONFLICT"] = "命令基于过期世界版本，请刷新后再试。",
        ["IDEMPOTENCY_CONFLICT"] = "同一命令编号携带了不同的命令内容。",
        ["UNKNOWN_COMMAND"] = "未知的实时命令类型。",
        ["INVALID_SPEED"] = "实时速度必须在 0.25 到 5 倍之间。",
        ["RECOVERY_READ_ONLY"] = "当前从损坏存档回退到只读恢复点；可查看/导出，但不能继续推进或提交命令。",

        // 政令（CreateDecreeCommand / ApplyDecree）
        ["INVALID_DECREE_GOAL"] = "政令目标不能为空。",
        ["DECREE_ALREADY_EXISTS"] = "政令编号已经存在。",
        ["INVALID_DECREE_BUDGET"] = "政令预算必须为正数。",
        ["DECREE_DEADLINE_IN_PAST"] = "政令期限必须晚于当前时间。",
        ["RESPONSIBLE_ACTOR_NOT_FOUND"] = "政令承办人不存在。",
        ["DECREE_ISSUER_UNAUTHORIZED"] = "政令签发人不存在或没有签发政令的授权。",
        ["DECREE_APPROVER_UNAUTHORIZED"] = "请饷批准人没有财权授权。",
        ["DECREE_RESPONSIBLE_UNAUTHORIZED"] = "政令承办人没有所需能力的授权。",
        ["DECREE_BUDGET_EXCEEDS_TREASURY"] = "国库银两不足以批准该政令预算。",
        ["DECREE_NOT_FOUND"] = "政令不存在。",
        ["DECREE_NOT_PENDING_APPROVAL"] = "该政令当前不处于待批准状态。",
        ["DECREE_SHIPMENT_NOT_FOUND"] = "政令绑定的运输单不存在。",
        ["DECREE_SHIPMENT_ALREADY_ARRIVED"] = "运输单已抵达，不能再绑定或批准该绑定政令。",
        ["DECREE_SHIPMENT_ALREADY_BOUND"] = "该运输单已被另一道有效政令占用。",

        // 粮运/行军等其他命令（本面板不直接发起，但顶栏结果区会显示）
        ["ARMY_NOT_FOUND"] = "军队不存在。",
        ["ARMY_ALREADY_IN_TRANSIT"] = "该军队已经在执行另一条行军。",
        ["TOOL_SCOPE_DENIED"] = "目标超出调用者能力范围。",
        ["INVALID_TRAVEL_TIME"] = "行军时间必须在 1 小时到 365 天之间。",
        ["PROVINCE_NOT_ADJACENT"] = "目标地区必须存在且与军队当前地区相邻。",
        ["INVALID_GRAIN_QUANTITY"] = "运输粮食数量必须为正数。",
        ["SHIPMENT_ALREADY_EXISTS"] = "运输单编号已经存在。",
        ["ROUTE_NOT_FOUND"] = "路线不存在。",
        ["ESCORT_BUDGET_INSUFFICIENT"] = "国库银两不足以支付护卫费用。",
        ["INSUFFICIENT_GRAIN"] = "起点库存不足。",
        ["ROUTE_CAPACITY_EXCEEDED"] = "路线在途容量不足。",
        ["LOSS_CALCULATION_OVERFLOW"] = "运输损耗计算超出安全范围。",
        ["DESTINATION_CAPACITY_EXCEEDED"] = "目的地库存容量不足。",

        // 推进层
        ["TARGET_GAME_TIME_IN_PAST"] = "目标游戏时间不能早于当前权威时间。",
    };

    /// <summary>把内核错误码翻译为中文；未知错误码原样返回并标注未收录。</summary>
    public static string Translate(string errorCode) =>
        Chinese.TryGetValue(errorCode, out var text) ? text : $"未收录错误码：{errorCode}";
}
