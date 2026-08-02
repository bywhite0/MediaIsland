using System.Collections.Concurrent;
using System.Net;
using System.Web;
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

    /// <summary>单 IP 认证失败次数上限。</summary>
    internal const int AuthFailureLimit = 5;

    /// <summary>认证失败滑动窗口（秒）。</summary>
    internal const int AuthFailureWindowSeconds = 60;

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
    private readonly ConcurrentDictionary<string, AuthFailureWindow> _authFailures = new();
    private string _listenAddress = "127.0.0.1";
    private HashSet<string>? _allowedOrigins;
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

        _listenAddress = listenAddress;
        var listener = new TcpListener(address, port);
        listener.Start();
        _listener = listener;
        var actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Endpoint = $"ws://{FormatHost(address)}:{actualPort}{MediaLinkProtocol.Path}";
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
                if (!await TryUpgradeAsync(network, _listenAddress, _allowedOrigins, cancellationToken))
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
    internal static async Task<bool> TryUpgradeAsync(
        Stream stream,
        string listenAddress = "127.0.0.1",
        HashSet<string>? allowedOrigins = null,
        CancellationToken cancellationToken = default)
    {
        string headerText;
        try
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(TimeSpan.FromSeconds(5));
            headerText = await ReadHttpHeadersAsync(stream, readCts.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (!TryParseHttpRequest(headerText, out var path, out var method, out var headers))
        {
            return false;
        }

        var errorResponse = ValidateUpgradeRequest(path, method, headers, listenAddress, allowedOrigins, out var webSocketKey);
        if (errorResponse is not null)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(errorResponse), cancellationToken);
            return false;
        }

        if (webSocketKey is null)
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

    internal static bool TryParseHttpRequest(
        string headerText,
        out string path,
        out string method,
        out Dictionary<string, string> headers)
    {
        path = string.Empty;
        method = string.Empty;
        headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
        {
            return false;
        }

        var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        method = parts[0];
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
            headers[name] = value;
        }

        return true;
    }

    /// <summary>
    /// 严格校验 WebSocket 升级请求。返回 null 表示通过；返回非空字符串为 HTTP 错误响应。
    /// </summary>
    internal static string? ValidateUpgradeRequest(
        string path,
        string method,
        Dictionary<string, string> headers,
        string listenAddress,
        HashSet<string>? allowedOrigins,
        out string? webSocketKey)
    {
        webSocketKey = null;

        // Method must be GET
        if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            return "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
        }

        // Path must be exactly /v1/ws (allow query string)
        var pathOnly = path.Split('?', 2)[0];
        if (!pathOnly.Equals(MediaLinkProtocol.Path, StringComparison.Ordinal))
        {
            return "HTTP/1.1 404 Not Found\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
        }

        // Connection must contain "Upgrade"
        if (!headers.TryGetValue("Connection", out var connection) ||
            !connection.Contains("Upgrade", StringComparison.OrdinalIgnoreCase))
        {
            return "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
        }

        // Upgrade must be "websocket"
        if (!headers.TryGetValue("Upgrade", out var upgrade) ||
            !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
        {
            return "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
        }

        // Sec-WebSocket-Version must be 13
        if (headers.TryGetValue("Sec-WebSocket-Version", out var version) &&
            version != "13")
        {
            return "HTTP/1.1 426 Upgrade Required\r\nSec-WebSocket-Version: 13\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
        }

        // Sec-WebSocket-Key must exist and be 16 bytes base64
        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key) ||
            string.IsNullOrWhiteSpace(key))
        {
            return "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
        }

        try
        {
            var keyBytes = Convert.FromBase64String(key);
            if (keyBytes.Length != 16)
            {
                return "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
            }
        }
        catch (FormatException)
        {
            return "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
        }

        webSocketKey = key;

        // Host header: must match listen address
        if (headers.TryGetValue("Host", out var host))
        {
            var hostOnly = host.Split(':', 2)[0].Trim();
            if (!IsHostAllowed(hostOnly, listenAddress))
            {
                return "HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
            }
        }

        // Origin header: if present, must be in allowed set.
        // Null allowedOrigins = accept all Origins (no restriction).
        // Empty allowedOrigins = reject all Origins.
        if (headers.TryGetValue("Origin", out var origin) && allowedOrigins is not null)
        {
            if (!allowedOrigins.Contains(origin) && !allowedOrigins.Contains("null"))
            {
                return "HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
            }
        }

        return null; // valid
    }

    private static bool IsHostAllowed(string host, string listenAddress)
    {
        // Allow localhost variants
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("127.0.0.1", StringComparison.Ordinal) ||
            host.Equals("[::1]", StringComparison.Ordinal))
        {
            return true;
        }

        // Allow the listen address itself
        if (host.Equals(listenAddress, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Allow 0.0.0.0 listen address to accept any host
        if (listenAddress == "0.0.0.0")
        {
            return true;
        }

        return false;
    }

    /// <summary>向后兼容的旧接口。</summary>
    internal static bool TryParseHttpRequest(string headerText, out string path, out string? webSocketKey)
    {
        if (!TryParseHttpRequest(headerText, out path, out _, out var headers))
        {
            webSocketKey = null;
            return false;
        }
        headers.TryGetValue("Sec-WebSocket-Key", out webSocketKey);
        return true;
    }

    internal void RecordAuthFailure(string ip)
    {
        var now = DateTimeOffset.UtcNow;
        _authFailures.AddOrUpdate(ip,
            _ => new AuthFailureWindow(1, now),
            (_, existing) =>
            {
                if ((now - existing.WindowStart).TotalSeconds > AuthFailureWindowSeconds)
                    return new AuthFailureWindow(1, now);
                return new AuthFailureWindow(existing.Count + 1, existing.WindowStart);
            });
    }

    internal bool IsAuthRateLimited(string ip)
    {
        if (!_authFailures.TryGetValue(ip, out var window))
            return false;
        if ((DateTimeOffset.UtcNow - window.WindowStart).TotalSeconds > AuthFailureWindowSeconds)
        {
            _authFailures.TryRemove(ip, out _);
            return false;
        }
        return window.Count >= AuthFailureLimit;
    }

        private async Task HandleThumbnailRequest(Stream stream, string path, Dictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var query = path.Contains('?') ? path.Split('?', 2)[1] : string.Empty;
        var queryParams = System.Web.HttpUtility.ParseQueryString(query);
        var token = queryParams["token"];
        var expectedToken = _tokenFactory();
        if (string.IsNullOrEmpty(token) || expectedToken.Length == 0 ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expectedToken)))
        {
            var resp = "HTTP/1.1 401 Unauthorized\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(resp), cancellationToken);
            return;
        }

        var media = _coordinator?.GetMediaForPush();
        if (media?.Thumbnail is null && media?.ThumbnailSource is null)
        {
            var resp = "HTTP/1.1 404 Not Found\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(resp), cancellationToken);
            return;
        }

        // Full bitmap encoding not yet implemented; return 501 for now.
        var notImpl = "HTTP/1.1 501 Not Implemented\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(notImpl), cancellationToken);
    }
    private sealed record AuthFailureWindow(int Count, DateTimeOffset WindowStart);

    private static string FormatHost(IPAddress address) =>
        address.Equals(IPAddress.Any) ? "0.0.0.0" : address.ToString();

    public async ValueTask DisposeAsync() => await StopAsync();
}
