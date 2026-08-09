namespace MediaIsland.Services.Audio.Visualization;

/// <summary>
/// 可视化需求。组件挂载即注册、卸载即释放，对外只暴露一个布尔状态。
///
/// 内部对组件实例计数，但下游拿到的是状态而非增减事件——消费者收到
/// <see cref="DemandChanged"/> 后重算，重算幂等，漏触发最多延迟到下一次事件即自愈。
/// 反过来若下游做增减，漏一次就永久错位，且错位无法自愈。
/// （被反对的是让下游做增减，不是本类内部的实例计数。）
/// </summary>
public sealed class AudioVisualizationDemand
{
    private int _count;

    public bool IsDemanded => Volatile.Read(ref _count) > 0;

    public int Count => Volatile.Read(ref _count);

    /// <summary>仅在状态真正翻转时触发（0↔1 的那次）。中间的增减不通知。</summary>
    public event Action? DemandChanged;

    public IDisposable Register()
    {
        if (Interlocked.Increment(ref _count) == 1)
        {
            DemandChanged?.Invoke();
        }

        return new Registration(this);
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _count) == 0)
        {
            DemandChanged?.Invoke();
        }
    }

    /// <summary>
    /// 释放句柄。重复 <see cref="Dispose"/> 幂等——组件的 Unloaded 未必只走一次，
    /// 而计数掉成负数会让后续的 Register 把 -1 加成 0，状态翻不回真，
    /// 采集从此再也起不来且没有任何报错。
    /// </summary>
    private sealed class Registration(AudioVisualizationDemand owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
