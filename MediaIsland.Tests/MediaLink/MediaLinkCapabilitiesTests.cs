using System.Text.Json;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

/// <summary>
/// server.hello 的能力协商。老服务端不认识 audio 频道会返回 bad_request，
/// 客户端必须能在订阅前判断对端是否支持，而不是靠试错。
/// </summary>
public class MediaLinkCapabilitiesTests
{
    [Fact]
    public void ServerHello_SerializesCapabilitiesAndAudioFormat()
    {
        var payload = new MediaLinkServerHelloPayload
        {
            SessionEpoch = 3,
            Capabilities = [MediaLinkProtocol.CapabilityAudio],
            Audio = new MediaLinkAudioFormatPayload()
        };

        var json = JsonSerializer.Serialize(payload);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32());
        Assert.True(root.GetProperty("authRequired").GetBoolean());
        Assert.Equal(3, root.GetProperty("sessionEpoch").GetInt64());
        Assert.Equal("audio", root.GetProperty("capabilities")[0].GetString());
        Assert.Equal(48000, root.GetProperty("audio").GetProperty("sampleRate").GetInt32());
        Assert.Equal(2, root.GetProperty("audio").GetProperty("channels").GetInt32());
        Assert.Equal("s16le", root.GetProperty("audio").GetProperty("format").GetString());
    }

    [Fact]
    public void ServerHello_OmitsCapabilities_WhenNull()
    {
        // 未声明能力时不应产出空字段——老客户端解析到 null 数组可能崩。
        var json = JsonSerializer.Serialize(new MediaLinkServerHelloPayload { SessionEpoch = 1 });

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("capabilities", out _));
        Assert.False(document.RootElement.TryGetProperty("audio", out _));
    }

    [Fact]
    public void ServerHello_DeserializesFromLegacyPayload()
    {
        // 老服务端的 hello 没有这两个字段，反序列化后必须是 null 而非抛异常。
        const string legacy = """{"protocolVersion":1,"authRequired":true,"sessionEpoch":5}""";

        var payload = JsonSerializer.Deserialize<MediaLinkServerHelloPayload>(legacy);

        Assert.NotNull(payload);
        Assert.Equal(5, payload.SessionEpoch);
        Assert.Null(payload.Capabilities);
        Assert.Null(payload.Audio);
    }

    [Fact]
    public void ServerHello_RoundTripsCapabilitiesAndAudioFormat()
    {
        // 新客户端读新服务端：能力字段必须能原样读回，否则协商无从谈起。
        var json = JsonSerializer.Serialize(new MediaLinkServerHelloPayload
        {
            SessionEpoch = 7,
            Capabilities = [MediaLinkProtocol.CapabilityAudio],
            Audio = new MediaLinkAudioFormatPayload()
        });

        var payload = JsonSerializer.Deserialize<MediaLinkServerHelloPayload>(json);

        Assert.NotNull(payload);
        Assert.Equal([MediaLinkProtocol.CapabilityAudio], payload.Capabilities);
        Assert.NotNull(payload.Audio);
        Assert.Equal(MediaLinkProtocol.AudioSampleRate, payload.Audio.SampleRate);
        Assert.Equal(MediaLinkProtocol.AudioChannels, payload.Audio.Channels);
        Assert.Equal(MediaLinkProtocol.AudioFormat, payload.Audio.Format);
    }

    [Fact]
    public void AudioChannel_IsKnownChannel()
    {
        Assert.Contains(MediaLinkProtocol.ChannelAudio, MediaLinkProtocol.KnownChannels);
    }

    [Fact]
    public void KnownChannels_StillContainsExistingChannels()
    {
        // 回归锁定：新增频道不得影响既有两条。
        Assert.Contains(MediaLinkProtocol.ChannelMedia, MediaLinkProtocol.KnownChannels);
        Assert.Contains(MediaLinkProtocol.ChannelLyrics, MediaLinkProtocol.KnownChannels);
        Assert.Equal(3, MediaLinkProtocol.KnownChannels.Count);
    }

    [Fact]
    public void AudioControlTypes_UseNamespacedWireValues()
    {
        // 线上取值即客户端要发的字符串，改动等同破坏协议，故锁死字面量。
        Assert.Equal("audio.play_start", MediaLinkProtocol.TypeAudioPlayStart);
        Assert.Equal("audio.play_stop", MediaLinkProtocol.TypeAudioPlayStop);
    }
}
