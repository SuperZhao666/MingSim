using MingSim.Domain.Common;

namespace MingSim.Domain.Economy;

/// <summary>财政状态：第一版先以银两作为主货币。</summary>
public sealed class TreasuryState
{
    public TreasuryState(long silver)
    {
        Silver = silver;
    }

    public long Silver { get; private set; }

    public bool TrySpend(long amount)
    {
        if (amount < 0 || Silver < amount)
        {
            return false;
        }

        Silver -= amount;
        return true;
    }

    public void Receive(long amount)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        Silver += amount;
    }

    public TreasuryState Clone() => new(Silver);
}

/// <summary>一类实物库存。</summary>
public sealed class ResourceStock
{
    public ResourceStock(string resourceType, long quantity)
    {
        ResourceType = resourceType;
        Quantity = quantity;
    }

    public string ResourceType { get; }

    public long Quantity { get; private set; }

    public long Reserved { get; private set; }

    public bool TryReserve(long amount)
    {
        if (amount < 0 || Reserved + amount > Quantity)
        {
            return false;
        }

        Reserved += amount;
        return true;
    }

    public bool TryConsume(long amount)
    {
        if (amount < 0 || amount > Quantity - Reserved)
        {
            return false;
        }

        Quantity -= amount;
        return true;
    }

    public void Add(long amount)
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount));
        }

        Quantity += amount;
    }

    public ResourceStock Clone()
    {
        var clone = new ResourceStock(ResourceType, Quantity);
        if (Reserved > 0)
        {
            clone.TryReserve(Reserved);
        }

        return clone;
    }
}

/// <summary>世界所有可消耗物资的集合。</summary>
public sealed class InventoryState
{
    private readonly Dictionary<string, ResourceStock> _stocks = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ResourceStock> Stocks => _stocks;

    public ResourceStock GetOrCreate(string resourceType)
    {
        if (!_stocks.TryGetValue(resourceType, out var stock))
        {
            stock = new ResourceStock(resourceType, 0);
            _stocks.Add(resourceType, stock);
        }

        return stock;
    }

    public InventoryState Clone()
    {
        var clone = new InventoryState();
        foreach (var (resourceType, stock) in _stocks)
        {
            clone._stocks.Add(resourceType, stock.Clone());
        }

        return clone;
    }
}

/// <summary>所有经济相关状态的聚合对象。</summary>
public sealed class EconomyState
{
    public EconomyState(long treasurySilver)
    {
        Treasury = new TreasuryState(treasurySilver);
    }

    public TreasuryState Treasury { get; }

    public InventoryState Inventory { get; } = new();

    public EconomyState Clone()
    {
        var clone = new EconomyState(Treasury.Silver);
        foreach (var (resourceType, stock) in Inventory.Stocks)
        {
            var destination = clone.Inventory.GetOrCreate(resourceType);
            destination.Add(stock.Quantity);
            if (stock.Reserved > 0)
            {
                destination.TryReserve(stock.Reserved);
            }
        }

        return clone;
    }
}
