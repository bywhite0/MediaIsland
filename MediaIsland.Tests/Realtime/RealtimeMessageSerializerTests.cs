using System.Text.Json;
using MediaIsland.Services.Realtime.Protocol;
using Xunit;

namespace MediaIsland.Tests.Realtime;

public class RealtimeMessageSerializerTests
{
    [Fact]
    public void Envelope_RoundTrips_TypeIdTsNameAndPayload()
    {
        var original = RealtimeMessageSerializer.Create(
            RealtimeProtocol.TypeEvent,
            new RealtimeErrorPayload { Code = "x", Message = "y" },
            id: "req-1",
            name: RealtimeProtocol.EventMediaUpdated,
            ts: 1_710_000_000_000);

        var json = RealtimeMessageSerializer.Serialize(original);
        var restored = RealtimeMessageSerializer.Deserialize(json);

        Assert.NotNull(restored);
        Assert.Equal(RealtimeProtocol.Version, restored.V);
        Assert.Equal(RealtimeProtocol.TypeEvent, restored.Type);
        Assert.Equal("req-1", restored.Id);
        Assert.Equal(1_710_000_000_000, restored.Ts);
        Assert.Equal(RealtimeProtocol.EventMediaUpdated, restored.Name);
        Assert.NotNull(restored.Payload);

        var error = RealtimeMessageSerializer.DeserializePayload<RealtimeErrorPayload>(restored.Payload);
        Assert.NotNull(error);
        Assert.Equal("x", error.Code);
        Assert.Equal("y", error.Message);
    }

    [Fact]
    public void Envelope_AllowsNullPayload()
    {
        var message = RealtimeMessageSerializer.Create(
            RealtimeProtocol.TypeEvent,
            payload: null,
            name: RealtimeProtocol.EventLyricsUpdated,
            ts: 42);

        var json = RealtimeMessageSerializer.Serialize(message);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("payload", out _));

        var restored = RealtimeMessageSerializer.Deserialize(json);
        Assert.NotNull(restored);
        Assert.Null(restored.Payload);
        Assert.Null(RealtimeMessageSerializer.DeserializePayload<RealtimeLyricsDto>(restored.Payload));
    }

    [Fact]
    public void Deserialize_IgnoresUnknownProperties()
    {
        const string json = """
            {"v":1,"type":"ping","ts":1,"unknownField":true,"nested":{"a":1}}
            """;

        var message = RealtimeMessageSerializer.Deserialize(json);
        Assert.NotNull(message);
        Assert.Equal(RealtimeProtocol.TypePing, message.Type);
        Assert.Equal(1, message.Ts);
    }
}
