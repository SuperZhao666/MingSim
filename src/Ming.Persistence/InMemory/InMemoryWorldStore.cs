using MingSim.Application.Ports;
using MingSim.Domain;
using MingSim.Domain.Common;

namespace MingSim.Persistence.InMemory;

/// <summary>
/// 纯内存世界存储，专门用于 MVP、命令行演示和测试。
/// </summary>
/// <remarks>
/// 将来替换成 SQLite 时，应用层和模拟内核不需要改变；只需要新增一个实现同样接口的适配器。
/// 这里每次读写都复制状态，是为了模拟真实持久化边界，也防止调用方偷偷持有内部引用。
/// </remarks>
public sealed class InMemoryWorldStore : IWorldStore
{
    private WorldState _state;

    public InMemoryWorldStore(WorldState initialState)
    {
        _state = initialState.Clone();
    }

    public WorldState Load(WorldId worldId)
    {
        if (_state.Id != worldId)
        {
            throw new KeyNotFoundException($"世界 {worldId} 不存在。");
        }

        return _state.Clone();
    }

    public void Commit(WorldState newState)
    {
        if (newState.Id != _state.Id)
        {
            throw new InvalidOperationException("不能把一个世界提交到另一个世界的存储中。");
        }

        _state = newState.Clone();
    }
}
