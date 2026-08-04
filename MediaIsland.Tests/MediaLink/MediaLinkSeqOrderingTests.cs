using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Protocol;
using System.Net.WebSockets;
using System.Text.Json;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// seq 与入队顺序的一致性。客户端按「丢弃 seq ≤ 已处理值」过滤，
/// 因此帧到达的顺序必须与 seq 的大小顺序一致，否则较新的那条会被客户端丢掉。
///
/// seq 只在 MediaLinkStatePublisher 内分配，故必须经它的真实路径触发：
/// 快照走 PublishSnapshotAsync，增量走 coordinator 事件。
/// </summary>
public class MediaLinkSeqOrderingTests
{
    [Fact]
    public async Task SnapshotAndIncremental_ShareAllocator_NeverDuplicateSeq()
    {
        // 快照与增量是两条独立路径但共用一个分配器。二者交错时若产生重复 seq，
        // 客户端会把后到的那条当成"已处理过"而丢弃。
        var socket = new SlowFirstSendSocket(TimeSpan.FromMilliseconds(150));
        var media = new FakeMediaService();
        using var coordinator = CreateCoordinator(media);
        var hub = new MediaLinkSessionHub();
        var session = await AttachSessionAsync(hub, socket);

        using var publisher = new MediaLinkStatePublisher(coordinator, hub, timelineMinIntervalMs: () => 0);
        publisher.Start();

        RaiseTrack(media, "Song-0");

        var snapshot = publisher.PublishSnapshotAsync(session);
        RaiseTrack(media, "Song-1");
        RaiseTrack(media, "Song-2");
        await snapshot;

        await WaitForQuiescenceAsync(socket);

        var seqs = ExtractSeqs(socket.Sent);
        Assert.NotEmpty(seqs);
        Assert.Equal(seqs.Length, seqs.Distinct().Count());
    }

    [Fact]
    public async Task RapidIncrementalChanges_NoDuplicateOrReorderedSeq()
    {
        var socket = new SlowFirstSendSocket(TimeSpan.FromMilliseconds(120));
        var media = new FakeMediaService();
        using var coordinator = CreateCoordinator(media);
        var hub = new MediaLinkSessionHub();
        await AttachSessionAsync(hub, socket);

        using var publisher = new MediaLinkStatePublisher(coordinator, hub, timelineMinIntervalMs: () => 0);
        publisher.Start();

        // 非 Timeline 变更走 fire-and-forget 广播，是并发的真实来源。
        for (var i = 0; i < 12; i++)
        {
            RaiseTrack(media, $"Song-{i}");
        }

        await WaitForQuiescenceAsync(socket);

        var seqs = ExtractSeqs(socket.Sent);
        Assert.NotEmpty(seqs);
        Assert.Equal(seqs.Length, seqs.Distinct().Count());
        AssertSeqMonotonic(socket.Sent);
    }

    private static void AssertSeqMonotonic(IReadOnlyList<string> frames)
    {
        var seqs = ExtractSeqs(frames);
        Assert.Equal(seqs.OrderBy(s => s).ToArray(), seqs);
    }

    private static long[] ExtractSeqs(IReadOnlyList<string> frames) =>
        frames
            .Select(json => JsonDocument.Parse(json).RootElement)
            .Where(e => e.TryGetProperty("seq", out _))
            .Select(e => e.GetProperty("seq").GetInt64())
            .ToArray();

    private static MediaSourceCoordinator CreateCoordinator(FakeMediaService media)
    {
        var lyrics = new LyricsSearchService([], [], () => new LyricsSourceSettings());
        var settings = new PluginSettings
        {
            MediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly,
            MediaLinkPushUsesEffective = true
        };
        return new MediaSourceCoordinator(media, lyrics, new MediaLinkInjectionStore(), () => settings);
    }

    private static void RaiseTrack(FakeMediaService media, string title) =>
        media.Raise(
            new MediaInfo("app", title, "Artist", null, TimeSpan.Zero, TimeSpan.FromMinutes(3),
                new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null),
            MediaInfoChangeKind.MediaProperties);

    private static async Task<MediaLinkSession> AttachSessionAsync(MediaLinkSessionHub hub, IMediaLinkSocket socket)
    {
        var session = new MediaLinkSession(socket, new MediaLinkSessionOptions { ExpectedToken = "t" });
        hub.Add(session);
        session.StartWriter(CancellationToken.None);

        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeAuth, new MediaLinkAuthPayload { Token = "t" })),
            CancellationToken.None);
        await session.HandleMessageAsync(
            MediaLinkMessageSerializer.Serialize(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeSubscribe,
                new MediaLinkSubscribePayload { Channels = [MediaLinkProtocol.ChannelMedia] })),
            CancellationToken.None);
        return session;
    }

    /// <summary>等到连续 300ms 没有新帧，认为推送已收敛。</summary>
    private static async Task WaitForQuiescenceAsync(SlowFirstSendSocket socket)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        var lastCount = -1;
        var stableSince = DateTimeOffset.UtcNow;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var count = socket.Sent.Count;
            if (count != lastCount)
            {
                lastCount = count;
                stableSince = DateTimeOffset.UtcNow;
            }
            else if (count > 0 && (DateTimeOffset.UtcNow - stableSince).TotalMilliseconds > 300)
            {
                return;
            }

            await Task.Delay(20);
        }
    }

    /// <summary>首帧发送刻意变慢，放大分配与入队之间的窗口。</summary>
    private sealed class SlowFirstSendSocket(TimeSpan firstDelay) : IMediaLinkSocket
    {
        private readonly List<string> _sent = [];
        private readonly object _gate = new();
        private int _sends;

        public WebSocketState State { get; private set; } = WebSocketState.Open;

        public IReadOnlyList<string> Sent
        {
            get { lock (_gate) return _sent.ToArray(); }
        }

        public async Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _sends) == 1)
            {
                await Task.Delay(firstDelay, cancellationToken);
            }

            lock (_gate)
            {
                _sent.Add(text);
            }
        }

        public Task<string?> ReceiveTextAsync(CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => (string?)null, cancellationToken);

        public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        {
            State = WebSocketState.Closed;
            return Task.CompletedTask;
        }
    }
}
