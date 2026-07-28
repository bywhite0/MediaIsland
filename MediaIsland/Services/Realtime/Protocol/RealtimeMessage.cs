using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaIsland.Services.Realtime.Protocol;

public sealed class RealtimeMessage
{
    [JsonPropertyName("v")]
    public int V { get; set; } = RealtimeProtocol.Version;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("ts")]
    public long Ts { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Payload { get; set; }
}

public sealed class RealtimeMediaDto
{
    [JsonPropertyName("changeKind")]
    public string ChangeKind { get; set; } = string.Empty;

    [JsonPropertyName("sourceApp")]
    public string SourceApp { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    [JsonPropertyName("albumTitle")]
    public string? AlbumTitle { get; set; }

    [JsonPropertyName("positionMs")]
    public long PositionMs { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("playbackState")]
    public string PlaybackState { get; set; } = string.Empty;

    [JsonPropertyName("playbackRate")]
    public double? PlaybackRate { get; set; }

    [JsonPropertyName("hasThumbnail")]
    public bool HasThumbnail { get; set; }
}

public sealed class RealtimeLyricsDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("artist")]
    public string Artist { get; set; } = string.Empty;

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("score")]
    public int? Score { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("document")]
    public RealtimeLyricsDocumentDto? Document { get; set; }
}

public sealed class RealtimeLyricsDocumentDto
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("syncMode")]
    public string SyncMode { get; set; } = string.Empty;

    [JsonPropertyName("providerItemId")]
    public string ProviderItemId { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("metadata")]
    public RealtimeLyricsMetadataDto Metadata { get; set; } = new();

    [JsonPropertyName("lines")]
    public List<RealtimeLyricsLineDto> Lines { get; set; } = [];
}

public sealed class RealtimeLyricsMetadataDto
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    [JsonPropertyName("album")]
    public string? Album { get; set; }

    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; set; }
}

public sealed class RealtimeLyricsLineDto
{
    [JsonPropertyName("startMs")]
    public long StartMs { get; set; }

    [JsonPropertyName("endMs")]
    public long EndMs { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("translation")]
    public string? Translation { get; set; }

    [JsonPropertyName("romanization")]
    public string? Romanization { get; set; }

    [JsonPropertyName("isBackground")]
    public bool IsBackground { get; set; }

    [JsonPropertyName("isDuet")]
    public bool IsDuet { get; set; }

    [JsonPropertyName("words")]
    public List<RealtimeLyricsWordDto> Words { get; set; } = [];

    [JsonPropertyName("rubySpans")]
    public List<RealtimeLyricsRubySpanDto> RubySpans { get; set; } = [];
}

public sealed class RealtimeLyricsWordDto
{
    [JsonPropertyName("startMs")]
    public long StartMs { get; set; }

    [JsonPropertyName("endMs")]
    public long EndMs { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public sealed class RealtimeLyricsRubySpanDto
{
    [JsonPropertyName("baseStart")]
    public int BaseStart { get; set; }

    [JsonPropertyName("baseLength")]
    public int BaseLength { get; set; }

    [JsonPropertyName("reading")]
    public string Reading { get; set; } = string.Empty;
}

public sealed class RealtimeAuthPayload
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

public sealed class RealtimeSubscribePayload
{
    [JsonPropertyName("channels")]
    public List<string> Channels { get; set; } = [];
}

public sealed class RealtimeErrorPayload
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public sealed class RealtimeServerHelloPayload
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = RealtimeProtocol.Version;

    [JsonPropertyName("authRequired")]
    public bool AuthRequired { get; set; } = true;

    [JsonPropertyName("certFingerprintShort")]
    public string? CertFingerprintShort { get; set; }
}
