using MediaIsland.Services.Audio.Playback;
using Xunit;

namespace MediaIsland.Tests.Audio;

/// <summary>
/// 默认渲染端点的轮询检测器。判据全部用手动泵，不碰真设备：变化判定与缓存语义是
/// 纯托管逻辑，接真 COM 只会让结果取决于跑测试的机器插了什么。
///
/// 这里钉四件事——变化只报一次且只认「非 null → 不同的非 null」、不变与首拍零触发、
/// null 往返的三种走向、读缓存永不触发枚举（频次的界只由泵拍数决定）。
/// </summary>
public class DefaultEndpointWatcherTests
{
    /// <summary>
    /// 照脚本逐拍出值的 id 提供者，并记调用次数——缓存频次的界断在它上面。
    /// 脚本走完后停在最后一个值，模拟设备状态稳定后的持续轮询。
    /// </summary>
    private sealed class ScriptedIdProvider(params string?[] script)
    {
        public int Calls { get; private set; }

        public string? Next()
        {
            var value = script[Math.Min(Calls, script.Length - 1)];
            Calls++;
            return value;
        }
    }

    private static (DefaultEndpointWatcher Watcher, ScriptedIdProvider Provider, Func<int> Changes)
        Pumped(params string?[] script)
    {
        var provider = new ScriptedIdProvider(script);
        var watcher = new DefaultEndpointWatcher(provider.Next);
        var changes = 0;
        watcher.DefaultEndpointChanged += (_, _) => changes++;
        return (watcher, provider, () => changes);
    }

    [Fact]
    public void FirstBeat_WithADevice_EstablishesBaselineWithoutRaising()
    {
        // 首拍不误报：进程刚起时读到的设备不是「变化」，只是基线。
        var (watcher, _, changes) = Pumped("dev-a");
        using var _ = watcher;

        watcher.Poll();

        Assert.Equal(0, changes());
        Assert.Equal("dev-a", watcher.CachedId);
    }

    [Fact]
    public void UnchangedId_RaisesNothing_WhilePollingKeepsHappening()
    {
        var (watcher, provider, changes) = Pumped("dev-a", "dev-a", "dev-a");
        using var _ = watcher;

        watcher.Poll();
        watcher.Poll();
        watcher.Poll();

        // 防真空：先钉「轮询确实每拍都读了」，零触发才有意义——
        // 否则「压根没轮询」也能让下面那条通过。
        Assert.Equal(3, provider.Calls);
        Assert.Equal(0, changes());
    }

    [Fact]
    public void AChange_RaisesExactlyOnce_AndTheCacheFollows()
    {
        var (watcher, _, changes) = Pumped("dev-a", "dev-b", "dev-b");
        using var _ = watcher;

        watcher.Poll();
        watcher.Poll();

        Assert.Equal(1, changes());
        Assert.Equal("dev-b", watcher.CachedId);

        // 变化后的稳态拍不再重复报——事件语义是「变了」，不是「与很久前不同」。
        watcher.Poll();
        Assert.Equal(1, changes());
    }

    [Fact]
    public void GoingNull_IsRecordedButNotRaised()
    {
        // 变成 null 是设备消失，归拔线路径（WASAPI 错误返回自己会到），watcher 不越权。
        // 缓存要如实变 null：读它的人把 null 当「未知设备」，偏移取 0。
        var (watcher, _, changes) = Pumped("dev-a", null);
        using var _ = watcher;

        watcher.Poll();
        watcher.Poll();

        Assert.Equal(0, changes());
        Assert.Null(watcher.CachedId);
    }

    [Fact]
    public void NullRecovery_ToTheSameDevice_DoesNotRaise()
    {
        // 恢复到消失前的同一个端点不算变化：流若还活着不必重启，
        // 流已死走的是错误路径的既有恢复。
        var (watcher, _, changes) = Pumped("dev-a", null, "dev-a");
        using var _ = watcher;

        watcher.Poll();
        watcher.Poll();
        watcher.Poll();

        Assert.Equal(0, changes());
        Assert.Equal("dev-a", watcher.CachedId);
    }

    [Fact]
    public void NullRecovery_ToADifferentDevice_RaisesOnce()
    {
        // 消失期间默认设备换了人：恢复的那一拍与最近的非 null 基线比对，
        // 不同即报——正是「拔掉 A 插上 B」的形态。
        var (watcher, _, changes) = Pumped("dev-a", null, "dev-b");
        using var _ = watcher;

        watcher.Poll();
        watcher.Poll();
        watcher.Poll();

        Assert.Equal(1, changes());
        Assert.Equal("dev-b", watcher.CachedId);
    }

    [Fact]
    public void StartingWithNoDevice_TheFirstDeviceIsBaselineNotAChange()
    {
        // 无设备启动后插上第一台：从未有过非 null 基线，报变化等于把「首拍不误报」
        // 推迟成「第二拍误报」。
        var (watcher, _, changes) = Pumped(null, "dev-a");
        using var _ = watcher;

        watcher.Poll();
        watcher.Poll();

        Assert.Equal(0, changes());
        Assert.Equal("dev-a", watcher.CachedId);
    }

    [Fact]
    public void Reads_NeverTouchTheProvider()
    {
        // rv-t8 Minor 3 的收口本体：每秒全仓只枚举一次的「一次」由泵拍数决定，
        // 读多少遍缓存都不再碰 COM。界写死为泵拍常量，不引用实现推导出的量。
        var (watcher, provider, _) = Pumped("dev-a");
        using var _w = watcher;

        for (var beat = 0; beat < 5; beat++)
        {
            watcher.Poll();
        }

        Assert.InRange(provider.Calls, 1, 5);
        var callsAfterBeats = provider.Calls;

        for (var read = 0; read < 20; read++)
        {
            _ = watcher.CachedId;
        }

        Assert.Equal(callsAfterBeats, provider.Calls);
    }

    [Fact]
    public void AClosedGate_SkipsEnumerationEntirely()
    {
        // 门控是枚举级的：无会话且设置页不可见时，节拍照走但不碰 COM。
        var provider = new ScriptedIdProvider("dev-a");
        var gateOpen = false;
        // ReSharper disable once AccessToModifiedClosure
        using var watcher = new DefaultEndpointWatcher(provider.Next, () => gateOpen);

        watcher.Poll();
        watcher.Poll();
        watcher.Poll();

        Assert.Equal(0, provider.Calls);
        Assert.Null(watcher.CachedId);

        // 门开后同一拍机制立即恢复枚举——证明上面三拍的零调用是门造成的。
        gateOpen = true;
        watcher.Poll();
        Assert.Equal(1, provider.Calls);
        Assert.Equal("dev-a", watcher.CachedId);
    }

    [Fact]
    public void UiVisibility_OpensTheGateAndPrimesTheCacheImmediately()
    {
        // 设置页要读 HasPlaybackDevice 与 per-device 偏移。进场即泵一拍，
        // 页面打开第一眼就有值；离场后门随之关上，不再为看不见的页面枚举。
        var provider = new ScriptedIdProvider("dev-a");
        using var watcher = new DefaultEndpointWatcher(provider.Next, static () => false);

        watcher.SetUiVisible(true);

        Assert.Equal(1, provider.Calls);
        Assert.Equal("dev-a", watcher.CachedId);

        watcher.SetUiVisible(false);
        watcher.Poll();

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public void AThrowingProvider_ReadsAsNoDevice()
    {
        // 生产 provider 把 COM 失败折成 null，这条钉的是兜底层：任意异常同义于
        // 「此刻无设备」，不报变化、不上抛——计时器线程上没人接得住。
        var beats = 0;
        using var watcher = new DefaultEndpointWatcher(() =>
        {
            beats++;
            return beats == 2 ? throw new InvalidOperationException("COM 失败") : "dev-a";
        });
        var changes = 0;
        watcher.DefaultEndpointChanged += (_, _) => changes++;

        watcher.Poll();
        watcher.Poll();

        Assert.Equal(0, changes);
        Assert.Null(watcher.CachedId);

        // 失败后同一设备回来：与 null 往返同义，不报。
        watcher.Poll();
        Assert.Equal(0, changes);
        Assert.Equal("dev-a", watcher.CachedId);
    }
}
