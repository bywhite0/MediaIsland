using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MediaIsland.Models;
using MediaIsland.Services.Audio;
using MediaIsland.Services.Audio.Native;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace MediaIsland.Tests.Audio;

/// <summary>
/// 真机全链路：native WASAPI 采集 → AudioFrameHub → MediaLinkAudioBroadcaster
/// → MediaLinkSession → **真实 WebSocket** → 客户端解码。
///
/// 与 <see cref="WasapiLoopbackManualCheck"/> 的区别：那条只验采集本身，这条把协议
/// 也串进来，覆盖「客户端订阅 audio + play_start 后真的收到能解码的帧」这一完整承诺。
///
/// **默认跳过**——需真实音频设备且正在放音。手工验证时去掉 Skip 再跑，
/// 步骤见 AGENTS.md 的 "MediaLink Audio Capture Check"。
/// </summary>
[Collection(nameof(AudioNativeCollection))]
public class MediaLinkAudioEndToEndManualCheck(ITestOutputHelper output)
{
    [Fact(Skip = "手工验证：需真实音频设备且正在放音。见 AGENTS.md 的 MediaLink Audio Capture Check")]
    public async Task CapturedAudioReachesSubscriberOverRealWebSocket()
    {
        AudioCaptureNative.ResetForTesting();

        var mediaService = new FakeMediaService();
        var currentTrack = new MediaInfo(
            SourceApp: "E2E.exe",
            Title: "端到端曲目",
            Artist: "艺人",
            AlbumTitle: "专辑",
            Position: TimeSpan.FromSeconds(30),
            Duration: TimeSpan.FromMinutes(4),
            PlaybackInfo: new MediaPlaybackInfo(MediaPlaybackState.Playing),
            Thumbnail: null,
            ThumbnailSource: null);
        mediaService.Raise(currentTrack, MediaInfoChangeKind.CurrentSession);

        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var store = new MediaLinkInjectionStore();
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly,
            MediaLinkPushUsesEffective = true
        };
        using var coordinator = new MediaSourceCoordinator(mediaService, lyrics, store, () => settings);

        var hub = new MediaLinkSessionHub();
        using var publisher = new MediaLinkStatePublisher(coordinator, hub, timelineMinIntervalMs: () => 0);
        publisher.Start();

        // 真实采集链路，与 MediaLinkHostedService 的接线一致。
        using var audioSource = new WasapiLoopbackFrameSource();
        Assert.True(audioSource.IsAvailable, audioSource.FailureReason);
        using var audioHub = new AudioFrameHub(audioSource);
        using var sinkSubscription = audioHub.AddSink(
            new MediaLinkAudioBroadcaster(hub, () => coordinator.GetMediaForPush()));

        const string token = "e2e-audio-token";
        var server = new MediaLinkServer(
            hub,
            session => publisher.PublishSnapshotAsync(session),
            () => token,
            coordinator: coordinator,
            onAudioCaptureDemandChangedAsync: () =>
                audioHub.SetCaptureDemandAsync(hub.HasAudioCaptureDemand, CancellationToken.None));

        await server.StartAsync("127.0.0.1", 0);
        var port = int.Parse(server.Endpoint!.Split(':')[2].Split('/')[0]);

        try
        {
            using var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/v1/ws"), CancellationToken.None);

            await ReceiveTextAsync(client); // server.hello
            await SendAsync(client, """{"type":"auth","id":"a1","v":1,"ts":0,"payload":{"token":"e2e-audio-token"}}""");
            await ReceiveTextAsync(client); // auth_ok

            Assert.False(audioHub.IsCapturing, "订阅前不该采集");

            await SendAsync(client,
                """{"type":"subscribe","id":"s1","v":1,"ts":0,"payload":{"channels":["media","audio"]}}""");

            // 订阅后仍不该采集——还没发 play_start。
            await Task.Delay(200);
            Assert.False(audioHub.IsCapturing, "只订阅未 play_start 不该采集");

            await SendAsync(client, """{"type":"audio.play_start","id":"p1","v":1,"ts":0}""");

            await WaitUntilAsync(() => audioHub.IsCapturing, "采集未在 play_start 后启动");
            output.WriteLine("采集已启动，等待音频帧…（请确保正在放音）");

            var (header, pcmLength, frameCount) = await CollectAudioFramesAsync(client, TimeSpan.FromSeconds(3));

            output.WriteLine($"收到二进制帧 : {frameCount}");
            output.WriteLine($"trackToken   : {header.TrackToken}");
            output.WriteLine($"startPosition: {header.StartPositionMs} ms");
            output.WriteLine($"capturedAt   : {header.CapturedAtMs}");
            output.WriteLine($"serverTime   : {header.ServerTimeMs}");
            output.WriteLine($"seq          : {header.Seq}");
            output.WriteLine($"flags        : {header.Flags}");
            output.WriteLine($"PCM 字节     : {pcmLength}");

            Assert.True(frameCount > 0, "未通过 WebSocket 收到任何音频帧");

            // trackToken 必须与 media.updated 同源，否则合规客户端会丢弃全部音频。
            var expectedToken = MediaIsland.Services.MediaLink.Mapping.MediaLinkDtoMapper
                .ToMediaDto(currentTrack, MediaInfoChangeKind.CurrentSession)!.TrackToken;
            Assert.Equal(expectedToken, header.TrackToken);

            Assert.True(header.CapturedAtMs <= header.ServerTimeMs, "采样时刻不该晚于发送时刻");
            Assert.True(pcmLength > 0 && pcmLength % (Channels * sizeof(short)) == 0,
                $"PCM 长度 {pcmLength} 不是完整帧的整数倍");

            // 断连即撤销意愿——这是第 2 期的核心改动，不依赖客户端补发 play_stop。
            //
            // 用 Abort 而非 CloseAsync：前者直接掐断 TCP，正是「客户端进程被杀」的形态，
            // 也是本期真正要守住的场景。CloseAsync 在这里还会因服务端不回关闭握手而抛，
            // 那是第 1 期就有的既有行为，与采集生命周期无关。
            client.Abort();

            await WaitUntilAsync(() => !audioHub.IsCapturing, "客户端断开后采集未停止");
            output.WriteLine("客户端被强行断开后采集已自动停止 ✓");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            await server.DisposeAsync();
        }
    }

    private const int Channels = MediaLinkProtocol.AudioChannels;

    private static async Task SendAsync(ClientWebSocket client, string json) =>
        await client.SendAsync(
            Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<string> ReceiveTextAsync(ClientWebSocket client)
    {
        var buffer = new byte[64 * 1024];
        var result = await client.ReceiveAsync(buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    /// <summary>
    /// 收集二进制帧直到超时。解码放在同步方法里——ReadOnlySpan 是 ref struct，
    /// C# 12 不允许它出现在 async 方法体内（CS9202）。
    /// </summary>
    private static async Task<(MediaLinkAudioFrameHeader Header, int PcmLength, int FrameCount)>
        CollectAudioFramesAsync(ClientWebSocket client, TimeSpan duration)
    {
        var buffer = new byte[128 * 1024];
        var deadline = DateTime.UtcNow + duration;
        var frameCount = 0;
        MediaLinkAudioFrameHeader header = default;
        var pcmLength = 0;

        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            WebSocketReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(buffer, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                continue; // media.updated 等文本帧
            }

            if (TryDecodeFirst(buffer.AsSpan(0, result.Count), ref header, ref pcmLength))
            {
                frameCount++;
            }
        }

        return (header, pcmLength, frameCount);

        static bool TryDecodeFirst(
            ReadOnlySpan<byte> frame, ref MediaLinkAudioFrameHeader header, ref int pcmLength)
        {
            if (!MediaLinkAudioFrame.TryDecode(frame, out var decoded, out var pcm, out _))
            {
                return false;
            }

            header = decoded;
            pcmLength = pcm.Length;
            return true;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        throw new TimeoutException(message);
    }

    private sealed class FakeMediaService : IMediaService
    {
        public event EventHandler<MediaInfoChangedEventArgs>? MediaInfoChanged;

        public MediaInfo? CurrentMediaInfo { get; set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EnsureStartedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Raise(MediaInfo? info, MediaInfoChangeKind kind)
        {
            CurrentMediaInfo = info;
            MediaInfoChanged?.Invoke(this, new MediaInfoChangedEventArgs(info, kind));
        }
    }
}
