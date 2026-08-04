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

    /// <summary>取下一条文本消息；连接关闭时返回 null。</summary>
    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);
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

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(_buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            if (message.Length + result.Count > MaxMessageBytes)
            {
                return null; // 服务端不该发这么大的帧，视同协议故障
            }

            message.Write(_buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
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
            new MediaLinkSubscribePayload { Channels = _options.Channels.ToList() },
            id: "sub"), handshakeCts.Token);

        SetConnected(true);
        LastError = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var text = await socket.ReceiveTextAsync(cancellationToken);
            if (text is null)
            {
                return; // 对端关闭，交给重连逻辑
            }

            Dispatch(text);
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
        HandleEpoch(payload?.SessionEpoch ?? 0);
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
            HandleEpoch(hello?.SessionEpoch ?? 0);
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
