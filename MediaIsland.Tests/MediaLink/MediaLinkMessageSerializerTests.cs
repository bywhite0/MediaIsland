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

    [Fact]
    public void Ok_RoundTrips_ForField()
    {
        var json = MediaLinkMessageSerializer.Serialize(
            MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeOk,
                new MediaLinkOkPayload { For = MediaLinkProtocol.TypeMediaInject },
                id: "req-inject-1",
                ts: 100));

        var msg = MediaLinkMessageSerializer.Deserialize(json);
        Assert.NotNull(msg);
        Assert.Equal(MediaLinkProtocol.TypeOk, msg.Type);
        Assert.Equal("req-inject-1", msg.Id);
        var payload = MediaLinkMessageSerializer.DeserializePayload<MediaLinkOkPayload>(msg.Payload);
        Assert.NotNull(payload);
        Assert.Equal(MediaLinkProtocol.TypeMediaInject, payload.For);
    }

    [Fact]
    public void Error_RoundTrips_NotSupported_And_NoSession()
    {
        foreach (var code in new[]
                 {
                     MediaLinkProtocol.ErrorNotSupported,
                     MediaLinkProtocol.ErrorNoSession
                 })
        {
            var json = MediaLinkMessageSerializer.Serialize(
                MediaLinkMessageSerializer.Create(
                    MediaLinkProtocol.TypeError,
                    new MediaLinkErrorPayload { Code = code, Message = "x" },
                    id: "e1"));
            var msg = MediaLinkMessageSerializer.Deserialize(json)!;
            var err = MediaLinkMessageSerializer.DeserializePayload<MediaLinkErrorPayload>(msg.Payload)!;
            Assert.Equal(code, err.Code);
        }
    }

    [Fact]
    public void MediaInject_RoundTrips_RequiredFields()
    {
        var json = MediaLinkMessageSerializer.Serialize(
            MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeMediaInject,
                new MediaLinkMediaInjectPayload
                {
                    SourceApp = "external",
                    Title = "Song",
                    Artist = "A",
                    AlbumTitle = "Alb",
                    PositionMs = 1500,
                    DurationMs = 200000,
                    PlaybackState = "Playing",
                    PlaybackRate = 1.0
                },
                id: "m1"));

        var msg = MediaLinkMessageSerializer.Deserialize(json)!;
        Assert.Equal(MediaLinkProtocol.TypeMediaInject, msg.Type);
        var p = MediaLinkMessageSerializer.DeserializePayload<MediaLinkMediaInjectPayload>(msg.Payload)!;
        Assert.Equal("Song", p.Title);
        Assert.Equal(1500, p.PositionMs);
        Assert.Equal("Playing", p.PlaybackState);
    }

    [Fact]
    public void ClearInject_And_PlaybackCommand_RoundTrip()
    {
        var clearJson = MediaLinkMessageSerializer.Serialize(
            MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypeMediaClearInject,
                new MediaLinkClearInjectPayload { Channels = ["media", "lyrics"] },
                id: "c1"));
        var clear = MediaLinkMessageSerializer.DeserializePayload<MediaLinkClearInjectPayload>(
            MediaLinkMessageSerializer.Deserialize(clearJson)!.Payload)!;
        Assert.Equal(new[] { "media", "lyrics" }, clear.Channels);

        var cmdJson = MediaLinkMessageSerializer.Serialize(
            MediaLinkMessageSerializer.Create(
                MediaLinkProtocol.TypePlaybackCommand,
                new MediaLinkPlaybackCommandPayload { Action = "pause" },
                id: "p1"));
        var cmd = MediaLinkMessageSerializer.DeserializePayload<MediaLinkPlaybackCommandPayload>(
            MediaLinkMessageSerializer.Deserialize(cmdJson)!.Payload)!;
        Assert.Equal("pause", cmd.Action);
    }

    [Fact]
    public void Protocol_Constants_Match_Wire_Strings()
    {
        Assert.Equal("ok", MediaLinkProtocol.TypeOk);
        Assert.Equal("media.inject", MediaLinkProtocol.TypeMediaInject);
        Assert.Equal("lyrics.inject", MediaLinkProtocol.TypeLyricsInject);
        Assert.Equal("media.clear_inject", MediaLinkProtocol.TypeMediaClearInject);
        Assert.Equal("playback.command", MediaLinkProtocol.TypePlaybackCommand);
        Assert.Equal("not_supported", MediaLinkProtocol.ErrorNotSupported);
        Assert.Equal("no_session", MediaLinkProtocol.ErrorNoSession);
    }
}
