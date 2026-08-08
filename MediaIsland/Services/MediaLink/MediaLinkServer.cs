using System.Collections.Concurrent;
using System.Net;
using System.Web;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using MediaIsland.Services.Media;
using MediaIsland.Services.Media.Platform;
using MediaIsland.Services.MediaLink.Mapping;
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

    /// <summary>握手阶段整体读超时。</summary>
    private static readonly TimeSpan HandshakeReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>停服时等待单个会话完成关闭握手的上限。</summary>
    private static readonly TimeSpan GoingAwayTimeout = TimeSpan.FromSeconds(1);

    private readonly MediaLinkSessionHub _hub;
    private readonly Func<MediaLinkSession, Task> _onSubscribedAsync;
    private readonly Func<string> _tokenFactory;
    private readonly Func<string>? _allowedOriginsAccessor;
    private readonly MediaLinkInjectionStore? _injectionStore;
    private readonly MediaSourceCoordinator? _coordinator;
    private readonly Func<IMediaPlaybackController?>? _playbackControllerAccessor;
    private readonly Func<MediaInfoChangeKind, Task>? _onEffectiveMediaMutatedAsync;
    private readonly Func<Task>? _onEffectiveLyricsMutatedAsync;
    private readonly Func<MediaLinkAudioFrameHeader, byte[], Task>? _onAudioFrameAsync;
    private readonly ILogger<MediaLinkServer>? _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;
    private Task? _acceptLoop;
    private readonly List<Task> _sessionTasks = [];
    private readonly ConcurrentDictionary<string, AuthFailureWindow> _authFailures = new();
    private string _listenAddress = "127.0.0.1";
    private readonly object _gate = new();
    private int _activeSessions;

    public MediaLinkServer(
        MediaLinkSessionHub hub,
        Func<MediaLinkSession, Task> onSubscribedAsync,
        Func<string> tokenFactory,
        Func<string>? allowedOriginsAccessor = null,
        MediaLinkInjectionStore? injectionStore = null,
        MediaSourceCoordinator? coordinator = null,
        Func<IMediaPlaybackController?>? playbackControllerAccessor = null,
        Func<MediaInfoChangeKind, Task>? onEffectiveMediaMutatedAsync = null,
        Func<Task>? onEffectiveLyricsMutatedAsync = null,
        Func<MediaLinkAudioFrameHeader, byte[], Task>? onAudioFrameAsync = null,
        ILogger<MediaLinkServer>? logger = null)
    {
        _hub = hub;
        _onSubscribedAsync = onSubscribedAsync;
        _tokenFactory = tokenFactory;
        _allowedOriginsAccessor = allowedOriginsAccessor;
        _injectionStore = injectionStore;
        _coordinator = coordinator;
        _playbackControllerAccessor = playbackControllerAccessor;
        _onEffectiveMediaMutatedAsync = onEffectiveMediaMutatedAsync;
        _onEffectiveLyricsMutatedAsync = onEffectiveLyricsMutatedAsync;
        _onAudioFrameAsync = onAudioFrameAsync;
        _logger = logger;
    }

    public bool IsRunning { get; private set; }

    public string? Endpoint { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>
    /// 当前 listener 实例代号，进程内单调递增。随 <see cref="StartAsync"/> 递增，
    /// 供客户端区分「seq 归零」是服务端重建监听器而非消息乱序。
    /// </summary>
    public long SessionEpoch { get; private set; }

    private static long _epochCounter;

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
        SessionEpoch = Interlocked.Increment(ref _epochCounter);
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

        // 先停止 accept 再发关闭帧：新连接不再进来，但已有会话的 socket 仍存活。
        // 顺序不能反 —— 取消 _acceptCts 会终止 RunAsync 并 Dispose socket，关闭帧就发不出去了。
        _listener?.Stop();
        _listener = null;

        await _hub.CloseAllGoingAwayAsync(GoingAwayTimeout, cancellationToken);

        if (_acceptCts is not null)
        {
            try { _acceptCts.Cancel(); } catch { /* ignore */ }
        }

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

            if (GetRemoteIp(client) is { } remoteIp && IsAuthRateLimited(remoteIp))
            {
                _logger?.LogWarning("MediaLink 认证失败次数超限，暂时拒绝来自 {Ip} 的连接", remoteIp);
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
                var remoteIp = GetRemoteIp(client);
                var allowedOrigins = ParseAllowedOrigins(_allowedOriginsAccessor?.Invoke());

                string headerText;
                try
                {
                    using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    headerCts.CancelAfter(HandshakeReadTimeout);
                    headerText = await ReadHttpHeadersAsync(network, headerCts.Token);
                }
                catch (Exception)
                {
                    return;
                }

                if (!TryParseHttpRequest(headerText, out var requestPath, out var requestMethod, out var requestHeaders))
                {
                    return;
                }

                // 缩略图端点与 WS 共用同一 listener 与同一 Token。
                if (SplitPath(requestPath).Equals(MediaLinkProtocol.ThumbnailPath, StringComparison.Ordinal))
                {
                    await HandleThumbnailRequestAsync(network, requestPath, cancellationToken);
                    return;
                }

                if (!await TryUpgradeParsedAsync(
                        network, requestPath, requestMethod, requestHeaders,
                        _listenAddress, allowedOrigins, cancellationToken))
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
                        OnAuthFailed = () =>
                        {
                            if (remoteIp is not null)
                            {
                                RecordAuthFailure(remoteIp);
                            }
                        },
                        OnAuthSucceeded = () =>
                        {
                            if (remoteIp is not null)
                            {
                                ClearAuthFailures(remoteIp);
                            }
                        },
                        InjectionStore = _injectionStore,
                        Coordinator = _coordinator,
                        PlaybackControllerAccessor = _playbackControllerAccessor,
                        OnEffectiveMediaMutatedAsync = _onEffectiveMediaMutatedAsync,
                        OnEffectiveLyricsMutatedAsync = _onEffectiveLyricsMutatedAsync,
                        OnAudioFrameAsync = _onAudioFrameAsync
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
                            AuthRequired = true,
                            SessionEpoch = SessionEpoch,
                            Capabilities = [MediaLinkProtocol.CapabilityAudio],
                            Audio = new MediaLinkAudioFormatPayload()
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
    /// <param name="allowedOrigins">
    /// Origin 白名单。空或 null 时拒绝所有带 <c>Origin</c> 头的连接；无 Origin 头的原生客户端始终放行。
    /// </param>
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
            readCts.CancelAfter(HandshakeReadTimeout);
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

        return await TryUpgradeParsedAsync(
            stream, path, method, headers, listenAddress, allowedOrigins, cancellationToken);
    }

    /// <summary>
    /// 已解析 header 后的升级校验与 101 应答。与 <see cref="TryUpgradeAsync"/> 共用同一套校验规则。
    /// </summary>
    private static async Task<bool> TryUpgradeParsedAsync(
        Stream stream,
        string path,
        string method,
        Dictionary<string, string> headers,
        string listenAddress,
        HashSet<string>? allowedOrigins,
        CancellationToken cancellationToken)
    {
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

    /// <summary>剥离 query，返回纯路径部分。</summary>
    internal static string SplitPath(string path) => path.Split('?', 2)[0];

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
        out string? webSocketKey,
        IReadOnlySet<string>? localAddresses = null)
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

        // Sec-WebSocket-Version 必须存在且为 13。RFC 6455 要求客户端必发此头，
        // 缺失与版本不符同样处理：426 并回告本端支持的版本。
        if (!headers.TryGetValue("Sec-WebSocket-Version", out var version) ||
            version.Trim() != "13")
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

        // Host header: 必须存在（HTTP/1.1 强制）且指向本机
        if (!headers.TryGetValue("Host", out var host) || string.IsNullOrWhiteSpace(host))
        {
            return "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
        }

        if (!IsHostAllowed(NormalizeHostHeader(host), listenAddress, localAddresses))
        {
            return "HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
        }

        // Origin header: if present, must be in the allowed set.
        // Empty/null allowedOrigins = reject every connection that carries an Origin.
        if (headers.TryGetValue("Origin", out var origin))
        {
            if (allowedOrigins is null || !allowedOrigins.Contains(origin.Trim()))
            {
                return "HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
            }
        }

        return null; // valid
    }

    /// <summary>
    /// 解析 Origin 白名单设置（分号或逗号分隔）。空集合表示拒绝所有带 Origin 的连接。
    /// </summary>
    internal static HashSet<string> ParseAllowedOrigins(string? raw)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (var entry in raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// 剥离 <c>Host</c> 头的端口部分。IPv6 字面量形如 <c>[::1]:17654</c>，方括号内的冒号不是端口分隔符。
    /// </summary>
    internal static string NormalizeHostHeader(string host)
    {
        var trimmed = host.Trim();
        if (trimmed.StartsWith('['))
        {
            var end = trimmed.IndexOf(']');
            return end > 0 ? trimmed[..(end + 1)] : trimmed;
        }

        var separator = trimmed.IndexOf(':');
        return separator >= 0 ? trimmed[..separator] : trimmed;
    }

    private static bool IsHostAllowed(string host, string listenAddress, IReadOnlySet<string>? localAddresses)
    {
        // Loopback variants are always local.
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("127.0.0.1", StringComparison.Ordinal) ||
            host.Equals("[::1]", StringComparison.Ordinal) ||
            host.Equals("::1", StringComparison.Ordinal))
        {
            return true;
        }

        // Allow the listen address itself.
        if (host.Equals(listenAddress, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Wildcard listen address: accept any address that actually belongs to this machine.
        // 不能无条件放行，否则 DNS rebinding 恰好在唯一需要防护的配置下失效。
        if (listenAddress is "0.0.0.0" or "::")
        {
            return (localAddresses ?? GetLocalAddresses()).Contains(host);
        }

        return false;
    }

    /// <summary>
    /// 本机所有单播地址的字面量形式（IPv6 带方括号）。缓存 60s，避免每次握手枚举网卡。
    /// </summary>
    private static IReadOnlySet<string> GetLocalAddresses()
    {
        var cached = Volatile.Read(ref _localAddressCache);
        if (cached is not null && (DateTimeOffset.UtcNow - cached.CapturedAt).TotalSeconds < 60)
        {
            return cached.Addresses;
        }

        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                addresses.Add(address.AddressFamily == AddressFamily.InterNetworkV6
                    ? $"[{address}]"
                    : address.ToString());
            }
        }
        catch (Exception)
        {
            // 枚举失败时退化为仅回环，宁可误拒也不误放。
        }

        Volatile.Write(ref _localAddressCache, new LocalAddressCache(addresses, DateTimeOffset.UtcNow));
        return addresses;
    }

    private static LocalAddressCache? _localAddressCache;

    private sealed record LocalAddressCache(IReadOnlySet<string> Addresses, DateTimeOffset CapturedAt);

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

    /// <summary>
    /// 认证成功后清零该 IP 的失败计数。多台设备经 NAT 共享出口 IP 时，
    /// 一台填错 Token 不应持续影响同网段其它实例。
    /// </summary>
    internal void ClearAuthFailures(string ip) => _authFailures.TryRemove(ip, out _);

    private static string? GetRemoteIp(TcpClient client)
    {
        try
        {
            return (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>GET /v1/thumbnail?token=&lt;token&gt;&amp;t=&lt;trackToken&gt;</c>。
    /// 浏览器 <c>&lt;img&gt;</c> 无法带自定义头，故 Token 只能走 query，响应一律 no-store。
    /// </summary>
    private async Task HandleThumbnailRequestAsync(Stream stream, string path, CancellationToken cancellationToken)
    {
        var query = path.Contains('?') ? path.Split('?', 2)[1] : string.Empty;
        var queryParams = HttpUtility.ParseQueryString(query);

        if (!MediaLinkAuth.ValidateToken(_tokenFactory(), queryParams["token"]))
        {
            await WriteSimpleResponseAsync(stream, "401 Unauthorized", cancellationToken);
            return;
        }

        var media = _coordinator?.GetMediaForPush();
        if (media is null)
        {
            await WriteSimpleResponseAsync(stream, "404 Not Found", cancellationToken);
            return;
        }

        // t 仅做存在性比对，用于客户端缓存失效；不匹配即视为已过期。
        var requestedTrackToken = queryParams["t"];
        if (!string.IsNullOrEmpty(requestedTrackToken))
        {
            var currentTrackToken = MediaLinkDtoMapper.ComputeTrackToken(
                media.SourceApp, media.Title, media.Artist, media.AlbumTitle);
            if (!string.Equals(requestedTrackToken, currentTrackToken, StringComparison.Ordinal))
            {
                await WriteSimpleResponseAsync(stream, "404 Not Found", cancellationToken);
                return;
            }
        }

        byte[]? png;
        try
        {
            png = await MediaLinkThumbnail.EncodePngAsync(media, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "MediaLink 缩略图编码失败");
            await WriteSimpleResponseAsync(stream, "500 Internal Server Error", cancellationToken);
            return;
        }

        if (png is null || png.Length == 0)
        {
            await WriteSimpleResponseAsync(stream, "404 Not Found", cancellationToken);
            return;
        }

        var header =
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {MediaLinkThumbnail.MimeType}\r\n" +
            $"Content-Length: {png.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
        await stream.WriteAsync(png, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static Task WriteSimpleResponseAsync(Stream stream, string status, CancellationToken cancellationToken) =>
        stream.WriteAsync(
            Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"),
            cancellationToken).AsTask();

    private sealed record AuthFailureWindow(int Count, DateTimeOffset WindowStart);

    private static string FormatHost(IPAddress address) =>
        address.Equals(IPAddress.Any) ? "0.0.0.0" : address.ToString();

    public async ValueTask DisposeAsync() => await StopAsync();
}
