using MediaIsland.Models;
using MediaIsland.Services.Audio.Playback;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.Media.Platform;
using MediaIsland.Services.MediaLink;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// MediaLink 的服务注册图。这一层此前完全无人看守，而它的失效形态是插件加载期硬崩：
/// 上游服务的工厂里即时解析服务端，只要哪天服务端的工厂反过来解析上游服务，
/// 两个单例就会互相等对方构造完，进程当场死掉，且不会有任何编译期或启动前的提示。
///
/// 即时解析本身无法避开：三条重算边里有一条是往服务端的事件上挂处理器，
/// 挂事件必须持有发布方实例，委托形式做不到。故守卫只能落在「整张图能建起来」这条上。
/// </summary>
public class MediaLinkServiceRegistrationTests
{
    private static ServiceProvider BuildProvider(PluginSettings settings)
    {
        var services = new ServiceCollection();

        // AddMediaLink 的三个前置依赖。插件入口里由更前面的注册提供，此处给最小可用实现。
        services.AddSingleton<IMediaService>(new RegistrationFakeMediaService());
        services.AddSingleton(new LyricsSearchService([], [], () => new LyricsSourceSettings()));
        services.AddSingleton(new MediaPlatformProviderResolver(
            [], NullLogger<MediaPlatformProviderResolver>.Instance));

        services.AddMediaLink(() => settings);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ObjectGraph_ResolvesWithoutConstructionCycle()
    {
        // 解析上游服务会连带构造服务端与协调器——图里所有即时解析都在这一次里走到。
        // 有环时这里不是抛异常就是爆栈，两者都会让测试宿主非正常终止，不会静默通过。
        using var provider = BuildProvider(new PluginSettings());

        var upstream = provider.GetRequiredService<MediaLinkUpstreamHostedService>();

        Assert.NotNull(upstream);
        // 两个宿主服务必须是同一对单例：广播入口与需求读取都靠委托回到「那一个」服务端，
        // 解析出第二份的话，转发会发给一个没有会话的空壳。
        Assert.Same(
            provider.GetRequiredService<MediaLinkHostedService>(),
            provider.GetRequiredService<IMediaLinkGateway>());
        Assert.Same(
            provider.GetRequiredService<MediaSourceCoordinator>(),
            provider.GetRequiredService<IEffectiveMediaSource>());
        // 呈现侧读延迟的那条别名边。解析出第二份 AudioPlaybackService 的话，
        // 它的「在播」恒为假、延迟恒为零，跨机播放时歌词会一直领先耳朵一个缓冲深度，
        // 而这个失效不报错、不影响出声。
        Assert.Same(
            provider.GetRequiredService<AudioPlaybackService>(),
            provider.GetRequiredService<IAudioOutputLatency>());
    }

    [Fact]
    public void ObjectGraph_HasAudioRecomputeEdgesConnected()
    {
        // 上一条只证明图建得起来，建得起来但没接线同样是静默失效。
        // 这里驱动三条边里唯一能同步观测的那条：生效媒体变化 → 重算向上游的订阅。
        // MediaLinkAudioWiring.Connect 内部无分支，故这一条通即三条都接上了。
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.ExternalPreferred
        };
        using var provider = BuildProvider(settings);

        var upstream = provider.GetRequiredService<MediaLinkUpstreamHostedService>();
        var media = (RegistrationFakeMediaService)provider.GetRequiredService<IMediaService>();
        var audioSourceChanges = 0;
        upstream.AudioSourceChanged += (_, _) => Interlocked.Increment(ref audioSourceChanges);

        // 协调器订阅了媒体服务，故这一下会让它重算并发出生效媒体变化。
        // 上游客户端尚未建立，重算走同步分支并就地发出音源变化事件，无需等待。
        media.Raise(
            new MediaInfo("app", "WiredUpSong", "WiredUpArtist", null,
                TimeSpan.Zero, TimeSpan.FromMinutes(1),
                new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null),
            MediaInfoChangeKind.MediaProperties);

        Assert.True(
            Volatile.Read(ref audioSourceChanges) > 0,
            "生效媒体变了却没触发向上游的重算：接线没接上");
    }

    [Fact]
    public void ObjectGraph_RoutesInboundAudioThroughThePlaybackDecorator()
    {
        // 装饰器必须接在入站 PCM 与可视化之间，否则播放路径全程收不到帧：
        // renderer 起得来、开关显示为开、日志一切正常，就是没声音。
        // 这条边在整张图里只出现一次，接错不会有任何运行期提示。
        using var provider = BuildProvider(new PluginSettings());

        var upstream = provider.GetRequiredService<MediaLinkUpstreamHostedService>();

        Assert.Same(
            provider.GetRequiredService<AudioPlaybackService>(),
            upstream.AudioSubmitTarget);
    }

    [Fact]
    public async Task PlaybackDepthSetting_ReachesTheDecorator()
    {
        // W9 与 W10 两跳串联，全程同步（上游客户端为 null，重算就地发出音源变化）：
        //   改设置 → PropertyChanged → OnSettingsChanged → AffectsAudioRouting 判真
        //   → RecomputeAudioSubscription → ConnectPlayback 的 lambda → Configure
        //
        // 断言落在请求深度上而不是「起播成功」：这两跳是纯托管接线，不该因为跑测试的
        // 机器没有 native 音频库就无从判定。native 缺失时 Configure 走回落分支，
        // Start 从不被调用，而请求值照样记下——那正是这条判据要看的东西。
        //
        // 用真 DI 图而不是手搭：ConnectPlayback 的调用本身住在 AddMediaLink 的
        // upstream 工厂里，手搭只能证明手搭得对，证不了注册图里那条边接上了。
        var settings = new PluginSettings
        {
            MediaLinkPlaybackIsEnabled = true,
            MediaLinkPlaybackBufferMs = 300
        };
        using var provider = BuildProvider(settings);
        var upstream = provider.GetRequiredService<MediaLinkUpstreamHostedService>();
        var playback = provider.GetRequiredService<AudioPlaybackService>();

        // BindSettings 在 StartAsync 内，不起来就没人听 PropertyChanged。
        await upstream.StartAsync(CancellationToken.None);

        // 地基：此刻还没有哪次 Configure 带过 500。不钉这一条，
        // 「本来就是 500」会冒充成功。
        Assert.NotEqual(500, playback.RequestedTargetBufferMs);

        settings.MediaLinkPlaybackBufferMs = 500;

        Assert.Equal(500, playback.RequestedTargetBufferMs);

        await upstream.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void AudioSourceChange_AloneReconfiguresPlayback()
    {
        // W10 单独。刻意不 StartAsync：BindSettings 没跑，故改设置不会触发任何链路，
        // 于是唯一能让 Configure 被调用的路径只剩音源变化这一条。
        // 这个隔离让上一条与本条各自失败时指向不同的边，而不是一起红。
        var settings = new PluginSettings { MediaLinkPlaybackBufferMs = 800 };
        using var provider = BuildProvider(settings);
        var upstream = provider.GetRequiredService<MediaLinkUpstreamHostedService>();
        var playback = provider.GetRequiredService<AudioPlaybackService>();

        // 地基两条：既证明 800 不是初始值，也证明未 Start 时链路确实是断的。
        Assert.NotEqual(800, playback.RequestedTargetBufferMs);

        upstream.RecomputeAudioSubscription();

        Assert.Equal(800, playback.RequestedTargetBufferMs);
    }

    private sealed class RegistrationFakeMediaService : IMediaService
    {
        public event EventHandler<MediaInfoChangedEventArgs>? MediaInfoChanged;
        public MediaInfo? CurrentMediaInfo { get; private set; }
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task EnsureStartedAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void Raise(MediaInfo? info, MediaInfoChangeKind kind)
        {
            CurrentMediaInfo = info;
            MediaInfoChanged?.Invoke(this, new MediaInfoChangedEventArgs(info, kind));
        }
    }
}
