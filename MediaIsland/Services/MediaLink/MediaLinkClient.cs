using MediaIsland.Services.MediaLink.Protocol;
using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MediaIsland.Services.MediaLink;

/// <summary>客户端侧的 WebSocket 抽象，便于在无真实 socket 的情况下测试协议状态机。</summary>
public interface IMediaLinkClientSocket : IAsyncDisposable
{
    WebSocketState State { get; }

    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    Task SendTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>收下一条消息，文本与二进制均可；连接关闭时 Text 与 Binary 皆为 null。</summary>
    Task<MediaLinkSocketMessage> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 只收文本，跳过二进制帧。与服务端侧同构：既有调用方不消费音频，
    /// 收到二进制返回 null 会被误判为连接关闭。
    /// </summary>
    async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var message = await ReceiveAsync(cancellationToken);
            if (message.IsClosed)
            {
                return null;
            }

            if (message.Text is { } text)
            {
                return text;
            }
        }
    }
}

public sealed class ClientWebSocketAdapter : IMediaLinkClientSocket
{
    /// <summary>单条入站消息上限，与服务端一致。</summary>
    private const int MaxMessageBytes = 2 * 1024 * 1024;

    private readonly ClientWebSocket _socket = new();
    private readonly byte[] _buffer = new byte[64 * 1024];

    public WebSocketState State => _socket.State;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        _socket.ConnectAsync(uri, cancellationToken);

    public Task SendTextAsync(string text, CancellationToken cancellationToken) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken);

    /// <summary>
    /// 组装一条完整消息，文本与二进制同等对待。此前遇非 Text 就 continue，
    /// 服务端推来的音频帧会被静默丢弃——发出去等于没发。
    /// </summary>
    public async Task<MediaLinkSocketMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var isBinary = false;
        var started = false;

        while (true)
        {
            var result = await _socket.ReceiveAsync(_buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return default;
            }

            if (!started)
            {
                isBinary = result.MessageType == WebSocketMessageType.Binary;
                started = true;
            }

            if (message.Length + result.Count > MaxMessageBytes)
            {
                return default; // 服务端不该发这么大的帧，视同协议故障
            }

            message.Write(_buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return isBinary
                    ? new MediaLinkSocketMessage(null, message.ToArray())
                    : new MediaLinkSocketMessage(
                        Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length), null);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
            }
        }
        catch
        {
            // 关闭握手失败无所谓，接着释放
        }

        _socket.Dispose();
    }
}

public sealed class MediaLinkClientOptions
{
    public required Uri Endpoint { get; init; }

    public required string Token { get; init; }

    public IReadOnlyList<string> Channels { get; init; } = [MediaLinkProtocol.ChannelMedia, MediaLinkProtocol.ChannelLyrics];

    /// <summary>重连退避的起始与上限值。</summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>握手到 auth_ok 的上限，防止对端接受连接后不作声。</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public Func<IMediaLinkClientSocket> SocketFactory { get; init; } = () => new ClientWebSocketAdapter();
}

public sealed class MediaLinkMediaReceivedEventArgs(MediaLinkMediaDto media, long receivedAtTick) : EventArgs
{
    public MediaLinkMediaDto Media { get; } = media;

    /// <summary>收帧时的本地单调时钟读数，用于计算 positionAgeMs。</summary>
    public long ReceivedAtTick { get; } = receivedAtTick;
}

public sealed class MediaLinkLyricsReceivedEventArgs(MediaLinkLyricsDto lyrics) : EventArgs
{
    public MediaLinkLyricsDto Lyrics { get; } = lyrics;
}

/// <summary>
/// 收到一份封面回复。<c>Data</c> 为 null 表示「当前无可用封面」——协议规定
/// 无封面、超限与 trackToken 失配都走这条，且都不是错误。
/// </summary>
public sealed class MediaLinkThumbnailReceivedEventArgs(string? trackToken, byte[]? data) : EventArgs
{
    /// <summary>本份封面所属的曲目。请求方据此判断它是否已经过期。</summary>
    public string? TrackToken { get; } = trackToken;

    public byte[]? Data { get; } = data;
}

public sealed class MediaLinkAudioFrameReceivedEventArgs(byte[] frame) : EventArgs
{
    /// <summary>完整的协议二进制帧（含帧头）。解码由 MediaLinkAudioReceiver 负责——
    /// 客户端只认「这是一条二进制消息」，不认它的内部结构。</summary>
    public byte[] Frame { get; } = frame;
}

/// <summary>
/// 消费另一台实例 MediaLink 服务的客户端。负责连接、认证、订阅与重连，
/// 并按协议处理 sessionEpoch 与 seq；不决定收到的数据如何使用。
/// </summary>
public sealed class MediaLinkClient : IAsyncDisposable
{
    private readonly MediaLinkClientOptions _options;
    private readonly ILogger? _logger;
    private readonly Func<long> _tickProvider;
    private CancellationTokenSource? _runCts;
    private Task? _runLoop;

    private long _sessionEpoch = -1;
    private long _lastSeq;
    private IReadOnlyCollection<string> _serverCapabilities = [];
    private long _serverAudioClockBudgetMs = NoBudgetDeclared;

    /// <summary>播放延迟预算的空值哨兵。见 <see cref="ServerAudioClockBudgetMs"/>。</summary>
    private const long NoBudgetDeclared = long.MinValue;
    private volatile bool _audioSubscriptionWanted;
    private IMediaLinkClientSocket? _activeSocket;

    /// <summary>
    /// 服务端在 server.hello 声明的可选能力。第 3 期据此决定是否订阅 audio——
    /// subscribe 是整体请求，向不支持的服务端请求 audio 会让 media 与 lyrics 一起被拒。
    ///
    /// 用 Volatile 读写：写入发生在握手线程，而本属性会被需求变化线程读取来决定订阅内容。
    /// 第 1 期判定「当前无消费者，暂不加屏障」，本期那两条线程真实存在了。
    /// </summary>
    public IReadOnlyCollection<string> ServerCapabilities => Volatile.Read(ref _serverCapabilities);

    public bool SupportsAudio => ServerCapabilities.Contains(MediaLinkProtocol.CapabilityAudio);

    public bool SupportsAudioClock =>
        ServerCapabilities.Contains(MediaLinkProtocol.CapabilityAudioClock);

    /// <summary>
    /// 服务端声明的播放延迟预算，毫秒。未声明时为 null。
    ///
    /// 缺失不回落到默认值：两端各自默认成同一个数看起来一致，实则是两份各自为真的
    /// 声明，改了一端就静默失配，而症状是两台机器差一个固定的量、像硬件延迟。
    /// 判定见 <see cref="MediaLinkAlignmentPolicy"/>。
    ///
    /// 用哨兵而不是 <c>long?</c> 存内部字段：可空类型没有原子读写，而这个值写在握手
    /// 线程、读在播放线程。哨兵取 <see cref="long.MinValue"/> 而不是 -1——负预算是
    /// 「声明了一个办不到的值」，与「没声明」的排查方向不同（改服务端配置 / 查服务端版本），
    /// 拿 -1 当空值会把前者吞成后者。
    /// </summary>
    public long? ServerAudioClockBudgetMs =>
        Volatile.Read(ref _serverAudioClockBudgetMs) is var value && value != NoBudgetDeclared
            ? value
            : null;

    public MediaLinkClient(MediaLinkClientOptions options, ILogger? logger = null, Func<long>? tickProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _tickProvider = tickProvider ?? (() => Environment.TickCount64);
    }

    public bool IsConnected { get; private set; }

    public string? LastError { get; private set; }

    public event EventHandler<MediaLinkMediaReceivedEventArgs>? MediaReceived;

    public event EventHandler<MediaLinkLyricsReceivedEventArgs>? LyricsReceived;

    /// <summary>收到一条二进制消息。本期只有音频帧走二进制通道。</summary>
    public event EventHandler<MediaLinkAudioFrameReceivedEventArgs>? AudioFrameReceived;

    /// <summary>收到 <c>thumbnail</c> 回复。请求由 <see cref="RequestThumbnailAsync"/> 发出。</summary>
    public event EventHandler<MediaLinkThumbnailReceivedEventArgs>? ThumbnailReceived;

    /// <summary>连接状态变化（含重连中断），供 UI 显示。</summary>
    public event EventHandler? ConnectionStateChanged;

    public void Start()
    {
        if (_runLoop is not null)
        {
            return;
        }

        _runCts = new CancellationTokenSource();
        _runLoop = Task.Run(() => RunWithRetryAsync(_runCts.Token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        if (_runCts is null)
        {
            return;
        }

        try { await _runCts.CancelAsync(); } catch { /* ignore */ }

        if (_runLoop is not null)
        {
            try { await _runLoop; } catch { /* ignore */ }
            _runLoop = null;
        }

        _runCts.Dispose();
        _runCts = null;
        SetConnected(false);
    }

    private async Task RunWithRetryAsync(CancellationToken cancellationToken)
    {
        var delay = _options.InitialRetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(cancellationToken);
                delay = _options.InitialRetryDelay; // 成功连过一次就重置退避
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _logger?.LogDebug(ex, "MediaLink 客户端连接中断");
            }
            finally
            {
                SetConnected(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // 指数退避：对端长期不可用时不至于每秒敲一次门。
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, _options.MaxRetryDelay.TotalMilliseconds));
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var socket = _options.SocketFactory();
        await socket.ConnectAsync(_options.Endpoint, cancellationToken);

        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeCts.CancelAfter(_options.HandshakeTimeout);

        // server.hello → auth → auth_ok → subscribe → subscribe_ok
        await ExpectServerHelloAsync(socket, handshakeCts.Token);
        await SendAsync(socket, MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeAuth,
            new MediaLinkAuthPayload { Token = _options.Token },
            id: "auth"), handshakeCts.Token);
        await ExpectAuthResultAsync(socket, handshakeCts.Token);
        await SendAsync(socket, MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeSubscribe,
            new MediaLinkSubscribePayload { Channels = BuildChannels() },
            id: "sub"), handshakeCts.Token);

        // 赋值与 SetConnected 都放进 try：连接态事件是同步派发的，订阅者抛异常会从这里
        // 穿出去，若赋值在 try 外，_activeSocket 会留着指向已 Dispose 的 socket
        // 直到下次握手覆盖它。放进来则由 finally 兜底清空。
        try
        {
            // 先记活动 socket，再宣告连上。顺序要紧：SetConnected 是同步派发事件的，
            // 订阅方会就地重算订阅意愿并调 SetAudioSubscribedAsync。若那时 _activeSocket
            // 还是空，该调用会把意愿旗标置真却发不出 subscribe，而下面只补 audio.play_start
            // ——上游起了采集，本会话却没订阅 audio 频道，广播跳过它，表现为静默零帧；
            // 且旗标已为真，后续重算被相同值去重掐掉，只能等重连才恢复。
            //
            // 握手完成前不记：那时发控制帧没有意义。
            _activeSocket = socket;

            SetConnected(true);
            LastError = null;

            if (_audioSubscriptionWanted && SupportsAudio)
            {
                await SendAsync(socket, MediaLinkMessageSerializer.Create(
                    MediaLinkProtocol.TypeAudioPlayStart, payload: null, id: "audio-start"), cancellationToken);
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(cancellationToken);
                if (received.IsClosed)
                {
                    return; // 对端关闭，交给重连逻辑
                }

                if (received.Binary is { } binary)
                {
                    AudioFrameReceived?.Invoke(this, new MediaLinkAudioFrameReceivedEventArgs(binary));
                    continue;
                }

                if (received.Text is { } text)
                {
                    Dispatch(text);
                }
            }
        }
        finally
        {
            _activeSocket = null;
        }
    }

    /// <summary>
    /// subscribe 是整体替换语义，且向不支持 audio 的老服务端请求 audio 会让
    /// media 与 lyrics 一起被拒。故 audio 只在对端声明支持时才加入，
    /// 且每次都发全量频道列表而非增量。
    /// </summary>
    private List<string> BuildChannels()
    {
        var channels = _options.Channels.ToList();
        var wantsAudio = _audioSubscriptionWanted && SupportsAudio;
        var hasAudio = channels.Contains(MediaLinkProtocol.ChannelAudio, StringComparer.Ordinal);

        if (wantsAudio && !hasAudio)
        {
            channels.Add(MediaLinkProtocol.ChannelAudio);
        }
        else if (!wantsAudio && hasAudio)
        {
            channels.RemoveAll(c => string.Equals(c, MediaLinkProtocol.ChannelAudio, StringComparison.Ordinal));
        }

        return channels;
    }

    /// <summary>
    /// 切换音频订阅意愿。意愿与连接解耦：未连接时只记下，下次连上自动带上——
    /// 否则组件在断线期间挂载，需求就永久丢失，得等用户手动重开组件才恢复。
    ///
    /// 重复传相同值是 no-op：需求侧的取向是「状态变了就无脑重算并调用」，
    /// 不去重会让每次需求事件都多一轮 subscribe 往返。
    /// </summary>
    public async Task SetAudioSubscribedAsync(bool subscribed)
    {
        if (_audioSubscriptionWanted == subscribed)
        {
            return;
        }

        _audioSubscriptionWanted = subscribed;

        var socket = _activeSocket;
        if (socket is null || !IsConnected || !SupportsAudio)
        {
            return;
        }

        try
        {
            await SendAsync(socket, MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeSubscribe,
                new MediaLinkSubscribePayload { Channels = BuildChannels() },
                id: "sub-audio"), CancellationToken.None);

            await SendAsync(socket, MediaLinkMessageSerializer.Create(
                subscribed ? MediaLinkProtocol.TypeAudioPlayStart : MediaLinkProtocol.TypeAudioPlayStop,
                payload: null,
                id: subscribed ? "audio-start" : "audio-stop"), CancellationToken.None);
        }
        catch (Exception ex)
        {
            // 发送失败即连接已坏，重连逻辑会带着新意愿重新握手，不必在此重试。
            _logger?.LogDebug(ex, "切换音频订阅失败");
        }
    }

    /// <summary>
    /// 请求当前封面。回复经 <see cref="ThumbnailReceived"/> 异步到达，不在此处等待——
    /// 等待需要按 id 配对的挂起表，而封面回复只有一种消费者且天然幂等：
    /// 收到就装上，没收到就等下一次 media.updated 再问。为一张图引入请求-响应表
    /// 是不必要的复杂度。
    ///
    /// 未连接时静默丢弃：请求本身没有排队价值，下一条 media.updated 会再触发一次。
    /// </summary>
    /// <param name="trackToken">
    /// 期望的曲目。服务端据此校验，失配时回空数据而非上一首的图。
    /// </param>
    public async Task RequestThumbnailAsync(string? trackToken, CancellationToken cancellationToken = default)
    {
        var socket = _activeSocket;
        if (socket is null || !IsConnected)
        {
            return;
        }

        try
        {
            await SendAsync(socket, MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeThumbnailGet,
                new MediaLinkThumbnailGetPayload { TrackToken = trackToken },
                id: "th"), cancellationToken);
        }
        catch (Exception ex)
        {
            // 发送失败即连接已坏，重连后下一条 media.updated 会再问一次，不必在此重试。
            _logger?.LogDebug(ex, "请求封面失败");
        }
    }

    private async Task ExpectServerHelloAsync(IMediaLinkClientSocket socket, CancellationToken cancellationToken)
    {
        var message = await ReceiveMessageAsync(socket, cancellationToken)
            ?? throw new InvalidOperationException("连接后未收到 server.hello");

        if (message.Type != MediaLinkProtocol.TypeEvent ||
            message.Name != MediaLinkProtocol.EventServerHello)
        {
            throw new InvalidOperationException($"期望 server.hello，实际为 {message.Type}/{message.Name}");
        }

        var payload = MediaLinkMessageSerializer.DeserializePayload<MediaLinkServerHelloPayload>(message.Payload);
        ApplyServerHello(payload);
    }

    private async Task ExpectAuthResultAsync(IMediaLinkClientSocket socket, CancellationToken cancellationToken)
    {
        var message = await ReceiveMessageAsync(socket, cancellationToken)
            ?? throw new InvalidOperationException("认证期间连接被关闭");

        if (message.Type == MediaLinkProtocol.TypeAuthOk)
        {
            return;
        }

        // Token 错误不是暂时性故障，但仍走重连：用户可能正在设置页改 Token。
        throw new InvalidOperationException(
            message.Type == MediaLinkProtocol.TypeAuthFail ? "Token 不正确" : $"认证期间收到意外消息：{message.Type}");
    }

    /// <summary>
    /// 每条 server.hello 都是对端对自身当前状态的完整声明，握手时与会话中途收到的一律同等对待：
    /// 只在握手记一次，服务端重建会话后能力若有变化就无从察觉。
    /// 缺 capabilities 字段的老服务端落到空集合，即「不支持任何可选能力」。
    /// </summary>
    private void ApplyServerHello(MediaLinkServerHelloPayload? payload)
    {
        Volatile.Write(ref _serverCapabilities, payload?.Capabilities is { } capabilities
            ? new HashSet<string>(capabilities, StringComparer.Ordinal)
            : []);

        // server.hello 可以在连接存活期间重发并改 D，故这里是无条件覆写而非「仅首次」：
        // 重发把预算改小时接收端要能退回，改大时要能重新进入。
        // 只有字段真的缺失才记空值；声明出来的数原样保留，哪怕它办不到——
        // 「声明了 0」与「没声明」的排查方向不同。
        Volatile.Write(
            ref _serverAudioClockBudgetMs,
            payload?.AudioClock?.BudgetMs ?? NoBudgetDeclared);

        HandleEpoch(payload?.SessionEpoch ?? 0);
    }

    /// <summary>
    /// seq 在服务端重建监听器后从 0 重新开始。不跟着 sessionEpoch 重置水位，
    /// 客户端会因「丢弃 seq ≤ 已处理值」而永久停止更新。
    /// </summary>
    private void HandleEpoch(long epoch)
    {
        if (epoch == _sessionEpoch)
        {
            return;
        }

        _sessionEpoch = epoch;
        _lastSeq = 0;
    }

    private void Dispatch(string text)
    {
        MediaLinkMessage? message;
        try
        {
            message = MediaLinkMessageSerializer.Deserialize(text);
        }
        catch (JsonException ex)
        {
            _logger?.LogDebug(ex, "MediaLink 客户端收到无法解析的消息");
            return;
        }

        if (message is null)
        {
            return;
        }

        if (message.Type == MediaLinkProtocol.TypeEvent &&
            message.Name == MediaLinkProtocol.EventServerHello)
        {
            var hello = MediaLinkMessageSerializer.DeserializePayload<MediaLinkServerHelloPayload>(message.Payload);
            ApplyServerHello(hello);
            return;
        }

        // 封面回复是响应帧：不带 seq，也不是 event。故必须在下面那两道闸之前接住，
        // 否则会被「非 event 一律丢弃」那一句静默吃掉。
        if (message.Type == MediaLinkProtocol.TypeThumbnail)
        {
            var thumbnail = MediaLinkMessageSerializer.DeserializePayload<MediaLinkThumbnailPayload>(message.Payload);
            if (thumbnail is not null)
            {
                ThumbnailReceived?.Invoke(this, new MediaLinkThumbnailReceivedEventArgs(
                    thumbnail.TrackToken, DecodeBase64OrNull(thumbnail.DataBase64)));
            }

            return;
        }

        // 乱序或重复的事件直接丢弃；控制帧不带 seq，不参与此判断。
        if (message.Seq > 0)
        {
            if (message.Seq <= _lastSeq)
            {
                return;
            }

            _lastSeq = message.Seq;
        }

        if (message.Type != MediaLinkProtocol.TypeEvent)
        {
            return;
        }

        if (message.Name == MediaLinkProtocol.EventMediaUpdated)
        {
            var dto = MediaLinkMessageSerializer.DeserializePayload<MediaLinkMediaDto>(message.Payload);
            if (dto is not null)
            {
                MediaReceived?.Invoke(this, new MediaLinkMediaReceivedEventArgs(dto, _tickProvider()));
            }

            return;
        }

        if (message.Name == MediaLinkProtocol.EventLyricsUpdated)
        {
            var dto = MediaLinkMessageSerializer.DeserializePayload<MediaLinkLyricsDto>(message.Payload);
            if (dto is not null)
            {
                LyricsReceived?.Invoke(this, new MediaLinkLyricsReceivedEventArgs(dto));
            }
        }
    }

    /// <summary>
    /// 宽松解码：坏 base64 按「无封面」处理而非抛出。对端的这个字段是可选的，
    /// 一个畸形值不该把整条连接带下去——协议要求容忍不合规输入。
    /// </summary>
    private static byte[]? DecodeBase64OrNull(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        return Convert.TryFromBase64String(base64, new byte[base64.Length], out _)
            ? Convert.FromBase64String(base64)
            : null;
    }

    private static Task SendAsync(IMediaLinkClientSocket socket, MediaLinkMessage message, CancellationToken cancellationToken) =>
        socket.SendTextAsync(MediaLinkMessageSerializer.Serialize(message), cancellationToken);

    private static async Task<MediaLinkMessage?> ReceiveMessageAsync(IMediaLinkClientSocket socket, CancellationToken cancellationToken)
    {
        var text = await socket.ReceiveTextAsync(cancellationToken);
        return text is null ? null : MediaLinkMessageSerializer.Deserialize(text);
    }

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected)
        {
            return;
        }

        IsConnected = connected;
        ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
