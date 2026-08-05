using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaIsland.Services.MediaLink;

/// <summary>
/// 连接配置的可复制文本形式，用于把「地址 + 密钥」一次性搬到另一台设备，
/// 免去手抄 32 字节密钥。
///
/// <para>
/// **这不是加密**。配置码只是把明文 JSON 做 base64url，任何拿到它的人都能解出
/// 密钥。之所以仍要编码而不直接展示 JSON：一是避免用户漏抄、错抄花括号与引号，
/// 二是让"这串东西是机密、不该随手贴出去"这件事在视觉上更明显。
/// 界面必须同时给出明确的保密提示。
/// </para>
/// </summary>
public sealed record MediaLinkConfigCode(string Endpoint, string Token)
{
    /// <summary>前缀便于用户识别这串文本是什么，也便于粘贴时校验。</summary>
    public const string Prefix = "medialink:";

    /// <summary>
    /// 配置码解码后的上限。远超实际需要（地址 + 32 字节密钥约 100 字节），
    /// 用于挡住畸形长输入而非精确限长。
    /// </summary>
    private const int MaxPayloadBytes = 4 * 1024;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Encode()
    {
        var json = JsonSerializer.Serialize(new Payload { E = Endpoint, T = Token }, Options);
        return Prefix + Base64UrlEncode(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// 解析配置码。容忍首尾空白、缺失前缀与用户粘贴时带入的换行；
    /// 内容不可解析时返回 false 并给出面向用户的原因。
    /// </summary>
    public static bool TryParse(string? raw, out MediaLinkConfigCode? code, out string? error)
    {
        code = null;

        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "配置码为空";
            return false;
        }

        // 用户可能从聊天软件复制，带入换行或空格。
        text = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

        if (text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[Prefix.Length..];
        }

        if (text.Length == 0)
        {
            error = "配置码为空";
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Base64UrlDecode(text);
        }
        catch (FormatException)
        {
            error = "配置码格式不正确，请确认已完整复制";
            return false;
        }

        if (bytes.Length is 0 or > MaxPayloadBytes)
        {
            error = "配置码格式不正确，请确认已完整复制";
            return false;
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(bytes, Options);
        }
        catch (JsonException)
        {
            error = "配置码格式不正确，请确认已完整复制";
            return false;
        }

        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.E) ||
            string.IsNullOrWhiteSpace(payload.T))
        {
            error = "配置码缺少地址或密钥";
            return false;
        }

        code = new MediaLinkConfigCode(payload.E.Trim(), payload.T.Trim());
        error = null;
        return true;
    }

    /// <summary>
    /// 生成本机的配置码。<paramref name="listenAddress"/> 为回环或通配时，
    /// 直接写进配置码对另一台设备毫无意义，故替换为本机的局域网地址。
    /// 拿不到局域网地址时返回 null——给出一个连不上的配置码比不给更糟。
    /// </summary>
    public static MediaLinkConfigCode? ForLocalInstance(
        string? listenAddress,
        int port,
        string? token,
        Func<string?>? lanAddressProvider = null)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var address = listenAddress?.Trim() ?? string.Empty;
        var needsLanAddress = address.Length == 0 ||
                              address is "0.0.0.0" or "::" or "127.0.0.1" or "::1" or "[::1]" ||
                              address.StartsWith("127.", StringComparison.Ordinal) ||
                              address.Equals("localhost", StringComparison.OrdinalIgnoreCase);

        if (needsLanAddress)
        {
            address = (lanAddressProvider ?? GetLanAddress)() ?? string.Empty;
            if (address.Length == 0)
            {
                return null;
            }
        }

        return new MediaLinkConfigCode($"{address}:{port}", token);
    }

    /// <summary>本机第一个非回环 IPv4 地址；取不到返回 null。</summary>
    private static string? GetLanAddress()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(a =>
                    a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(a))
                ?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }

    /// <summary>字段名刻意用单字母：配置码要短到便于复制粘贴。</summary>
    private sealed class Payload
    {
        [JsonPropertyName("e")]
        public string? E { get; set; }

        [JsonPropertyName("t")]
        public string? T { get; set; }
    }
}
