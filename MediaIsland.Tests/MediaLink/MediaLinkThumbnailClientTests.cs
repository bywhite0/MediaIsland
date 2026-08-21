using Avalonia.Media.Imaging;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink;
using MediaIsland.Services.MediaLink.Mapping;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 接收侧的封面链路：客户端问、存储收、切歌作废。
///
/// 断言止于「封面已解析到哪一首」而非位图本身：<c>new Bitmap(stream)</c> 需要
/// <c>IPlatformRenderInterface</c>，测试进程无渲染后端，构造即抛。
/// 而这条链路的全部风险在于「问不问」与「配给谁」，两者都不需要真图。
/// </summary>
public class MediaLinkThumbnailClientTests
{
    [Fact]
    public void SetThumbnail_MatchingToken_IsAccepted()
    {
        var store = new MediaLinkInjectionStore();
        InjectMedia(store, "Song");

        Assert.True(store.TrySetThumbnail(TokenOf(store), thumbnail: null));
        Assert.Equal(TokenOf(store), store.ResolvedThumbnailToken);
    }

    [Fact]
    public void SetThumbnail_NoMedia_IsRejected()
    {
        // 无媒体时无从校验归属，接受它就等于把图配给下一首。
        var store = new MediaLinkInjectionStore();

        Assert.False(store.TrySetThumbnail("any-token", thumbnail: null));
        Assert.Null(store.ResolvedThumbnailToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SetThumbnail_MissingToken_IsRejected(string? token)
    {
        var store = new MediaLinkInjectionStore();
        InjectMedia(store, "Song");

        Assert.False(store.TrySetThumbnail(token, thumbnail: null));
        Assert.Null(store.ResolvedThumbnailToken);
    }

    [Fact]
    public void SetThumbnail_StaleToken_IsRejected()
    {
        // 回复在途中又切了歌。装上就是把上一首的图配给新曲目。
        var store = new MediaLinkInjectionStore();
        InjectMedia(store, "Old");
        var staleToken = TokenOf(store);
        InjectMedia(store, "New");

        Assert.False(store.TrySetThumbnail(staleToken, thumbnail: null));
        Assert.Null(store.ResolvedThumbnailToken);
    }

    [Fact]
    public void TrackChange_InvalidatesResolvedThumbnail()
    {
        // 换歌即作废。不作废则新曲目在等封面的空窗期里顶着上一首的图。
        var store = new MediaLinkInjectionStore();
        InjectMedia(store, "Old");
        store.TrySetThumbnail(TokenOf(store), thumbnail: null);
        Assert.NotNull(store.ResolvedThumbnailToken);

        InjectMedia(store, "New");

        Assert.Null(store.ResolvedThumbnailToken);
    }

    [Fact]
    public void PositionUpdateOnSameTrack_KeepsResolvedThumbnail()
    {
        // 位置前进不是换歌。跟着作废会让播放期间每条更新都重问一次封面。
        var store = new MediaLinkInjectionStore();
        InjectMedia(store, "Song", positionMs: 0);
        store.TrySetThumbnail(TokenOf(store), thumbnail: null);
        var resolved = store.ResolvedThumbnailToken;

        InjectMedia(store, "Song", positionMs: 30_000, kind: MediaInfoChangeKind.Timeline);

        Assert.Equal(resolved, store.ResolvedThumbnailToken);
    }

    [Fact]
    public void ClearInject_DropsResolvedThumbnail()
    {
        var store = new MediaLinkInjectionStore();
        InjectMedia(store, "Song");
        store.TrySetThumbnail(TokenOf(store), thumbnail: null);

        Assert.True(store.TryClear([MediaLinkProtocol.ChannelMedia], out _));

        Assert.Null(store.ResolvedThumbnailToken);
    }

    [Fact]
    public void SetThumbnail_RepeatedEmptyResult_RaisesChangedOnce()
    {
        // 上游对无封面曲目会反复回空。每次都发 Changed 会让组件反复重刷 UI。
        var store = new MediaLinkInjectionStore();
        InjectMedia(store, "Song");
        var hits = 0;
        store.Changed += (_, _) => hits++;

        store.TrySetThumbnail(TokenOf(store), thumbnail: null);
        store.TrySetThumbnail(TokenOf(store), thumbnail: null);

        Assert.Equal(1, hits);
    }

    [Fact]
    public void SetThumbnail_ReportsMediaProperties_NotCurrentSession()
    {
        // 封面变化不是换会话。报成 CurrentSession 会让歌词组件走整条重载路径，
        // 把高亮行与间奏动画清零——表现为封面一到歌词就闪一下。
        var store = new MediaLinkInjectionStore();
        InjectMedia(store, "Song");
        MediaInfoChangeKind? kind = null;
        store.Changed += (_, e) => kind = e.ChangeKind;

        store.TrySetThumbnail(TokenOf(store), thumbnail: null);

        Assert.Equal(MediaInfoChangeKind.MediaProperties, kind);
    }

    [Fact]
    public async Task Client_RequestThumbnail_SendsGetWithToken()
    {
        var socket = new ScriptedClientSocket();
        socket.QueueServerHello(epoch: 1);
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        await using var client = NewClient(socket);

        client.Start();
        await socket.WaitForSendsAsync(2);
        await client.RequestThumbnailAsync("abc123");

        var sent = socket.Sent.Select(j => MediaLinkMessageSerializer.Deserialize(j)!)
            .Single(m => m.Type == MediaLinkProtocol.TypeThumbnailGet);
        var payload = MediaLinkMessageSerializer
            .DeserializePayload<MediaLinkThumbnailGetPayload>(sent.Payload);
        Assert.Equal("abc123", payload!.TrackToken);
    }

    [Fact]
    public async Task Client_RequestThumbnail_WhileDisconnected_IsNoOp()
    {
        // 未连接时请求没有排队价值：下一条 media.updated 会再触发一次。
        var socket = new ScriptedClientSocket();
        await using var client = NewClient(socket);

        await client.RequestThumbnailAsync("abc123");

        Assert.Empty(socket.Sent);
    }

    [Fact]
    public async Task Client_ThumbnailReply_RaisesEventWithDecodedBytes()
    {
        var socket = new ScriptedClientSocket();
        socket.QueueServerHello(epoch: 1);
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        socket.QueueThumbnail(trackToken: "abc", dataBase64: Convert.ToBase64String([1, 2, 3]));
        await using var client = NewClient(socket);

        var signal = new TaskCompletionSource<MediaLinkThumbnailReceivedEventArgs>();
        client.ThumbnailReceived += (_, e) => signal.TrySetResult(e);
        client.Start();

        var received = await signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("abc", received.TrackToken);
        Assert.Equal<byte[]>([1, 2, 3], received.Data!);
    }

    [Fact]
    public async Task Client_ThumbnailReply_WithoutSeq_IsNotDiscarded()
    {
        // 封面回复是响应帧，不带 seq。若按事件走 seq 闸，它会被静默吃掉——
        // 这正是封面显示不出来时最难看出的一种失效。
        var socket = new ScriptedClientSocket();
        socket.QueueServerHello(epoch: 1);
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        socket.QueueMediaUpdated(seq: 100, title: "Song");   // 把水位抬到 100
        socket.QueueThumbnail(trackToken: "abc", dataBase64: Convert.ToBase64String([9]));
        await using var client = NewClient(socket);

        var signal = new TaskCompletionSource();
        client.ThumbnailReceived += (_, _) => signal.TrySetResult();
        client.Start();

        await signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Client_ThumbnailReply_EmptyData_RaisesEventWithNull()
    {
        // 空数据是协议规定的正常回复，不是错误：客户端必须照样上报，
        // 上层据此把残留的旧图清掉。
        var socket = new ScriptedClientSocket();
        socket.QueueServerHello(epoch: 1);
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        socket.QueueThumbnail(trackToken: "abc", dataBase64: null);
        await using var client = NewClient(socket);

        var signal = new TaskCompletionSource<MediaLinkThumbnailReceivedEventArgs>();
        client.ThumbnailReceived += (_, e) => signal.TrySetResult(e);
        client.Start();

        var received = await signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("abc", received.TrackToken);
        Assert.Null(received.Data);
    }

    [Fact]
    public async Task Client_ThumbnailReply_MalformedBase64_TreatedAsNoThumbnail()
    {
        // 畸形字段不该把连接带下去：协议要求容忍不合规输入。
        var socket = new ScriptedClientSocket();
        socket.QueueServerHello(epoch: 1);
        socket.QueueMessage(MediaLinkProtocol.TypeAuthOk);
        socket.QueueThumbnail(trackToken: "abc", dataBase64: "!!!not-base64!!!");
        socket.QueueMediaUpdated(seq: 1, title: "Survived");
        await using var client = NewClient(socket);

        var thumbnail = new TaskCompletionSource<MediaLinkThumbnailReceivedEventArgs>();
        var media = new TaskCompletionSource<string>();
        client.ThumbnailReceived += (_, e) => thumbnail.TrySetResult(e);
        client.MediaReceived += (_, e) => media.TrySetResult(e.Media.Title!);
        client.Start();

        Assert.Null((await thumbnail.Task.WaitAsync(TimeSpan.FromSeconds(5))).Data);
        Assert.Equal("Survived", await media.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void MediaSnapshot_HandsOutStoredThumbnail_NotHardcodedNull()
    {
        // 这是本次修复的核心：存储此前把 Thumbnail 硬写 null，接收端组件与中继转发
        // 因此都拿不到图。断言必须区分「交出了存着的那张」与「交出常量 null」，
        // 而位图在测试进程里造不出来（无渲染后端），故用未初始化实例占位——
        // 只比引用，不触碰任何需要渲染后端的成员。
        var store = new MediaLinkInjectionStore();
        InjectMedia(store, "Song");
        var sentinel = (Bitmap)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(Bitmap));

        Assert.True(store.TrySetThumbnail(TokenOf(store), sentinel));

        var snapshot = store.GetMediaSnapshot()!;
        Assert.Same(sentinel, snapshot.Thumbnail);
        // ThumbnailSource 保持 null：注入的图在上游已按上游设置裁过，
        // 本机不得再走组件里那条 Spotify 裁标路径，否则会被裁第二次。
        Assert.Null(snapshot.ThumbnailSource);
    }

    [Fact]
    public void SetThumbnail_ReplacingBitmapOnSameTrack_RaisesChanged()
    {
        // 同一曲目换图必须通知：上游可能先回空、封面就绪后再回一张真图。
        var store = new MediaLinkInjectionStore();
        InjectMedia(store, "Song");
        store.TrySetThumbnail(TokenOf(store), thumbnail: null);
        var hits = 0;
        store.Changed += (_, _) => hits++;

        var bitmap = (Bitmap)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(Bitmap));
        Assert.True(store.TrySetThumbnail(TokenOf(store), bitmap));

        Assert.Equal(1, hits);
        Assert.Same(bitmap, store.GetMediaSnapshot()!.Thumbnail);
    }

    private static string TokenOf(MediaLinkInjectionStore store)
    {
        var media = store.GetMediaSnapshot()!;
        return MediaLinkDtoMapper.ComputeTrackToken(
            media.SourceApp, media.Title, media.Artist, media.AlbumTitle);
    }

    private static void InjectMedia(
        MediaLinkInjectionStore store,
        string title,
        long positionMs = 0,
        MediaInfoChangeKind kind = MediaInfoChangeKind.MediaProperties)
    {
        Assert.True(store.TrySetMedia(new MediaLinkMediaInjectPayload
        {
            SourceApp = "upstream",
            Title = title,
            Artist = "Artist",
            PositionMs = positionMs,
            DurationMs = 180_000,
            PlaybackState = "Playing",
            PlaybackRate = 1.0
        }, out _, kind));
    }

    private static MediaLinkClient NewClient(ScriptedClientSocket socket) =>
        new(new MediaLinkClientOptions
        {
            Endpoint = new Uri("ws://127.0.0.1:1/v1/ws"),
            Token = "tok",
            InitialRetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(200),
            SocketFactory = () => socket
        });
}
