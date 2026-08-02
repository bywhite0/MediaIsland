using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using MediaIsland.Services.Media;
using MediaIsland.Services.Media.Platform;
using MediaIsland.Services.MediaLink.Protocol;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.MediaLink;

public sealed class MediaLinkServer : IAsyncDisposable
{
    /// <summary>最大并发会话数；满则拒绝新 TCP 连接。</summary>
    public const int MaxConcurrentSessions = 32;

    /// <summary>HTTP 升级请求头最大字节数（防止畸形请求占内存）。</summary>
    internal const int MaxHttpHeaderBytes = 16 * 1024;

    private readonly MediaLinkSessionHub _hub;
    private readonly Func<MediaLinkSession, Task> _onSubscribedAsync;
    private readonly Func<string> _tokenFactory;
    private readonly MediaLinkInjectionStore? _injectionStore;
    private readonly MediaSourceCoordinator? _coordinator;
    private readonly Func<IMediaPlaybackController?>? _playbackControllerAccessor;
    private readonly Func<MediaInfoChangeKind, Task>? _onEffectiveMediaMutatedAsync;
    private readonly Func<Task>? _onEffectiveLyricsMutatedAsync;
    private readonly ILogger<MediaLinkServer>? _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;
    private Task? _acceptLoop;
    private readonly List<Task> _sessionTasks = [];
    private readonly object _gate = new();
    private int _activeSessions;

    public MediaLinkServer(
        MediaLinkSessionHub hub,
        Func<MediaLinkSession, Task> onSubscribedAsync,
        Func<string> tokenFactory,
        MediaLinkInjectionStore? injectionStore = null,
        MediaSourceCoordinator? coordinator = null,
        Func<IMediaPlaybackController?>? playbackControllerAccessor = null,
        Func<MediaInfoChangeKind, Task>? onEffectiveMediaMutatedAsync = null,
        Func<Task>? onEffectiveLyricsMutatedAsync = null,
        ILogger<MediaLinkServer>? logger = null)
    {
        _hub = hub;
        _onSubscribedAsync = onSubscribedAsync;
        _tokenFactory = tokenFactory;
        _injectionStore = injectionStore;
        _coordinator = coordinator;
        _playbackControllerAccessor = playbackControllerAccessor;
        _onEffectiveMediaMutatedAsync = onEffectiveMediaMutatedAsync;
        _onEffectiveLyricsMutatedAsync = onEffectiveLyricsMutatedAsync;
        _logger = logger;
    }

    public bool IsRunning { get; private set; }

    public string? Endpoint { get; private set; }

    public string? LastError { get; private set; }

    public async Task StartAsync(string listenAddress, int port, CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);

        var token = _tokenFactory();
        if (string.IsNullOrWhiteSpace(token))
        {
            LastError = "Token 为空，拒绝启动。";
            throw new InvalidOperationException(LastError);
        }

        if (!IPAddress.TryParse(listenAddress, out var address))
        {
            LastError = $"无法解析监听地址：{listenAddress}";
            throw new InvalidOperationException(LastError);
        }

        var listener = new TcpListener(address, port);
        listener.Start();
        _listener = listener;
        Endpoint = $"ws://{FormatHost(address)}:{port}{MediaLinkProtocol.Path}";
        LastError = null;
        IsRunning = true;
        _acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _acceptCts.Token;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(token, ct), CancellationToken.None);
        _logger?.LogInformation("MediaLink WS 已监听 {Endpoint}", Endpoint);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsRunning = false;
        Endpoint = null;

        if (_acceptCts is not null)
        {
            try { _acceptCts.Cancel(); } catch { /* ignore */ }
        }

        _listener?.Stop();
        _listener = null;

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { /* ignore */ }
            _acceptLoop = null;
        }

        Task[] sessions;
        lock (_gate)
        {
            sessions = _sessionTasks.ToArray();
            _sessionTasks.Clear();
        }

        try { await Task.WhenAll(sessions); } catch { /* ignore */ }
        await _hub.DisposeAllAsync();
        _acceptCts?.Dispose();
        _acceptCts = null;
        Volatile.Write(ref _activeSessions, 0);
    }

    private async Task AcceptLoopAsync(string token, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Accept 失败");
                continue;
            }

            if (Volatile.Read(ref _activeSessions) >= MaxConcurrentSessions)
            {
                _logger?.LogWarning(
                    "MediaLink 会话数已达上限 {Max}，拒绝新连接",
                    MaxConcurrentSessions);
                try { client.Close(); } catch { /* ignore */ }
                continue;
            }

            var task = Task.Run(() => HandleClientAsync(client, token, cancellationToken), CancellationToken.None);
            lock (_gate)
            {
                _sessionTasks.Add(task);
                _sessionTasks.RemoveAll(t => t.IsCompleted);
            }
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        string token,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            Interlocked.Increment(ref _activeSessions);
            try
            {
                await using var network = client.GetStream();
                if (!await TryUpgradeAsync(network, cancellationToken))
                {
                    return;
                }

                var webSocket = WebSocket.CreateFromStream(
                    network,
                    isServer: true,
                    subProtocol: null,
                    keepAliveInterval: TimeSpan.FromSeconds(30));

                using var linkSocket = new WebSocketMediaLinkSocket(webSocket);
                var session = new MediaLinkSession(
                    linkSocket,
                    new MediaLinkSessionOptions
                    {
                        ExpectedToken = token,
                        OnSubscribedAsync = _onSubscribedAsync,
                        InjectionStore = _injectionStore,
                        Coordinator = _coordinator,
                        PlaybackControllerAccessor = _playbackControllerAccessor,
                        OnEffectiveMediaMutatedAsync = _onEffectiveMediaMutatedAsync,
                        OnEffectiveLyricsMutatedAsync = _onEffectiveLyricsMutatedAsync
                    },
                    _logger);
                _hub.Add(session);
                try
                {
                    session.StartWriter(cancellationToken);
                    var hello = MediaLinkMessageSerializer.Create(
                        MediaLinkProtocol.TypeEvent,
                        new MediaLinkServerHelloPayload
                        {
                            ProtocolVersion = MediaLinkProtocol.Version,
                            AuthRequired = true
                        },
                        name: MediaLinkProtocol.EventServerHello);
                    await session.SendAsync(hello, cancellationToken);
                    await session.RunAsync(cancellationToken);
                }
                finally
                {
                    _hub.Remove(session);
                    await session.DisposeAsync();
                    webSocket.Dispose();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "MediaLink 连接处理失败");
            }
            finally
            {
                Interlocked.Decrement(ref _activeSessions);
            }
        }
    }

    /// <summary>
    /// 纯字节解析 HTTP 升级请求，严格停在 <c>\r\n\r\n</c> 之后，不吞掉后续 WebSocket 帧。
    /// </summary>
    internal static async Task<bool> TryUpgradeAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (!TryParseHttpRequest(
                await ReadHttpHeadersAsync(stream, cancellationToken),
                out var path,
                out var webSocketKey))
        {
            return false;
        }

        if (!path.StartsWith(MediaLinkProtocol.Path, StringComparison.Ordinal))
        {
            var bad = "HTTP/1.1 404 Not Found\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(bad), cancellationToken);
            return false;
        }

        if (string.IsNullOrWhiteSpace(webSocketKey))
        {
            return false;
        }

        var accept = Convert.ToBase64String(
            System.Security.Cryptography.SHA1.HashData(
                Encoding.ASCII.GetBytes(webSocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        return true;
    }

    /// <summary>
    /// 逐字节读到 header 结束符；返回 header 文本（不含 terminator 后的任何字节）。
    /// </summary>
    internal static async Task<string> ReadHttpHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHttpHeaderBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), cancellationToken);
            if (read == 0)
            {
                break;
            }

            length++;
            if (length >= 4 &&
                buffer[length - 4] == (byte)'\r' &&
                buffer[length - 3] == (byte)'\n' &&
                buffer[length - 2] == (byte)'\r' &&
                buffer[length - 1] == (byte)'\n')
            {
                return Encoding.ASCII.GetString(buffer, 0, length);
            }
        }

        throw new InvalidOperationException("HTTP headers too large or incomplete.");
    }

    internal static bool TryParseHttpRequest(string headerText, out string path, out string? webSocketKey)
    {
        path = string.Empty;
        webSocketKey = null;

        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
        {
            return false;
        }

        var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        path = parts[1];
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrEmpty(line))
            {
                break;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
            {
                webSocketKey = value;
            }
        }

        return true;
    }

    private static string FormatHost(IPAddress address) =>
        address.Equals(IPAddress.Any) ? "0.0.0.0" : address.ToString();

    public async ValueTask DisposeAsync() => await StopAsync();
}
