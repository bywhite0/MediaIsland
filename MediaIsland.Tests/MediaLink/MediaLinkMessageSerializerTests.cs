using System.Text.Json;
using MediaIsland.Services.MediaLink.Protocol;
using Xunit;

namespace MediaIsland.Tests.MediaLink;

public class MediaLinkMessageSerializerTests
{
    [Fact]
    public void Envelope_RoundTrips_TypeIdTsNameAndPayload()
    {
        var original = MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeEvent,
            new MediaLinkErrorPayload { Code = "x", Message = "y" },
            id: "req-1",
            name: MediaLinkProtocol.EventMediaUpdated,
            ts: 1_710_000_000_000);

        var json = MediaLinkMessageSerializer.Serialize(original);
        var restored = MediaLinkMessageSerializer.Deserialize(json);

        Assert.NotNull(restored);
        Assert.Equal(MediaLinkProtocol.Version, restored.V);
        Assert.Equal(MediaLinkProtocol.TypeEvent, restored.Type);
        Assert.Equal("req-1", restored.Id);
        Assert.Equal(1_710_000_000_000, restored.Ts);
        Assert.Equal(MediaLinkProtocol.EventMediaUpdated, restored.Name);
        Assert.NotNull(restored.Payload);

        var error = MediaLinkMessageSerializer.DeserializePayload<MediaLinkErrorPayload>(restored.Payload);
        Assert.NotNull(error);
        Assert.Equal("x", error.Code);
        Assert.Equal("y", error.Message);
    }

    [Fact]
    public void Envelope_AllowsNullPayload()
    {
        var message = MediaLinkMessageSerializer.Create(
            MediaLinkProtocol.TypeEvent,
            payload: null,
            name: MediaLinkProtocol.EventLyricsUpdated,
            ts: 42);

        var json = MediaLinkMessageSerializer.Serialize(message);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("payload", out _));

        var restored = MediaLinkMessageSerializer.Deserialize(json);
        Assert.NotNull(restored);
        Assert.Null(restored.Payload);
        Assert.Null(MediaLinkMessageSerializer.DeserializePayload<MediaLinkLyricsDto>(restored.Payload));
    }

    [Fact]
    public void Deserialize_IgnoresUnknownProperties()
    {
        const string json = """
            {"v":1,"type":"ping","ts":1,"unknownField":true,"nested":{"a":1}}
            """;

        var message = MediaLinkMessageSerializer.Deserialize(json);
        Assert.NotNull(message);
        Assert.Equal(MediaLinkProtocol.TypePing, message.Type);
        Assert.Equal(1, message.Ts);
    }

    [Fact]
    public void ServerHello_OmitsCertFingerprint()
    {
        var json = MediaLinkMessageSerializer.Serialize(
            MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeEvent,
                new MediaLinkServerHelloPayload
                {
                    ProtocolVersion = MediaLinkProtocol.Version,
                    AuthRequired = true
                },
                name: MediaLinkProtocol.EventServerHello));

        Assert.DoesNotContain("certFingerprint", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authRequired", json, StringComparison.OrdinalIgnoreCase);
    }
}
