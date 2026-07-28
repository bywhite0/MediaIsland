using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MediaIsland.Services.MediaLink.Protocol;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.MediaLink;

public sealed class MediaLinkServer : IAsyncDisposable
{
    private readonly MediaLinkCertificateStore _certificateStore;
    private readonly MediaLinkSessionHub _hub;
    private readonly Func<MediaLinkSession, Task> _onSubscribedAsync;
    private readonly Func<string> _tokenFactory;
    private readonly ILogger<MediaLinkServer>? _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;
    private Task? _acceptLoop;
    private readonly List<Task> _sessionTasks = [];
    private readonly object _gate = new();

    public MediaLinkServer(
        MediaLinkCertificateStore certificateStore,
        MediaLinkSessionHub hub,
        Func<MediaLinkSession, Task> onSubscribedAsync,
        Func<string> tokenFactory,
        ILogger<MediaLinkServer>? logger = null)
    {
        _certificateStore = certificateStore;
        _hub = hub;
        _onSubscribedAsync = onSubscribedAsync;
        _tokenFactory = tokenFactory;
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

        var certificate = _certificateStore.EnsureCertificate();
        if (!IPAddress.TryParse(listenAddress, out var address))
        {
            LastError = $"无法解析监听地址：{listenAddress}";
            throw new InvalidOperationException(LastError);
        }

        var listener = new TcpListener(address, port);
        listener.Start();
        _listener = listener;
        Endpoint = $"wss://{FormatHost(address)}:{port}{MediaLinkProtocol.Path}";
        LastError = null;
        IsRunning = true;
        _acceptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _acceptCts.Token;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(certificate, token, ct), CancellationToken.None);
        _logger?.LogInformation("MediaLink WSS 已监听 {Endpoint}", Endpoint);
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
    }

    private async Task AcceptLoopAsync(X509Certificate2 certificate, string token, CancellationToken cancellationToken)
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

            var task = Task.Run(() => HandleClientAsync(client, certificate, token, cancellationToken), CancellationToken.None);
            lock (_gate)
            {
                _sessionTasks.Add(task);
                _sessionTasks.RemoveAll(t => t.IsCompleted);
            }
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        X509Certificate2 certificate,
        string token,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await using var network = client.GetStream();
                await using var ssl = new SslStream(network, leaveInnerStreamOpen: false);
                var sslOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    ClientCertificateRequired = false
                };
                await ssl.AuthenticateAsServerAsync(sslOptions, cancellationToken);

                if (!await TryUpgradeAsync(ssl, cancellationToken))
                {
                    return;
                }

                var webSocket = WebSocket.CreateFromStream(
                    ssl,
                    isServer: true,
                    subProtocol: null,
                    keepAliveInterval: TimeSpan.FromSeconds(30));

                var session = new MediaLinkSession(
                    new WebSocketMediaLinkSocket(webSocket),
                    new MediaLinkSessionOptions
                    {
                        ExpectedToken = token,
                        OnSubscribedAsync = _onSubscribedAsync
                    },
                    _logger);
                _hub.Add(session);
                try
                {
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
        }
    }

    private static async Task<bool> TryUpgradeAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return false;
        }

        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 ||
            !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase) ||
            !parts[1].StartsWith(MediaLinkProtocol.Path, StringComparison.Ordinal))
        {
            var bad = "HTTP/1.1 404 Not Found\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
            var badBytes = Encoding.ASCII.GetBytes(bad);
            await stream.WriteAsync(badBytes, cancellationToken);
            return false;
        }

        string? webSocketKey = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null || line.Length == 0)
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
        var bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, cancellationToken);
        return true;
    }

    private static string FormatHost(IPAddress address) =>
        address.Equals(IPAddress.Any) ? "0.0.0.0" : address.ToString();

    public async ValueTask DisposeAsync() => await StopAsync();
}
