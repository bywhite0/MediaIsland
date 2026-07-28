using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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

public sealed class WebSocketMediaLinkSocket(WebSocket webSocket) : IMediaLinkSocket
{
    private readonly byte[] _buffer = new byte[64 * 1024];

    public WebSocketState State => webSocket.State;

    public async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await webSocket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
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

            message.Write(_buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }

    public Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken) =>
        webSocket.CloseAsync(status, description, cancellationToken);
}

public sealed class MediaLinkSessionOptions
{
    public required string ExpectedToken { get; init; }

    public TimeSpan AuthTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public Func<MediaLinkSession, Task>? OnSubscribedAsync { get; init; }
}

public sealed class MediaLinkSession : IAsyncDisposable
{
    private readonly IMediaLinkSocket _socket;
    private readonly MediaLinkSessionOptions _options;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly HashSet<string> _channels = new(StringComparer.Ordinal);
    private bool _authenticated;
    private bool _closed;

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
            case "media.inject":
            case "lyrics.inject":
            case "media.clear_inject":
            case "playback.command":
                await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorNotImplemented, message.Type, cancellationToken);
                break;
            default:
                if (!_authenticated)
                {
                    await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorUnauthorized, "authenticate first", cancellationToken);
                    await CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", cancellationToken);
                    return;
                }

                await SendErrorAsync(message.Id, MediaLinkProtocol.ErrorBadRequest, $"unknown type: {message.Type}", cancellationToken);
                break;
        }
    }

    public Task SendEventAsync(string eventName, object? payload, CancellationToken cancellationToken = default) =>
        SendAsync(MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeEvent,
            payload,
            name: eventName,
            ts: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), cancellationToken);

    public async Task SendAsync(MediaLinkMessage message, CancellationToken cancellationToken = default)
    {
        if (_closed || _socket.State != WebSocketState.Open)
        {
            return;
        }

        var json = MediaLinkMessageSerializer.Serialize(message);
        await _socket.SendTextAsync(json, cancellationToken);
    }

    private async Task HandleAuthAsync(MediaLinkMessage message, CancellationToken cancellationToken)
    {
        var payload = MediaLinkMessageSerializer.DeserializePayload<MediaLinkAuthPayload>(message.Payload);
        var ok = MediaLinkAuth.ValidateToken(_options.ExpectedToken, payload?.Token);
        if (!ok)
        {
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

    public async Task DisposeAllAsync()
    {
        foreach (var session in _sessions.Keys)
        {
            await session.DisposeAsync();
            _sessions.TryRemove(session, out _);
        }
    }
}
