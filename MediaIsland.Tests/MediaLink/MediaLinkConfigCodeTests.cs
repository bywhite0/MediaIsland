using MediaIsland.Services.MediaLink;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// 连接配置码。它承载密钥且未加密，所以这里既验证可用性，
/// 也把"不该给出一个连不上的配置码"这类判断固定下来。
/// </summary>
public class MediaLinkConfigCodeTests
{
    [Fact]
    public void EncodeThenParse_RoundTrips()
    {
        var original = new MediaLinkConfigCode("192.168.1.201:21757", "tok-abc_123-XYZ");

        Assert.True(MediaLinkConfigCode.TryParse(original.Encode(), out var parsed, out _));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void Encode_CarriesRecognizablePrefix()
    {
        // 前缀让用户看得出这串文本是什么，粘贴时也便于校验。
        var code = new MediaLinkConfigCode("host:1", "t").Encode();
        Assert.StartsWith(MediaLinkConfigCode.Prefix, code);
    }

    [Fact]
    public void Encode_DoesNotLeakTokenInPlainText()
    {
        // 不是加密，但至少不能让密钥在界面上直接可读——否则截图即泄露。
        var code = new MediaLinkConfigCode("host:1", "SuperSecretToken").Encode();
        Assert.DoesNotContain("SuperSecretToken", code);
    }

    [Theory]
    [InlineData("32 字节密钥常见长度")]
    [InlineData("含-连字符_与下划线")]
    public void RoundTrip_PreservesTokenExactly(string token)
    {
        var code = new MediaLinkConfigCode("host:1", token).Encode();

        Assert.True(MediaLinkConfigCode.TryParse(code, out var parsed, out _));
        Assert.Equal(token, parsed!.Token);
    }

    [Fact]
    public void TryParse_ToleratesWhitespaceAndNewlinesFromChatApps()
    {
        // 从聊天软件复制常带入换行与空格。
        var code = new MediaLinkConfigCode("192.168.1.5:21757", "tok").Encode();
        var mangled = "  " + code[..10] + "\r\n  " + code[10..] + "\n";

        Assert.True(MediaLinkConfigCode.TryParse(mangled, out var parsed, out _));
        Assert.Equal("192.168.1.5:21757", parsed!.Endpoint);
    }

    [Fact]
    public void TryParse_AcceptsCodeWithoutPrefix()
    {
        var code = new MediaLinkConfigCode("host:1", "tok").Encode();
        var withoutPrefix = code[MediaLinkConfigCode.Prefix.Length..];

        Assert.True(MediaLinkConfigCode.TryParse(withoutPrefix, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryParse_Empty_Fails(string? input)
    {
        Assert.False(MediaLinkConfigCode.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("medialink:!!!not-base64!!!")]
    [InlineData("medialink:")]
    [InlineData("完全不相干的一段文字")]
    public void TryParse_Malformed_FailsWithReason(string input)
    {
        Assert.False(MediaLinkConfigCode.TryParse(input, out var code, out var error));
        Assert.Null(code);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParse_ValidBase64ButNotOurJson_Fails()
    {
        // base64 解得开但内容不是配置码：不能当成有效输入。
        var junk = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"hello\":1}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.False(MediaLinkConfigCode.TryParse(MediaLinkConfigCode.Prefix + junk, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void ForLocalInstance_SubstitutesLanAddress_WhenListeningOnLoopback()
    {
        // 回环地址写进配置码对另一台设备毫无意义。
        var code = MediaLinkConfigCode.ForLocalInstance(
            "127.0.0.1", 21757, "tok", lanAddressProvider: () => "192.168.1.7");

        Assert.Equal("192.168.1.7:21757", code!.Endpoint);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("localhost")]
    [InlineData("127.0.1.1")]
    [InlineData("")]
    public void ForLocalInstance_SubstitutesLanAddress_ForAllNonRoutableForms(string listen)
    {
        var code = MediaLinkConfigCode.ForLocalInstance(
            listen, 21757, "tok", lanAddressProvider: () => "10.0.0.3");

        Assert.Equal("10.0.0.3:21757", code!.Endpoint);
    }

    [Fact]
    public void ForLocalInstance_KeepsExplicitLanAddress()
    {
        var code = MediaLinkConfigCode.ForLocalInstance(
            "192.168.1.50", 21757, "tok", lanAddressProvider: () => "should-not-be-used");

        Assert.Equal("192.168.1.50:21757", code!.Endpoint);
    }

    [Fact]
    public void ForLocalInstance_NoLanAddress_ReturnsNull()
    {
        // 给出一个必然连不上的配置码比不给更糟：用户会以为配好了。
        var code = MediaLinkConfigCode.ForLocalInstance(
            "127.0.0.1", 21757, "tok", lanAddressProvider: () => null);

        Assert.Null(code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ForLocalInstance_NoToken_ReturnsNull(string? token)
    {
        var code = MediaLinkConfigCode.ForLocalInstance(
            "192.168.1.50", 21757, token, lanAddressProvider: () => "192.168.1.50");

        Assert.Null(code);
    }

    [Fact]
    public void ForLocalInstance_ProducesParsableCode()
    {
        // 端到端：生成的配置码必须能被接收端解析。
        var code = MediaLinkConfigCode.ForLocalInstance(
            "0.0.0.0", 21757, "real-token", lanAddressProvider: () => "192.168.1.9");

        Assert.True(MediaLinkConfigCode.TryParse(code!.Encode(), out var parsed, out _));
        Assert.Equal("192.168.1.9:21757", parsed!.Endpoint);
        Assert.Equal("real-token", parsed.Token);
    }

    [Fact]
    public void ParsedCode_FeedsUpstreamEndpointResolution()
    {
        // 配置码里的地址必须能被上游服务直接接受，否则粘贴完还是连不上。
        var code = MediaLinkConfigCode.ForLocalInstance(
            "0.0.0.0", 21757, "tok", lanAddressProvider: () => "192.168.1.9");
        Assert.True(MediaLinkConfigCode.TryParse(code!.Encode(), out var parsed, out _));

        Assert.True(MediaLinkUpstreamHostedService.TryBuildEndpoint(parsed!.Endpoint, out var uri, out _));
        Assert.Equal("ws://192.168.1.9:21757/v1/ws", uri.ToString());
    }
}
