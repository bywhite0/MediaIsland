using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MediaIsland.Services.Realtime.Protocol;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Realtime;

public interface IRealtimeSocket
{
    WebSocketState State { get; }

    Task SendTextAsync(string text, CancellationToken cancellationToken);

    Task<string?> ReceiveTextAsync(CancellationToken cancellationToken);

    Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken);
}

public sealed class WebSocketRealtimeSocket(WebSocket webSocket) : IRealtimeSocket
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

public sealed class RealtimeSessionOptions
{
    public required string ExpectedToken { get; init; }

    public TimeSpan AuthTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public Func<RealtimeSession, Task>? OnSubscribedAsync { get; init; }
}

public sealed class RealtimeSession : IAsyncDisposable
{
    private readonly IRealtimeSocket _socket;
    private readonly RealtimeSessionOptions _options;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly HashSet<string> _channels = new(StringComparer.Ordinal);
    private bool _authenticated;
    private bool _closed;

    public RealtimeSession(IRealtimeSocket socket, RealtimeSessionOptions options, ILogger? logger = null)
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
                    await SendErrorAsync(null, RealtimeProtocol.ErrorUnauthorized, "auth timeout", cancellationToken);
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
            _logger?.LogDebug(ex, "Realtime session ended with error.");
        }
        finally
        {
            _closed = true;
        }
    }

    public async Task HandleMessageAsync(string text, CancellationToken cancellationToken)
    {
        RealtimeMessage? message;
        try
        {
            message = RealtimeMessageSerializer.Deserialize(text);
        }
        catch (JsonException)
        {
            await SendErrorAsync(null, RealtimeProtocol.ErrorProtocolError, "invalid json", cancellationToken);
            return;
        }

        if (message is null || string.IsNullOrWhiteSpace(message.Type))
        {
            await SendErrorAsync(null, RealtimeProtocol.ErrorBadRequest, "missing type", cancellationToken);
            return;
        }

        switch (message.Type)
        {
            case RealtimeProtocol.TypeAuth:
                await HandleAuthAsync(message, cancellationToken);
                break;
            case RealtimeProtocol.TypeSubscribe:
                await HandleSubscribeAsync(message, cancellationToken);
                break;
            case RealtimeProtocol.TypePing:
                await SendAsync(RealtimeMessageSerializer.Create(
                    RealtimeProtocol.TypePong,
                    id: message.Id,
                    ts: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), cancellationToken);
                break;
            case RealtimeProtocol.TypeHello:
                // ignore client hello
                break;
            case "media.inject":
            case "lyrics.inject":
            case "media.clear_inject":
            case "playback.command":
                await SendErrorAsync(message.Id, RealtimeProtocol.ErrorNotImplemented, message.Type, cancellationToken);
                break;
            default:
                if (!_authenticated)
                {
                    await SendErrorAsync(message.Id, RealtimeProtocol.ErrorUnauthorized, "authenticate first", cancellationToken);
                    await CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", cancellationToken);
                    return;
                }

                await SendErrorAsync(message.Id, RealtimeProtocol.ErrorBadRequest, $"unknown type: {message.Type}", cancellationToken);
                break;
        }
    }

    public Task SendEventAsync(string eventName, object? payload, CancellationToken cancellationToken = default) =>
        SendAsync(RealtimeMessageSerializer.Create(
            RealtimeProtocol.TypeEvent,
            payload,
            name: eventName,
            ts: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), cancellationToken);

    public async Task SendAsync(RealtimeMessage message, CancellationToken cancellationToken = default)
    {
        if (_closed || _socket.State != WebSocketState.Open)
        {
            return;
        }

        var json = RealtimeMessageSerializer.Serialize(message);
        await _socket.SendTextAsync(json, cancellationToken);
    }

    private async Task HandleAuthAsync(RealtimeMessage message, CancellationToken cancellationToken)
    {
        var payload = RealtimeMessageSerializer.DeserializePayload<RealtimeAuthPayload>(message.Payload);
        var ok = RealtimeAuth.ValidateToken(_options.ExpectedToken, payload?.Token);
        if (!ok)
        {
            await SendAsync(RealtimeMessageSerializer.Create(
                RealtimeProtocol.TypeAuthFail,
                new RealtimeErrorPayload
                {
                    Code = RealtimeProtocol.ErrorUnauthorized,
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

        await SendAsync(RealtimeMessageSerializer.Create(RealtimeProtocol.TypeAuthOk, id: message.Id), cancellationToken);
    }

    private async Task HandleSubscribeAsync(RealtimeMessage message, CancellationToken cancellationToken)
    {
        if (!IsAuthenticated)
        {
            await SendErrorAsync(message.Id, RealtimeProtocol.ErrorUnauthorized, "authenticate first", cancellationToken);
            await CloseAsync(WebSocketCloseStatus.PolicyViolation, "unauthorized", cancellationToken);
            return;
        }

        var payload = RealtimeMessageSerializer.DeserializePayload<RealtimeSubscribePayload>(message.Payload);
        var requested = (payload?.Channels ?? [])
            .Where(channel => !string.IsNullOrWhiteSpace(channel))
            .Select(channel => channel.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (requested.Length == 0 || requested.Any(channel => !RealtimeProtocol.KnownChannels.Contains(channel)))
        {
            await SendErrorAsync(message.Id, RealtimeProtocol.ErrorBadRequest, "invalid channels", cancellationToken);
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

        await SendAsync(RealtimeMessageSerializer.Create(
            RealtimeProtocol.TypeSubscribeOk,
            new RealtimeSubscribePayload { Channels = requested.ToList() },
            id: message.Id), cancellationToken);

        if (_options.OnSubscribedAsync is not null)
        {
            await _options.OnSubscribedAsync(this);
        }
    }

    private Task SendErrorAsync(string? id, string code, string message, CancellationToken cancellationToken) =>
        SendAsync(RealtimeMessageSerializer.Create(
            RealtimeProtocol.TypeError,
            new RealtimeErrorPayload { Code = code, Message = message },
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

public sealed class RealtimeSessionHub
{
    private readonly ConcurrentDictionary<RealtimeSession, byte> _sessions = new();

    public void Add(RealtimeSession session) => _sessions[session] = 0;

    public void Remove(RealtimeSession session) => _sessions.TryRemove(session, out _);

    public IReadOnlyCollection<RealtimeSession> Sessions => _sessions.Keys.ToArray();

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
