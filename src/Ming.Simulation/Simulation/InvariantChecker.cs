using MingSim.Domain;
using MingSim.Domain.Errors;

namespace MingSim.Simulation;

/// <summary>
/// 提交回合前的最后一道安全网。
/// </summary>
/// <remarks>
/// 规则代码可能不断增加，但“不允许负库存、负兵员、负国库”这类底线应该集中管理，
/// 这样任何新系统接入时都必须通过同一套检查。
/// </remarks>
public sealed class InvariantChecker
{
    public IReadOnlyList<SimulationError> Check(WorldState world)
    {
        var errors = new List<SimulationError>();

        if (world.Economy.Treasury.Silver < 0)
        {
            errors.Add(new SimulationError(
                "INVARIANT_TREASURY_NEGATIVE",
                "国库银两不能小于 0。"));
        }

        foreach (var stock in world.Economy.Inventory.Stocks.Values)
        {
            if (stock.Quantity < 0 || stock.Reserved < 0 || stock.Reserved > stock.Quantity)
            {
                errors.Add(new SimulationError(
                    "INVARIANT_INVENTORY_INVALID",
                    $"库存 {stock.ResourceType} 不满足 quantity >= reserved >= 0。"));
            }
        }

        foreach (var army in world.Military.Armies.Values)
        {
            if (army.Auxiliaries < 0 || army.LineInfantry < 0 || army.TrainingDays < 0)
            {
                errors.Add(new SimulationError(
                    "INVARIANT_ARMY_NEGATIVE",
                    $"军队 {army.Name} 的兵员或训练天数出现负数。"));
            }
        }

        foreach (var facility in world.Industry.Facilities.Values)
        {
            if (facility.BaseCapacity < 0 || facility.Workforce < 0 || facility.ProducedThisTurn < 0)
            {
                errors.Add(new SimulationError(
                    "INVARIANT_FACILITY_INVALID",
                    $"工坊 {facility.Id} 的产能、工人或产量出现非法值。"));
            }
        }

        return errors;
    }
}
