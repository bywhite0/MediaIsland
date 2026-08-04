using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.MediaLink;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 上游消费服务的配置解析与生效条件。
/// </summary>
public class MediaLinkUpstreamHostedServiceTests
{
    [Theory]
    [InlineData("192.168.1.10:17654", "ws://192.168.1.10:17654/v1/ws")]
    [InlineData("ws://192.168.1.10:17654", "ws://192.168.1.10:17654/v1/ws")]
    [InlineData("ws://192.168.1.10:17654/v1/ws", "ws://192.168.1.10:17654/v1/ws")]
    [InlineData("  127.0.0.1:1234  ", "ws://127.0.0.1:1234/v1/ws")]
    public void TryBuildEndpoint_NormalizesShorthand(string input, string expected)
    {
        // 要求用户手写完整 ws:// URL 是没必要的摩擦。
        Assert.True(MediaLinkUpstreamHostedService.TryBuildEndpoint(input, out var uri, out _));
        Assert.Equal(expected, uri.ToString());
    }

    [Fact]
    public void TryBuildEndpoint_PreservesNonDefaultPath()
    {
        // 已指定路径时不得被覆盖，未来协议换路径仍可用。
        Assert.True(MediaLinkUpstreamHostedService.TryBuildEndpoint(
            "ws://host:1/custom", out var uri, out _));
        Assert.Equal("/custom", uri.AbsolutePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryBuildEndpoint_Empty_Fails(string? input)
    {
        Assert.False(MediaLinkUpstreamHostedService.TryBuildEndpoint(input, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("http://host:1")]      // 非 ws 协议
    [InlineData("https://host:1")]
    public void TryBuildEndpoint_WrongScheme_Fails(string input)
    {
        // 只断言"被拒绝且给了理由"，不锁死具体措辞——文案面向用户，会随易读性调整。
        Assert.False(MediaLinkUpstreamHostedService.TryBuildEndpoint(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public async Task Disabled_DoesNotConnect()
    {
        var settings = NewSettings();
        settings.MediaLinkUpstreamIsEnabled = false;
        using var service = NewService(settings, out _);

        await service.StartAsync(CancellationToken.None);

        Assert.False(service.IsConnected);
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task EnabledWithEmptyToken_ReportsError()
    {
        // Token 为空时对方一定拒绝，不如直接说清楚而非无休止重连。
        var settings = NewSettings();
        settings.MediaLinkUpstreamIsEnabled = true;
        settings.MediaLinkUpstreamEndpoint = "127.0.0.1:1";
        settings.MediaLinkUpstreamToken = string.Empty;
        using var service = NewService(settings, out _);

        await service.StartAsync(CancellationToken.None);

        // 同上：断言"报告了错误"而非具体措辞。
        Assert.False(service.IsConnected);
        Assert.False(string.IsNullOrWhiteSpace(service.LastError));
    }

    [Fact]
    public async Task EnabledWithInvalidEndpoint_ReportsError()
    {
        var settings = NewSettings();
        settings.MediaLinkUpstreamIsEnabled = true;
        settings.MediaLinkUpstreamEndpoint = "http://wrong-scheme";
        settings.MediaLinkUpstreamToken = "tok";
        using var service = NewService(settings, out _);

        await service.StartAsync(CancellationToken.None);

        Assert.False(service.IsConnected);
        Assert.False(string.IsNullOrEmpty(service.LastError));
    }

    [Fact]
    public async Task StateChanged_FiresOnConfigApply()
    {
        var settings = NewSettings();
        using var service = NewService(settings, out _);
        var hits = 0;
        service.StateChanged += (_, _) => Interlocked.Increment(ref hits);

        await service.StartAsync(CancellationToken.None);

        Assert.True(Volatile.Read(ref hits) > 0);
    }

    private static PluginSettings NewSettings() => new()
    {
        MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.ExternalPreferred
    };

    private static MediaLinkUpstreamHostedService NewService(
        PluginSettings settings, out MediaLinkInjectionStore store)
    {
        var media = new FakeMediaService();
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        store = new MediaLinkInjectionStore();
        var coordinator = new MediaSourceCoordinator(media, lyrics, store, () => settings);
        return new MediaLinkUpstreamHostedService(store, coordinator, () => settings);
    }
}
