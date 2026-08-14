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

        if (world.Scenario.LocalBurden is < 0 or > 100 || world.Scenario.MinisterTrust is < 0 or > 100 ||
            world.Scenario.DailyGrainDemand <= 0 || world.Scenario.SpentSilver < 0)
        {
            errors.Add(new SimulationError(
                "INVARIANT_SCENARIO_INVALID",
                "场景级状态必须满足 0<=负担/信任<=100、日需为正、支出非负。"));
        }

        if (world.Readiness.ValueBasisPoints is < 0 or > 10_000 ||
            world.Readiness.ArrearsGrain < 0 || world.Readiness.ConsecutiveZeroGrainDays < 0)
        {
            errors.Add(new SimulationError(
                "INVARIANT_READINESS_INVALID",
                "战备必须在 0..10000 基点内，欠饷与连续断粮天数不能为负。"));
        }

        foreach (var decree in world.Decrees.Values)
        {
            if (decree.Budget < 0)
            {
                errors.Add(new SimulationError(
                    "INVARIANT_DECREE_INVALID",
                    $"政令 {decree.Id} 的预算不能为负。"));
            }
        }

        foreach (var shipment in world.Logistics.Shipments.Values)
        {
            if (shipment.RaidLossGrain < 0 || shipment.RaidLossGrain > shipment.GrainQuantity)
            {
                errors.Add(new SimulationError(
                    "INVARIANT_SHIPMENT_RAID_INVALID",
                    $"运输单 {shipment.Id} 的袭粮损失必须落在 0 到计划量之间。"));
            }
        }

        return errors;
    }
}
