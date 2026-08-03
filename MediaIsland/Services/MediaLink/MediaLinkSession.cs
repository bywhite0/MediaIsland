using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MediaIsland.Services.Media;
using MediaIsland.Services.Media.Platform;
using MediaIsland.Services.MediaLink.Protocol;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.MediaLink;

public interface IMediaLinkSocket
{
    WebSocketState State { get; }

    Task SendTextAsync(string text, CancellationToken cancellationToken);

    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);

    Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken);
}

public sealed class WebSocketMediaLinkSocket(WebSocket webSocket) : IMediaLinkSocket, IDisposable
{
    /// <summary>单条文本消息组装上限（2 MiB）。</summary>
    public const int MaxMessageBytes = 2 * 1024 * 1024;

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

    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var result = await webSocket.ReceiveAsync(_buffer, cancellationToken);
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

                return null;
            }

            message.Write(_buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
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
}

public sealed class MediaLinkSession : IAsyncDisposable
{
    private readonly IMediaLinkSocket _socket;
    private readonly MediaLinkSessionOptions _options;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly HashSet<string> _channels = new(StringComparer.Ordinal);
    private bool _authenticated;
    private bool _closed;
    private readonly Channel<string> _outbound = Channel.CreateBounded<string>(64);
    private Task? _writerTask;

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
                string? text;
                try
                {
                    text = await _socket.ReceiveTextAsync(token);
                }
                catch (OperationCanceledException) when (!_authenticated && !cancellationToken.IsCancellationRequested)
                {
                    await SendErrorAsync(null, MediaLinkProtocol.ErrorUnauthorized, "auth timeout", cancellationToken);
                    await CloseAsync(WebSocketCloseStatus.PolicyViolation, "auth timeout", cancellationToken);
                    return;
                }

                if (text is null)
                {
                    break;
                }

                await HandleMessageAsync(text, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "MediaLink session ended with error.");
        }
        finally
        {
            _closed = true;
            _outbound.Writer.TryComplete();
        }
    }

    /// <summary>
    /// 启动出站队列写者任务。在 RunAsync 之前或并发调用。
    /// </summary>
    public void StartWriter(CancellationToken cancellationToken)
    {
        if (_writerTask is not null) return;
        _writerTask = Task.Run(() => WriteLoopAsync(cancellationToken), CancellationToken.None);
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var json in _outbound.Reader.ReadAllAsync(cancellationToken))
            {
                if (_closed || _socket.State != WebSocketState.Open) break;
                try
                {
                    using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    sendCts.CancelAfter(TimeSpan.FromSeconds(5));
                    await _socket.SendTextAsync(json, sendCts.Token);
                }
                catch
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
        finally
        {
            _closed = true;
        }
    }

    public async Task EnqueueAsync(MediaLinkMessage message, bool droppable = false, CancellationToken cancellationToken = default)
    {
        if (_closed) return;
        var json = MediaLinkMessageSerializer.Serialize(message);
        if (_outbound.Writer.TryWrite(json)) return;
        if (droppable)
        {
            // DropOldest 语义：尝试读一条丢弃后再写
            _outbound.Reader.TryRead(out _);
            _outbound.Writer.TryWrite(json);
            return;
        }
        await CloseRateLimitedAsync(cancellationToken);
    }

    private async Task CloseRateLimitedAsync(CancellationToken cancellationToken)
    {
        _closed = true;
        _outbound.Writer.TryComplete();
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _socket.CloseAsync((WebSocketCloseStatus)1011, "rate_limited", cancellationToken);
        }
        catch { }
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

            await _socket.SendTextAsync(json, cancellationToken);
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

        await SendAsync(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeUnsubscribeOk,
            new MediaLinkUnsubscribePayload { Channels = removed.ToList() },
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

    public ValueTask DisposeAsync()
    {
        _closed = true;
        // 不 Dispose _sendLock：停止过程中仍可能有 in-flight Send。
        return ValueTask.CompletedTask;
    }
}

public sealed class MediaLinkSessionHub
{
    private readonly ConcurrentDictionary<MediaLinkSession, byte> _sessions = new();

    public void Add(MediaLinkSession session) => _sessions[session] = 0;

    public void Remove(MediaLinkSession session) => _sessions.TryRemove(session, out _);

    public IReadOnlyCollection<MediaLinkSession> Sessions => _sessions.Keys.ToArray();

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

    public async Task DisposeAllAsync()
    {
        foreach (var session in _sessions.Keys)
        {
            await session.DisposeAsync();
            _sessions.TryRemove(session, out _);
        }
    }
}
