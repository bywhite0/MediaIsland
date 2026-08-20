using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MediaIsland.Services.Media;
using MediaIsland.Services.Media.Platform;
using MediaIsland.Services.MediaLink.Mapping;
using MediaIsland.Services.MediaLink.Protocol;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 一条收到的 WebSocket 消息。<see cref="Text"/> 与 <see cref="Binary"/> 恰有一个非 null；
/// 两者皆 null 表示连接已关闭。用单一结构而非两个方法，是因为文本与二进制共用一条
/// 接收流，分成两个方法会让调用方无法保持它们的到达顺序。
/// </summary>
public readonly record struct MediaLinkSocketMessage(string? Text, byte[]? Binary)
{
    public bool IsClosed => Text is null && Binary is null;
}

public interface IMediaLinkSocket
{
    WebSocketState State { get; }

    Task SendTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>发送二进制帧。音频帧专用，不走 JSON 信封。</summary>
    Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>收下一条消息，文本与二进制均可。</summary>
    Task<MediaLinkSocketMessage> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 只收文本，跳过二进制帧。保留此方法是为了既有调用方无需改写——
    /// 它们不消费音频，收到二进制返回 null 会被误判为连接关闭。
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

    Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken);

    /// <summary>
    /// 只发关闭帧，不等对端回帧。停服时对端是否回握手与「让客户端看到 1001」无关：
    /// 帧一送达客户端就已知晓关闭原因，等待纯属浪费——而进程退出时我们没有这个时间。
    /// 默认转发到 <see cref="CloseAsync"/>，仅真实 WebSocket 覆写为单向关闭。
    /// </summary>
    Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken) =>
        CloseAsync(status, description, cancellationToken);
}

public sealed class WebSocketMediaLinkSocket(WebSocket webSocket) : IMediaLinkSocket, IDisposable
{
    /// <summary>单条文本消息的组装上限（2 MiB）。</summary>
    public const int MaxMessageBytes = 2 * 1024 * 1024;

    /// <summary>
    /// 单条二进制消息的组装上限，与文本分开。二进制必须更严，是因为认证判定在会话层的
    /// <c>HandleBinaryAsync</c> 才发生，而字节在这里就已全部落进托管堆并被 <c>ToArray()</c>
    /// 再复制一份——未认证连接可以在 AuthTimeout 窗口内反复让服务端为它缓冲满额。
    /// 合法音频帧 20ms 约 3.9 KB（3840 字节 PCM + 34 字节定长头 + trackToken），
    /// 文本那 2 MiB 的额度对二进制路径没有任何正当用途。
    ///
    /// 取 128 KiB 而不更小：接收缓冲本身是 64 KiB，上限若不高于它，入站二进制就再也
    /// 无法跨分片组装，而分片组装正是这条路径上拷贝时机唯一验得到的地方。
    /// </summary>
    public const int MaxBinaryMessageBytes = 128 * 1024;

    private readonly byte[] _buffer = new byte[64 * 1024];
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WebSocketState State => webSocket.State;

    public async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await webSocket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await webSocket.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<MediaLinkSocketMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var isBinary = false;
        var started = false;

        while (true)
        {
            var result = await webSocket.ReceiveAsync(_buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return default;
            }

            if (!started)
            {
                isBinary = result.MessageType == WebSocketMessageType.Binary;
                started = true;
            }

            // 阈值按帧类型取。isBinary 在上面的首分片分支里已经定下，故此处不会拿文本上限
            // 去量二进制，也不会反过来收紧文本。
            var maxBytes = isBinary ? MaxBinaryMessageBytes : MaxMessageBytes;
            if (message.Length + result.Count > maxBytes)
            {
                try
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.MessageTooBig,
                        "message too big",
                        cancellationToken);
                }
                catch
                {
                    // ignore close races
                }

                return default;
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

    public async Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await webSocket.CloseAsync(status, description, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// 只写出关闭帧，不等待对端的关闭响应。停服路径专用：宿主随时可能结束进程，
    /// 等待对端握手会让关闭帧根本来不及发出，客户端最终只看到 1006。
    /// </summary>
    public async Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await webSocket.CloseOutputAsync(status, description, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose() => _sendLock.Dispose();
}

public sealed class MediaLinkSessionOptions
{
    public required string ExpectedToken { get; init; }

    public TimeSpan AuthTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public Action? OnAuthFailed { get; init; }

    public Action? OnAuthSucceeded { get; init; }

    public Func<MediaLinkSession, Task>? OnSubscribedAsync { get; init; }

    // Phase 2
    public MediaLinkInjectionStore? InjectionStore { get; init; }

    public MediaSourceCoordinator? Coordinator { get; init; }

    public Func<IMediaPlaybackController?>? PlaybackControllerAccessor { get; init; }

    /// <summary>成功 mutator 后可选：通知 publisher 推送（Task 7 接线）。</summary>
    public Func<MediaInfoChangeKind, Task>? OnEffectiveMediaMutatedAsync { get; init; }

    public Func<Task>? OnEffectiveLyricsMutatedAsync { get; init; }

    /// <summary>
    /// 音频采集需求可能已变化，宿主应重算。
    ///
    /// 刻意是「重算信号」而非 start/stop 事件对：采集需求本质上是当前会话集合的纯函数
    /// （见 <see cref="MediaLinkSession.WantsAudioCapture"/>），而会话有五条终结路径。
    /// 用事件对表达就要求五条路径每条都精确补发 stop，漏一条即永久占着音频设备且无告警；
    /// 重算则是幂等的，漏触发最多延迟一拍，不会永久错。
    /// </summary>
    public Func<Task>? OnAudioCaptureDemandChangedAsync { get; init; }

    /// <summary>收到入站音频帧（转发链路与接收端用）。</summary>
    public Func<MediaLinkAudioFrameHeader, byte[], Task>? OnAudioFrameAsync { get; init; }
}

/// <summary>出站帧。<paramref name="Droppable"/> 决定队列满时它能否被牺牲。</summary>
internal readonly record struct MediaLinkOutboundFrame(string Json, bool Droppable);

/// <summary>
/// 每会话出站有界队列。相比 <c>Channel</c> 的 DropOldest，这里的丢弃是按可丢标记选择的：
/// 队列满时只挤掉最旧的可丢帧（<c>media.updated</c>，客户端有插值兜底），
/// 歌词与控制帧永不被挤掉。队头恰好是不可丢帧时 Channel 的 DropOldest 会丢错对象。
/// </summary>
internal sealed class MediaLinkOutboundQueue
{
    private readonly int _capacity;
    private readonly LinkedList<MediaLinkOutboundFrame> _items = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private bool _completed;

    public MediaLinkOutboundQueue(int capacity) => _capacity = capacity;

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    /// <summary>
    /// 入队。队列满时牺牲最旧的可丢帧为来者腾位置，不论来者本身可不可丢。
    /// 返回 false 表示队列已满且一条可丢帧都没有，调用方应以 <c>rate_limited</c> 关闭该会话。
    /// </summary>
    public bool TryEnqueue(MediaLinkOutboundFrame frame)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return true; // 会话正在结束，静默丢弃不算背压失败
            }

            if (_items.Count >= _capacity)
            {
                // 队列满时一律牺牲最旧的可丢帧，不论来者可不可丢——
                // 可丢帧是会被后继取代的采样值，积压的那些本就过期；
                // 而按来者的可丢性决定要不要腾位置，会让越重要的帧越先被拒。
                var victim = FindOldestDroppable();
                if (victim is null)
                {
                    // 满队且一条可丢的都没有：对端确实跟不上重要流量，交由调用方关闭会话。
                    return false;
                }

                _items.Remove(victim);
            }

            _items.AddLast(frame);
        }

        // 丢弃+新增时净数量不变，此处会多释放一次信号量。
        // 多余的唤醒是良性的：出队方拿不到帧就继续等待；少释放才会导致帧滞留。
        _signal.Release();
        return true;
    }

    /// <summary>取下一帧；队列被 <see cref="Complete"/> 且排空后返回 null。</summary>
    public async Task<MediaLinkOutboundFrame?> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_items.First is { } head)
                {
                    _items.RemoveFirst();
                    return head.Value;
                }

                if (_completed)
                {
                    return null;
                }
            }

            await _signal.WaitAsync(cancellationToken);
        }
    }

    /// <summary>非阻塞取帧：队列为空时返回 false，不等待新帧。</summary>
    public bool TryDequeue(out MediaLinkOutboundFrame frame)
    {
        lock (_gate)
        {
            if (_items.First is { } head)
            {
                _items.RemoveFirst();
                frame = head.Value;
                return true;
            }
        }

        frame = default;
        return false;
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
        }

        _signal.Release(); // 唤醒等待中的出队方，使其观察到 _completed
    }

    private LinkedListNode<MediaLinkOutboundFrame>? FindOldestDroppable()
    {
        for (var node = _items.First; node is not null; node = node.Next)
        {
            if (node.Value.Droppable)
            {
                return node;
            }
        }

        return null;
    }
}

public sealed class MediaLinkSession : IAsyncDisposable
{
    /// <summary>
    /// 单帧发送超时；超时视同该会话故障。
    ///
    /// internal 而非 private：停服的排水期限由它推导（见 MediaLinkServer.ShutdownDrainTimeout）。
    /// 那个期限必须随它变，写成字面量就不会跟。
    /// </summary>
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 等写者退出的上限。与 MediaLinkServer.ShutdownDrainTimeout 同一条推导：
    /// 写者退出前最多还压着一次发送，其界是 SendTimeout，另加一秒余量给循环退出。
    /// </summary>
    internal static readonly TimeSpan WriterDrainTimeout = SendTimeout + TimeSpan.FromSeconds(1);

    private readonly IMediaLinkSocket _socket;
    private readonly MediaLinkSessionOptions _options;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly HashSet<string> _channels = new(StringComparer.Ordinal);
    private bool _authenticated;
    private bool _closed;
    private bool _goingAwaySent;
    private readonly MediaLinkOutboundQueue _outbound = new(64);
    private Task? _writerTask;

    /// <summary>
    /// 音频出站队列，按帧数而非时长计量——帧长由音频设备决定，服务端选不了
    /// （WASAPI 共享模式下实测约 10ms/帧，8 帧即约 80ms）。
    ///
    /// 丢最旧的前提下，队列深度决定的只是「发送暂时卡顿时保留多少最新帧」，
    /// 而过期帧对可视化本就无价值，故容量不需要随帧长调整。
    /// </summary>
    private readonly MediaLinkAudioQueue _audioOutbound = new(8);
    private Task? _audioWriterTask;

    /// <summary>客户端是否发过 audio.play_start（且未被 play_stop 撤销）。受 _gate 保护。</summary>
    private bool _audioPlayRequested;

    public MediaLinkSession(IMediaLinkSocket socket, MediaLinkSessionOptions options, ILogger? logger = null)
    {
        _socket = socket;
        _options = options;
        _logger = logger;
    }

    public bool IsAuthenticated
    {
        get { lock (_gate) return _authenticated; }
    }

    /// <summary>会话是否已关闭（认证失败、队列溢出或连接断开）。</summary>
    public bool IsClosed
    {
        get { lock (_gate) return _closed; }
    }

    public IReadOnlyCollection<string> Channels
    {
        get { lock (_gate) return _channels.ToArray(); }
    }

    public bool IsSubscribedToAudio
    {
        get { lock (_gate) return _authenticated && _channels.Contains(MediaLinkProtocol.ChannelAudio); }
    }

    /// <summary>
    /// 此刻本会话是否需要音频采集。四个条件缺一不可：未关闭、已认证、订阅了 audio、请求过 play_start。
    ///
    /// 这是状态量而非事件计数，是刻意的。会话有五条终结路径——RunAsync 正常退出、auth 超时、
    /// rate_limited、CloseGoingAwayAsync、DisposeAllAsync——用「start/stop 配对」表达需求，
    /// 就要求五条路径每条都记得补发 stop：漏一条即永久占着音频设备，且没有任何信号提示。
    /// 状态量没有「路径」这个概念，故没有可漏的路径。
    ///
    /// 同理，重复的 play_start 天然幂等：读同一个状态量两次得到同一个值，无需判重代码。
    /// </summary>
    public bool WantsAudioCapture
    {
        get
        {
            if (_closed)
            {
                return false;
            }

            lock (_gate)
            {
                return _authenticated
                    && _audioPlayRequested
                    && _channels.Contains(MediaLinkProtocol.ChannelAudio);
            }
        }
    }

    public long AudioDroppedCount => _audioOutbound.DroppedCount;

    public bool IsSubscribedTo(string channel)
    {
        lock (_gate)
        {
            return _authenticated && _channels.Contains(channel);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var authCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        authCts.CancelAfter(_options.AuthTimeout);

        try
        {
            while (!_closed && _socket.State == WebSocketState.Open)
            {
                var token = _authenticated ? cancellationToken : authCts.Token;
                MediaLinkSocketMessage received;
                try
                {
                    received = await _socket.ReceiveAsync(token);
                }
                catch (OperationCanceledException) when (!_authenticated && !cancellationToken.IsCancellationRequested)
                {
                    await SendErrorAsync(null, MediaLinkProtocol.ErrorUnauthorized, "auth timeout", cancellationToken);
                    await CloseAsync(WebSocketCloseStatus.PolicyViolation, "auth timeout", cancellationToken);
                    return;
                }

                if (received.IsClosed)
                {
                    // 回一次单向关闭再退出。不回应时连接只能等超时结束，
                    // 而仲裁变化会频繁开关上游连接，每次都留一个等超时的连接。
                    //
                    // 先判状态：IsClosed 还有第二个来源——消息超限时接收侧会自己先关掉
                    // socket 再报 IsClosed，那条路径走到这里 socket 已是 Closed，而未认证的
                    // 对端可以反复触发它，不判状态就是每次都白抛一个异常。
                    if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        try
                        {
                            // 写要有界。这次 await 挡在 finally 的采集需求重算之前，对端不读
                            // 数据时无界的写会把音频设备的释放一起无限期拖住。
                            using var closeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            closeCts.CancelAfter(SendTimeout);
                            await _socket.CloseOutputAsync(
                                WebSocketCloseStatus.NormalClosure, null, closeCts.Token);
                        }
                        catch (Exception ex)
                        {
                            // 对端可能已经走了，也可能是上面那个超时到了。握手回不去不是错误，
                            // 收循环照常退出。这里必须自己接住 OperationCanceledException——
                            // 最外层的 catch 过滤器把它排除在外。
                            _logger?.LogDebug(ex, "回应关闭握手失败");
                        }
                    }

                    break;
                }

                if (received.Binary is { } binary)
                {
                    await HandleBinaryAsync(binary, cancellationToken);
                    continue;
                }

                await HandleMessageAsync(received.Text!, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "MediaLink session ended with error.");
        }
        finally
        {
            _closed = true;
            _outbound.Complete();
            _audioOutbound.Complete();

            // 会话消失即撤销它的采集需求。这里不判断本会话是否曾 play_start——
            // 宿主重算的是「当前还活着的会话」，已关闭的会话 WantsAudioCapture 恒假。
            await NotifyAudioCaptureDemandChangedAsync();
        }
    }

    /// <summary>
    /// 通知宿主重算采集需求。吞掉异常：宿主重算失败不该拖垮会话，
    /// 且下一次任意会话事件会再次触发重算，错过一拍可自愈。
    /// </summary>
    private async Task NotifyAudioCaptureDemandChangedAsync()
    {
        if (_options.OnAudioCaptureDemandChangedAsync is null)
        {
            return;
        }

        try
        {
            await _options.OnAudioCaptureDemandChangedAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "MediaLink 音频采集需求重算失败");
        }
    }

    /// <summary>
    /// 启动出站队列写者任务。在 RunAsync 之前或并发调用。
    /// </summary>
    public void StartWriter(CancellationToken cancellationToken)
    {
        if (_writerTask is not null) return;
        _writerTask = Task.Run(() => WriteLoopAsync(cancellationToken), CancellationToken.None);
        _audioWriterTask = Task.Run(() => AudioWriteLoopAsync(cancellationToken), CancellationToken.None);
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var frame = await _outbound.DequeueAsync(cancellationToken);
                if (frame is null)
                {
                    break; // 队列已 Complete 且排空
                }

                if (_closed || _socket.State != WebSocketState.Open)
                {
                    break;
                }

                try
                {
                    using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    sendCts.CancelAfter(SendTimeout);
                    await _socket.SendTextAsync(frame.Value.Json, sendCts.Token);
                }
                catch
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _closed = true;
        }
    }

    /// <summary>
    /// 音频独立写者。与 JSON 写者分开是因为两者的背压策略相反：
    /// JSON 队列满且无可丢帧时要以 rate_limited 关闭会话，音频队列满则静默丢最旧。
    /// 两者共用 socket 的 _sendLock，写入仍然串行。
    /// </summary>
    private async Task AudioWriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var frame = await _audioOutbound.DequeueAsync(cancellationToken);
                if (frame is null)
                {
                    break;
                }

                if (_closed || _socket.State != WebSocketState.Open)
                {
                    break;
                }

                try
                {
                    using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    sendCts.CancelAfter(SendTimeout);
                    await _socket.SendBinaryAsync(frame, sendCts.Token);
                }
                catch
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // 与 JSON 写者对称。两者共用同一个 socket 与 _sendLock，音频发送失败意味着 socket
            // 已坏，JSON 也发不出去；静默退出而会话自认存活，会让音频永久停摆、服务端持续
            // 向一条死连接入队，直到 JSON 侧自己也撞上失败才被发现。
            _closed = true;
            _audioOutbound.Complete();
        }
    }

    /// <summary>
    /// 入队音频帧。未订阅 audio 的会话直接丢弃——这保证了老客户端零影响。
    /// 无失败路径：队列满时丢最旧，不关闭会话。
    /// </summary>
    public Task EnqueueAudioAsync(byte[] frame, CancellationToken cancellationToken = default)
    {
        if (_closed || !IsSubscribedToAudio)
        {
            return Task.CompletedTask;
        }

        _audioOutbound.Enqueue(frame);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 所有出站帧统一走队列，由单写者串行发出。控制帧不得绕过队列直发 socket：
    /// 那样会与队列中待发的事件帧交错，破坏 seq 单调性，且没有发送超时保护。
    /// </summary>
    public async Task EnqueueAsync(MediaLinkMessage message, bool droppable = false, CancellationToken cancellationToken = default)
    {
        if (_closed) return;
        var json = MediaLinkMessageSerializer.Serialize(message);
        if (_outbound.TryEnqueue(new MediaLinkOutboundFrame(json, droppable)))
        {
            return;
        }

        await CloseRateLimitedAsync(cancellationToken);
    }

    private async Task CloseRateLimitedAsync(CancellationToken cancellationToken)
    {
        _closed = true;
        _outbound.Complete();
        _audioOutbound.Complete();
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _socket.CloseAsync((WebSocketCloseStatus)1011, "rate_limited", cancellationToken);
        }
        catch { }
    }

    /// <summary>
    /// 入站二进制帧。解码失败不断连：未知 magic 可能是未来版本的其他二进制帧类型，
    /// 按协议的"必须容忍未知"原则丢弃即可。
    ///
    /// 入站二进制帧本身即是 spec 的 audio.inject——它与推送帧线格式相同、方向相反，
    /// 一条连接上入站的音频帧无需再用类型字段区分，故不另设 JSON 消息类型。
    /// </summary>
    private async Task HandleBinaryAsync(byte[] data, CancellationToken cancellationToken)
    {
        if (!IsAuthenticated)
        {
            return;
        }

        // 解码放在同步局部函数里有两重原因：ReadOnlySpan 是 ref struct，C# 12 不允许它出现在
        // async 方法体内；而 pcm 又是指向 data 的零拷贝切片，WebSocketMediaLinkSocket 复用固定
        // 接收缓冲，必须在跨 await 之前拷出，否则回调读到的是被下一帧覆写的数据。
        static (MediaLinkAudioFrameHeader Header, byte[] Pcm)? TryDecodeCopy(
            byte[] data, out MediaLinkAudioFrameDecodeError error)
        {
            if (!MediaLinkAudioFrame.TryDecode(data, out var header, out var pcm, out error))
            {
                return null;
            }

            return (header, pcm.ToArray());
        }

        var decoded = TryDecodeCopy(data, out var decodeError);
        if (decoded is null)
        {
            _logger?.LogDebug("丢弃无法解码的二进制帧：{Error}", decodeError);
            return;
        }

        if (_options.OnAudioFrameAsync is not null)
        {
            await _options.OnAudioFrameAsync(decoded.Value.Header, decoded.Value.Pcm);
        }
    }

    public async Task HandleMessageAsync(string text, CancellationToken cancellationToken)
    {
        MediaLinkMessage? message;
        try
        {
            message = MediaLinkMessageSerializer.Deserialize(text);
        }
        catch (JsonException)
        {
            await SendErrorAsync(null, MediaLinkProtocol.ErrorProtocolError, "invalid json", cancellationToken);
            return;
        }

        if (message is null || string.IsNullOrWhiteSpace(message.Type))
        {
            await SendErrorAsync(null, MediaLinkProtocol.ErrorBadRequest, "missing type", cancellationToken);
            return;
        }

        // 未认证时仅允许 auth；其它类型（含 ping / Phase2）一律 unauthorized 并关闭。
        if (!_authenticated && message.Type != MediaLinkProtocol.TypeAuth)
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorUnauthorized, "authenticate first", cancellationToken);
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", cancellationToken);
            return;
        }

        switch (message.Type)
        {
            case MediaLinkProtocol.TypeAuth:
                await HandleAuthAsync(message, cancellationToken);
                break;
            case MediaLinkProtocol.TypeSubscribe:
                await HandleSubscribeAsync(message, cancellationToken);
                break;
            case MediaLinkProtocol.TypePing:
                await SendAsync(MediaLinkMessageSerializer.Create(
                    MediaLinkProtocol.TypePong,
                    id: message.Id,
                    ts: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), cancellationToken);
                break;
            case MediaLinkProtocol.TypeHello:
                // ignore client hello
                break;
            case MediaLinkProtocol.TypeMediaInject:
                await HandleMediaInjectAsync(message, cancellationToken);
                break;
            case MediaLinkProtocol.TypeLyricsInject:
                await HandleLyricsInjectAsync(message, cancellationToken);
                break;
            case MediaLinkProtocol.TypeMediaClearInject:
                await HandleMediaClearInjectAsync(message, cancellationToken);
                break;
            case MediaLinkProtocol.TypePlaybackCommand:
                await HandlePlaybackCommandAsync(message, cancellationToken);
                break;
            case MediaLinkProtocol.TypeUnsubscribe:
                await HandleUnsubscribeAsync(message, cancellationToken);
                break;
            case MediaLinkProtocol.TypeThumbnailGet:
                await HandleThumbnailGetAsync(message, cancellationToken);
                break;
            case MediaLinkProtocol.TypeAudioPlayStart:
                await HandleAudioPlayAsync(message, start: true, cancellationToken);
                break;
            case MediaLinkProtocol.TypeAudioPlayStop:
                await HandleAudioPlayAsync(message, start: false, cancellationToken);
                break;
            default:
                await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorBadRequest, $"unknown type: {message.Type}", cancellationToken);
                break;
        }
    }

    public Task SendEventAsync(string eventName, object? payload, CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeEvent,
                payload,
                name: eventName,
                ts: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            droppable: eventName == MediaLinkProtocol.EventMediaUpdated,
            cancellationToken);

    /// <summary>
    /// 控制帧与响应帧直发 socket，不入队。这是刻意的：调用方（接收循环）依赖
    /// "本方法返回即已送出"来实现请求-响应语义，入队会让回复在处理器返回后才发出。
    ///
    /// 顺序不受影响：seq 只存在于事件帧上，而事件帧全部经出站队列由单写者发出，
    /// 它们的相对顺序由队列保证；控制帧插在其间不破坏 seq 单调性。
    ///
    /// 超时是必需的：对端 TCP 窗口塞满时，无超时的写会连带 socket 锁一起挂住
    /// 接收循环与写者任务，整个会话僵死。
    /// </summary>
    public async Task SendAsync(MediaLinkMessage message, CancellationToken cancellationToken = default)
    {
        if (_closed || _socket.State != WebSocketState.Open)
        {
            return;
        }

        var json = MediaLinkMessageSerializer.Serialize(message);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_closed || _socket.State != WebSocketState.Open)
            {
                return;
            }

            using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sendCts.CancelAfter(SendTimeout);
            try
            {
                await _socket.SendTextAsync(json, sendCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 写超时：对端已不可达，标记会话故障让接收循环退出。
                _closed = true;
                _outbound.Complete();
                _audioOutbound.Complete();
                _logger?.LogDebug("MediaLink 出站写超时，终止会话");
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task HandleAuthAsync(MediaLinkMessage message, CancellationToken cancellationToken)
    {
        var payload = MediaLinkMessageSerializer.DeserializePayload<MediaLinkAuthPayload>(message.Payload);
        var ok = MediaLinkAuth.ValidateToken(_options.ExpectedToken, payload?.Token);
        if (!ok)
        {
            _options.OnAuthFailed?.Invoke();
            await SendAsync(MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeAuthFail,
                new MediaLinkErrorPayload
                {
                    Code = MediaLinkProtocol.ErrorUnauthorized,
                    Message = "invalid token"
                },
                id: message.Id), cancellationToken);
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "auth failed", cancellationToken);
            return;
        }

        lock (_gate)
        {
            _authenticated = true;
        }

        _options.OnAuthSucceeded?.Invoke();
        await SendAsync(MediaLinkMessageSerializer.Create(MediaLinkProtocol.TypeAuthOk, id: message.Id), cancellationToken);
    }

    private async Task HandleSubscribeAsync(MediaLinkMessage message, CancellationToken cancellationToken)
    {
        if (!IsAuthenticated)
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorUnauthorized, "authenticate first", cancellationToken);
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", cancellationToken);
            return;
        }

        var payload = MediaLinkMessageSerializer.DeserializePayload<MediaLinkSubscribePayload>(message.Payload);
        var requested = (payload?.Channels ?? [])
            .Where(channel => !string.IsNullOrWhiteSpace(channel))
            .Select(channel => channel.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (requested.Length == 0 || requested.Any(channel => !MediaLinkProtocol.KnownChannels.Contains(channel)))
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorBadRequest, "invalid channels", cancellationToken);
            return;
        }

        lock (_gate)
        {
            _channels.Clear();
            foreach (var channel in requested)
            {
                _channels.Add(channel);
            }
        }

        // 订阅是整体替换语义，重发不含 audio 的频道列表会把 audio 顶掉，需求随之消失。
        await NotifyAudioCaptureDemandChangedAsync();

        await SendAsync(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeSubscribeOk,
            new MediaLinkSubscribePayload { Channels = requested.ToList() },
            id: message.Id), cancellationToken);

        if (_options.OnSubscribedAsync is not null)
        {
            await _options.OnSubscribedAsync(this);
        }
    }

    private async Task HandleUnsubscribeAsync(MediaLinkMessage message, CancellationToken cancellationToken)
    {
        if (!IsAuthenticated)
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorUnauthorized, "authenticate first", cancellationToken);
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", cancellationToken);
            return;
        }

        var payload = MediaLinkMessageSerializer.DeserializePayload<MediaLinkUnsubscribePayload>(message.Payload);
        var requested = (payload?.Channels ?? [])
            .Where(channel => !string.IsNullOrWhiteSpace(channel))
            .Select(channel => channel.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (requested.Length == 0 || requested.Any(channel => !MediaLinkProtocol.KnownChannels.Contains(channel)))
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorBadRequest, "invalid channels", cancellationToken);
            return;
        }

        string[] removed;
        lock (_gate)
        {
            removed = requested.Where(channel => _channels.Remove(channel)).ToArray();
        }

        if (removed.Length == 0)
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorBadRequest, "not subscribed", cancellationToken);
            return;
        }

        await NotifyAudioCaptureDemandChangedAsync();

        await SendAsync(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeUnsubscribeOk,
            new MediaLinkUnsubscribePayload { Channels = removed.ToList() },
            id: message.Id), cancellationToken);
    }

    /// <summary>
    /// 音频采集的开关。只表达意愿，不改订阅状态——订阅仍由 subscribe/unsubscribe 管理。
    ///
    /// 无论是否订阅了 audio 都回 ok：协议已冻结「两者只表达采集意愿」这一语义
    /// （docs/medialink-protocol.md），客户端先 play_start 再 subscribe 是合法顺序。
    /// 防「未订阅却白占音频设备」的门禁落在 <see cref="WantsAudioCapture"/>，不在此处。
    /// </summary>
    private async Task HandleAudioPlayAsync(MediaLinkMessage message, bool start, CancellationToken cancellationToken)
    {
        if (!IsAuthenticated)
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorUnauthorized, "authenticate first", cancellationToken);
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", cancellationToken);
            return;
        }

        lock (_gate)
        {
            _audioPlayRequested = start;
        }

        await NotifyAudioCaptureDemandChangedAsync();

        await SendAsync(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeOk,
            new MediaLinkOkPayload { For = message.Type },
            id: message.Id), cancellationToken);
    }
    /// <summary>
    /// 经 WebSocket 取当前封面。相比 HTTP 端点，已认证连接无需把 Token 放进 URL，
    /// 也不必为一张图另开一条 TCP 连接。
    /// </summary>
    private async Task HandleThumbnailGetAsync(MediaLinkMessage message, CancellationToken cancellationToken)
    {
        var coordinator = _options.Coordinator;
        if (coordinator is null)
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorInternal, "not configured", cancellationToken);
            return;
        }

        var media = coordinator.GetMediaForPush();
        if (media is null)
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorNoSession, "no session", cancellationToken);
            return;
        }

        var currentTrackToken = MediaLinkDtoMapper.ComputeTrackToken(
            media.SourceApp, media.Title, media.Artist, media.AlbumTitle);

        var payload = MediaLinkMessageSerializer.DeserializePayload<MediaLinkThumbnailGetPayload>(message.Payload);
        var requested = payload?.TrackToken;
        var stale = !string.IsNullOrEmpty(requested) &&
                    !string.Equals(requested, currentTrackToken, StringComparison.Ordinal);

        byte[]? png = null;
        if (!stale)
        {
            try
            {
                png = await MediaLinkThumbnail.EncodePngAsync(media, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "MediaLink WS 缩略图编码失败");
                await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorInternal, "thumbnail encode failed", cancellationToken);
                return;
            }
        }

        await SendAsync(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeThumbnail,
            new MediaLinkThumbnailPayload
            {
                TrackToken = currentTrackToken,
                MimeType = png is null ? null : MediaLinkThumbnail.MimeType,
                DataBase64 = png is null ? null : Convert.ToBase64String(png)
            },
            id: message.Id), cancellationToken);
    }

    private async Task HandleMediaInjectAsync(MediaLinkMessage message, CancellationToken cancellationToken)
    {
        var store = _options.InjectionStore;
        if (store is null)
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorInternal, "not configured", cancellationToken);
            return;
        }

        var payload = MediaLinkMessageSerializer.DeserializePayload<MediaLinkMediaInjectPayload>(message.Payload);
        if (payload is null)
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorBadRequest, "missing payload", cancellationToken);
            return;
        }

        if (!store.TrySetMedia(payload, out var error))
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorBadRequest, error ?? "invalid media inject", cancellationToken);
            return;
        }

        _options.Coordinator?.Recompute(MediaInfoChangeKind.MediaProperties);
        await SendOkAsync(message, cancellationToken);
        if (_options.OnEffectiveMediaMutatedAsync is not null)
        {
            await _options.OnEffectiveMediaMutatedAsync(MediaInfoChangeKind.MediaProperties);
        }
    }

    private async Task HandleLyricsInjectAsync(MediaLinkMessage message, CancellationToken cancellationToken)
    {
        var store = _options.InjectionStore;
        if (store is null)
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorInternal, "not configured", cancellationToken);
            return;
        }

        var payload = MediaLinkMessageSerializer.DeserializePayload<MediaLinkLyricsDto>(message.Payload);
        if (payload is null)
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorBadRequest, "missing payload", cancellationToken);
            return;
        }

        if (!store.TrySetLyrics(payload, out var error))
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorBadRequest, error ?? "invalid lyrics inject", cancellationToken);
            return;
        }

        _options.Coordinator?.Recompute(MediaInfoChangeKind.CurrentSession);
        await SendOkAsync(message, cancellationToken);
        if (_options.OnEffectiveLyricsMutatedAsync is not null)
        {
            await _options.OnEffectiveLyricsMutatedAsync();
        }
    }

    private async Task HandleMediaClearInjectAsync(MediaLinkMessage message, CancellationToken cancellationToken)
    {
        var store = _options.InjectionStore;
        if (store is null)
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorInternal, "not configured", cancellationToken);
            return;
        }

        // payload 可省略；省略时 clear 两边。
        var payload = MediaLinkMessageSerializer.DeserializePayload<MediaLinkClearInjectPayload>(message.Payload)
                      ?? new MediaLinkClearInjectPayload();

        if (!store.TryClear(payload.Channels, out var error))
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorBadRequest, error ?? "invalid clear_inject", cancellationToken);
            return;
        }

        _options.Coordinator?.Recompute(MediaInfoChangeKind.CurrentSession);
        await SendOkAsync(message, cancellationToken);

        var channels = payload.Channels;
        var clearedMedia = channels is null || channels.Count == 0 ||
                           channels.Any(c => string.Equals(c, MediaLinkProtocol.ChannelMedia, StringComparison.Ordinal));
        var clearedLyrics = channels is null || channels.Count == 0 ||
                            channels.Any(c => string.Equals(c, MediaLinkProtocol.ChannelLyrics, StringComparison.Ordinal));

        if (clearedMedia && _options.OnEffectiveMediaMutatedAsync is not null)
        {
            await _options.OnEffectiveMediaMutatedAsync(MediaInfoChangeKind.CurrentSession);
        }

        if (clearedLyrics && _options.OnEffectiveLyricsMutatedAsync is not null)
        {
            await _options.OnEffectiveLyricsMutatedAsync();
        }
    }

    private async Task HandlePlaybackCommandAsync(MediaLinkMessage message, CancellationToken cancellationToken)
    {
        var payload = MediaLinkMessageSerializer.DeserializePayload<MediaLinkPlaybackCommandPayload>(message.Payload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.Action))
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorBadRequest, "missing action", cancellationToken);
            return;
        }

        if (!TryParsePlaybackAction(payload.Action, out var command))
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorBadRequest, $"unknown action: {payload.Action}", cancellationToken);
            return;
        }

        var coordinator = _options.Coordinator;
        var store = _options.InjectionStore;

        // 有 coordinator 时以 compose 结果为准；否则仅在有 inject 时走虚拟路径。
        if (coordinator is not null)
        {
            var composed = coordinator.ComposeMedia();
            if (composed is null)
            {
                await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorNoSession, "no session", cancellationToken);
                return;
            }

            if (coordinator.IsExternalMediaEffective)
            {
                await HandleExternalPlaybackAsync(message, command, store, cancellationToken);
                return;
            }

            await HandlePlatformPlaybackAsync(message, command, cancellationToken);
            return;
        }

        if (store is not null && store.HasExternalMedia)
        {
            await HandleExternalPlaybackAsync(message, command, store, cancellationToken);
            return;
        }

        // 无 coordinator 且无 external：若 accessor 有 controller 则尝试平台；否则 no_session。
        if (_options.PlaybackControllerAccessor is not null)
        {
            await HandlePlatformPlaybackAsync(message, command, cancellationToken);
            return;
        }

        await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorNoSession, "no session", cancellationToken);
    }

    private async Task HandleExternalPlaybackAsync(
        MediaLinkMessage message,
        MediaPlaybackCommand command,
        MediaLinkInjectionStore? store,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorInternal, "not configured", cancellationToken);
            return;
        }

        if (command is MediaPlaybackCommand.Next or MediaPlaybackCommand.Previous)
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorNotSupported, "external next/previous not supported", cancellationToken);
            return;
        }

        string? error;
        var ok = command switch
        {
            MediaPlaybackCommand.Play => store.TryVirtualPlay(out error),
            MediaPlaybackCommand.Pause => store.TryVirtualPause(out error),
            _ => Fail(out error)
        };

        if (!ok)
        {
            // 无 media 时用 no_session；其它失败 bad_request。
            var code = string.Equals(error, "no external media", StringComparison.Ordinal)
                ? MediaLinkProtocol.ErrorNoSession
                : MediaLinkProtocol.ErrorBadRequest;
            await SendErrorAsync(message.Id, code, error ?? "playback failed", cancellationToken);
            return;
        }

        _options.Coordinator?.Recompute(MediaInfoChangeKind.Playback);
        await SendOkAsync(message, cancellationToken);
        if (_options.OnEffectiveMediaMutatedAsync is not null)
        {
            await _options.OnEffectiveMediaMutatedAsync(MediaInfoChangeKind.Playback);
        }

        static bool Fail(out string? err)
        {
            err = "unsupported command";
            return false;
        }
    }

    private async Task HandlePlatformPlaybackAsync(
        MediaLinkMessage message,
        MediaPlaybackCommand command,
        CancellationToken cancellationToken)
    {
        var controller = _options.PlaybackControllerAccessor?.Invoke();
        if (controller is null)
        {
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorNotSupported, "no playback controller", cancellationToken);
            return;
        }

        MediaPlaybackCommandResult result;
        try
        {
            result = await controller.ExecuteAsync(command, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "MediaLink platform playback command failed.");
            await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorInternal, ex.Message, cancellationToken);
            return;
        }

        switch (result.Status)
        {
            case MediaPlaybackCommandStatus.Succeeded:
                await SendOkAsync(message, cancellationToken);
                break;
            case MediaPlaybackCommandStatus.NotSupported:
                await SendErrorAsync(
                    message.Id,
                    MediaLinkProtocol.ErrorNotSupported,
                    result.Message ?? "not supported",
                    cancellationToken);
                break;
            case MediaPlaybackCommandStatus.NoSession:
                await SendErrorAsync(
                    message.Id,
                    MediaLinkProtocol.ErrorNoSession,
                    result.Message ?? "no session",
                    cancellationToken);
                break;
            case MediaPlaybackCommandStatus.Failed:
            default:
                await SendErrorAsync(
                    message.Id,
                    MediaLinkProtocol.ErrorInternal,
                    result.Message ?? "playback failed",
                    cancellationToken);
                break;
        }
    }

    private static bool TryParsePlaybackAction(string action, out MediaPlaybackCommand command)
    {
        if (action.Equals("play", StringComparison.OrdinalIgnoreCase))
        {
            command = MediaPlaybackCommand.Play;
            return true;
        }

        if (action.Equals("pause", StringComparison.OrdinalIgnoreCase))
        {
            command = MediaPlaybackCommand.Pause;
            return true;
        }

        if (action.Equals("next", StringComparison.OrdinalIgnoreCase))
        {
            command = MediaPlaybackCommand.Next;
            return true;
        }

        if (action.Equals("previous", StringComparison.OrdinalIgnoreCase))
        {
            command = MediaPlaybackCommand.Previous;
            return true;
        }

        command = default;
        return false;
    }

    private Task SendOkAsync(MediaLinkMessage message, CancellationToken cancellationToken) =>
        SendAsync(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeOk,
            new MediaLinkOkPayload { For = message.Type },
            id: message.Id), cancellationToken);

    private Task SendErrorAsync(string? id, string code, string message, CancellationToken cancellationToken) =>
        SendAsync(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeError,
            new MediaLinkErrorPayload { Code = code, Message = message },
            id: id), cancellationToken);

    private async Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
    {
        _closed = true;
        _outbound.Complete();
        _audioOutbound.Complete();
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseAsync(status, description, cancellationToken);
            }
        }
        catch
        {
            // ignore close races
        }
    }

    /// <summary>
    /// 服务停止时主动告知对端，使客户端能区分「服务端正常下线」与「网络故障」，
    /// 从而决定是否重连。仅此一处对外暴露关闭能力。
    ///
    /// 用 CloseOutputAsync 而非 CloseAsync：只需把 1001 送到对端，不必等它回关闭帧。
    /// 宿主（ClassIsland）不 await 停服流程，等待对端握手会让帧根本发不出去。
    ///
    /// 幂等：AppStopping 与 IHostedService.StopAsync 可能相继触发，第二次调用直接返回。
    /// </summary>
    public async Task CloseGoingAwayAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_goingAwaySent)
            {
                return;
            }

            _goingAwaySent = true;
        }

        _closed = true;
        _outbound.Complete();
        _audioOutbound.Complete();
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseOutputAsync((WebSocketCloseStatus)1001, "server stopping", cancellationToken);
            }
        }
        catch
        {
            // 对端已死或写超时：停服不能因此挂住
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        // 只 Complete，不 Dispose：发送方可能仍在 await _sendLock，此刻释放它会让对方
        // 拿到 ObjectDisposedException。Complete 足以让写者从队列上退出。
        _outbound.Complete();
        _audioOutbound.Complete();

        // 等写者真的退出。不等的话，本方法返回不等于该会话再无后台工作——
        // 在测试进程里那意味着上一条用例的写者可以活进下一条，表现为「随负载出现」的挂起。
        var writers = new List<Task>(2);
        if (_writerTask is not null)
        {
            writers.Add(_writerTask);
        }

        if (_audioWriterTask is not null)
        {
            writers.Add(_audioWriterTask);
        }

        await TaskDraining.DrainAsync(writers, WriterDrainTimeout).ConfigureAwait(false);
    }

    /// <summary>本会话的出站写者任务。判据用：Dispose 返回时它必须已完成。</summary>
    internal Task? WriterTaskForTest => _writerTask;

    /// <summary>本会话的音频写者任务。判据用，同 <see cref="WriterTaskForTest"/>。</summary>
    internal Task? AudioWriterTaskForTest => _audioWriterTask;
}

public sealed class MediaLinkSessionHub
{
    private readonly ConcurrentDictionary<MediaLinkSession, byte> _sessions = new();

    /// <summary>会话数变化时触发，供设置页等观察方刷新，避免轮询。</summary>
    public event Action? SessionCountChanged;

    public void Add(MediaLinkSession session)
    {
        _sessions[session] = 0;
        SessionCountChanged?.Invoke();
    }

    public void Remove(MediaLinkSession session)
    {
        if (_sessions.TryRemove(session, out _))
        {
            SessionCountChanged?.Invoke();
        }
    }

    public IReadOnlyCollection<MediaLinkSession> Sessions => _sessions.Keys.ToArray();

    /// <summary>
    /// 是否有会话要求推送音频（订阅了 audio 或发过 play_start）。
    ///
    /// 名字里不带 Capture：第 3 期这个信号只可能导致本机采集，本期起它在仲裁后
    /// 也可能导致转发上游音频，叫 CaptureDemand 会与实际后果不符。
    ///
    /// 重算而非增减引用计数：会话以任何方式消失（含客户端进程被杀这类不发关闭握手的路径）
    /// 都不会留下悬空的需求，因为这里读的始终是「当前还在集合里且仍想要」的会话。
    /// </summary>
    public bool HasDownstreamAudioDemand => _sessions.Keys.Any(session => session.WantsAudioCapture);

    /// <summary>
    /// 广播音频二进制帧。未订阅 audio 的会话由 <see cref="MediaLinkSession.EnqueueAudioAsync"/>
    /// 自行丢弃，故此处只需遍历；单会话失败不影响其余会话，与 JSON 广播同构。
    /// </summary>
    public async Task BroadcastAudioFrameAsync(byte[] frame, CancellationToken cancellationToken = default)
    {
        foreach (var session in _sessions.Keys)
        {
            if (!session.IsSubscribedToAudio)
            {
                continue;
            }

            try
            {
                await session.EnqueueAudioAsync(frame, cancellationToken);
            }
            catch
            {
                // drop broken sessions on next cleanup
            }
        }
    }

    public async Task BroadcastEventAsync(string channel, string eventName, object? payload, CancellationToken cancellationToken = default)
    {
        foreach (var session in _sessions.Keys)
        {
            if (!session.IsSubscribedTo(channel))
            {
                continue;
            }

            try
            {
                await session.SendEventAsync(eventName, payload, cancellationToken);
            }
            catch
            {
                // drop broken sessions on next cleanup
            }
        }
    }

    /// <summary>广播 media.updated，携带 seq，media.updated 可丢弃。</summary>
    public async Task BroadcastMediaUpdatedAsync(MediaLinkMediaDto? dto, long seq, CancellationToken cancellationToken = default)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var session in _sessions.Keys)
        {
            if (!session.IsSubscribedTo(MediaLinkProtocol.ChannelMedia))
            {
                continue;
            }

            try
            {
                await session.EnqueueAsync(
                    MediaLinkMessageSerializer.Create(
                        MediaLinkProtocol.TypeEvent, dto,
                        name: MediaLinkProtocol.EventMediaUpdated,
                        ts: nowMs, seq: seq),
                    droppable: true,
                    cancellationToken);
            }
            catch
            {
                // drop broken sessions on next cleanup
            }
        }
    }

    /// <summary>广播 lyrics.updated，携带 seq，歌词不可丢弃。</summary>
    public async Task BroadcastLyricsUpdatedAsync(MediaLinkLyricsDto? dto, long seq, CancellationToken cancellationToken = default)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var session in _sessions.Keys)
        {
            if (!session.IsSubscribedTo(MediaLinkProtocol.ChannelLyrics))
            {
                continue;
            }

            try
            {
                await session.EnqueueAsync(
                    MediaLinkMessageSerializer.Create(
                        MediaLinkProtocol.TypeEvent, dto,
                        name: MediaLinkProtocol.EventLyricsUpdated,
                        ts: nowMs, seq: seq),
                    droppable: false,
                    cancellationToken);
            }
            catch
            {
                // drop broken sessions on next cleanup
            }
        }
    }

    /// <summary>
    /// 向所有在线会话发 1001 GoingAway。必须在取消会话令牌之前调用：
    /// 一旦取消，RunAsync 立即结束并 Dispose socket，关闭帧就发不出去了。
    /// 单会话失败或超时不影响其余会话，也不阻塞停服。
    ///
    /// 并发而非串行：会话之间没有依赖，而串行会让超时累加——32 个卡死的会话
    /// 按 1s/个 就要 32s，远超宿主留给停服的窗口，后面的会话根本轮不到发帧。
    /// 并发后总耗时收敛为单会话超时。
    /// </summary>
    public async Task CloseAllGoingAwayAsync(TimeSpan perSessionTimeout, CancellationToken cancellationToken = default)
    {
        var closes = _sessions.Keys.Select(async session =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(perSessionTimeout);
                await session.CloseGoingAwayAsync(cts.Token);
            }
            catch
            {
                // 对端已死或不回关闭握手：停服不能因此挂住
            }
        });

        await Task.WhenAll(closes);
    }

    /// <summary>
    /// 并发而非串行：会话之间没有依赖，而 DisposeAsync 现在会等写者退出，
    /// 串行会把每会话的界累加成 N 倍——32 个会话按 6s/个 就是 192s，远超宿主留给停服的窗口。
    /// 并发后总耗时收敛为单会话的界。与上面 CloseAllGoingAwayAsync 同形。
    /// </summary>
    public async Task DisposeAllAsync()
    {
        var sessions = _sessions.Keys.ToArray();

        await Task.WhenAll(sessions.Select(async session =>
        {
            try
            {
                await session.DisposeAsync();
            }
            catch
            {
                // 单会话释放失败不影响其余会话，也不阻塞停服
            }
        }));

        foreach (var session in sessions)
        {
            _sessions.TryRemove(session, out _);
        }
    }
}
