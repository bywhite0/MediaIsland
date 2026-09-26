using MediaIsland.Models;
using MediaIsland.Services.Audio;
using MediaIsland.Services.Audio.Visualization;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media.Platform;
using MediaIsland.Services.MediaLink;
using MediaIsland.Tests.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 用户把频谱组件拖上岛却一片空白——根因是本机采集跟着共享服务端生死。
/// 这两条用例钉住「可视化需求不看共享开关」。第一条在解耦前必然失败。
/// </summary>
[Collection(nameof(MediaLinkSettingsTests))]
public class MediaLinkCaptureDecouplingTests
{
    [Fact]
    public async Task SharingDisabled_VisualizationDemand_StillStartsLocalCapture()
    {
        var settings = new PluginSettings { MediaLinkIsEnabled = false };
        var source = new FakeAudioFrameSource();
        var demand = new AudioVisualizationDemand();
        using var capture = new LocalAudioCapture(source);
        using var host = NewHost(settings, demand, capture);
        using (var cts = new CancellationTokenSource(10_000)) await host.StartAsync(cts.Token);

        using var registration = demand.Register();
        await WaitUntilAsync(() => source.StartCount == 1);

        Assert.Equal(1, source.StartCount);
    }

    [Fact]
    public async Task SharingToggledOff_WhileVisualizing_KeepsCapturing()
    {
        var settings = new PluginSettings
        {
            MediaLinkIsEnabled = true,
            MediaLinkListenAddress = "127.0.0.1",
            MediaLinkPort = 0
        };
        var source = new FakeAudioFrameSource();
        var demand = new AudioVisualizationDemand();
        using var capture = new LocalAudioCapture(source);
        using var host = NewHost(settings, demand, capture);
        using (var cts = new CancellationTokenSource(10_000)) await host.StartAsync(cts.Token);
        using var registration = demand.Register();
        await WaitUntilAsync(() => source.StartCount == 1);
        // 构造时没挂可视化 sink，此刻 hub 上唯一的 sink 就是服务端挂的广播器。
        Assert.Equal(1, capture.SinkCount);

        settings.MediaLinkIsEnabled = false;
        // 防抖 500ms + 停服；等足以确认「重建已经发生过」。
        await Task.Delay(1500);

        Assert.Equal(0, source.StopCount);
        Assert.True(capture.IsCapturing);
        // 采集照旧，但本机帧不再流向广播器。
        Assert.Equal(0, capture.SinkCount);
    }

    /// <summary>
    /// 宿主退出时 ClassIsland 不 Dispose Host，只调 StopAsync；采集若只在容器释放时才停，
    /// native 采集线程会一直跑到进程被终止。
    /// </summary>
    [Fact]
    public async Task StopAsync_WhileVisualizing_StopsLocalCapture()
    {
        var settings = new PluginSettings { MediaLinkIsEnabled = false };
        var source = new FakeAudioFrameSource();
        var demand = new AudioVisualizationDemand();
        using var capture = new LocalAudioCapture(source);
        using var host = NewHost(settings, demand, capture);
        using (var cts = new CancellationTokenSource(10_000)) await host.StartAsync(cts.Token);
        using var registration = demand.Register();
        await WaitUntilAsync(() => source.StartCount == 1);

        using (var cts = new CancellationTokenSource(10_000)) await host.StopAsync(cts.Token);

        Assert.Equal(1, source.StopCount);
        Assert.False(capture.IsCapturing);
    }

    private static MediaLinkHostedService NewHost(
        PluginSettings settings, AudioVisualizationDemand demand, LocalAudioCapture capture)
    {
        var media = new FakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        var coordinator = new MediaSourceCoordinator(media, lyrics, store, () => settings);
        var resolver = new MediaPlatformProviderResolver([], NullLogger<MediaPlatformProviderResolver>.Instance);
        return new MediaLinkHostedService(
            media, lyrics, store, coordinator, resolver, () => settings,
            visualizationDemand: demand,
            localCapture: capture);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(20);
        }
    }
}
