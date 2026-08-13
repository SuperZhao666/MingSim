namespace MingSim.Simulation.Random;

/// <summary>
/// 由世界编号、回合和事件编号共同决定的轻量确定性随机数发生器。
/// </summary>
/// <remarks>
/// 不要在核心规则里直接使用 Guid、DateTime 或普通随机数，否则同一存档无法稳定重放。
/// 这里先用稳定的 FNV-1a 哈希做种子；以后可以替换算法，但必须保持“相同输入得到相同结果”。
/// </remarks>
public sealed class DeterministicRandom
{
    private uint _state;

    public DeterministicRandom(string worldId, int turnNumber, string eventId)
    {
        _state = Hash($"{worldId}|{turnNumber}|{eventId}");
        if (_state == 0)
        {
            _state = 2_166_136_261;
        }
    }

    public int Next(int minimumInclusive, int maximumExclusive)
    {
        if (minimumInclusive >= maximumExclusive)
        {
            throw new ArgumentException("随机数范围必须满足 minimum < maximum。");
        }

        _state = 1_664_525u * _state + 1_013_904_223u;
        var range = (uint)(maximumExclusive - minimumInclusive);
        return minimumInclusive + (int)(_state % range);
    }

    private static uint Hash(string value)
    {
        const uint offsetBasis = 2_166_136_261;
        const uint prime = 16_777_619;
        var hash = offsetBasis;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }

        return hash;
    }
}
