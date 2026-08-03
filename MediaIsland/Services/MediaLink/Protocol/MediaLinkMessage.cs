using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaIsland.Services.MediaLink.Protocol;

public sealed class MediaLinkMessage
{
    [JsonPropertyName("v")]
    public int V { get; set; } = MediaLinkProtocol.Version;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("ts")]
    public long Ts { get; set; }

    [JsonPropertyName("seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Seq { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Payload { get; set; }
}

public sealed class MediaLinkMediaDto
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

    [JsonPropertyName("trackToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TrackToken { get; set; }

    [JsonPropertyName("positionCapturedAtMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long PositionCapturedAtMs { get; set; }

    [JsonPropertyName("serverTimeMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ServerTimeMs { get; set; }
}

public sealed class MediaLinkLyricsDto
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

    [JsonPropertyName("trackToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TrackToken { get; set; }

    [JsonPropertyName("document")]
    public MediaLinkLyricsDocumentDto? Document { get; set; }
}

public sealed class MediaLinkLyricsDocumentDto
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
    public MediaLinkLyricsMetadataDto Metadata { get; set; } = new();

    [JsonPropertyName("lines")]
    public List<MediaLinkLyricsLineDto> Lines { get; set; } = [];
}

public sealed class MediaLinkLyricsMetadataDto
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

public sealed class MediaLinkLyricsLineDto
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
    public List<MediaLinkLyricsWordDto> Words { get; set; } = [];

    [JsonPropertyName("rubySpans")]
    public List<MediaLinkLyricsRubySpanDto> RubySpans { get; set; } = [];
}

public sealed class MediaLinkLyricsWordDto
{
    [JsonPropertyName("startMs")]
    public long StartMs { get; set; }

    [JsonPropertyName("endMs")]
    public long EndMs { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public sealed class MediaLinkLyricsRubySpanDto
{
    [JsonPropertyName("baseStart")]
    public int BaseStart { get; set; }

    [JsonPropertyName("baseLength")]
    public int BaseLength { get; set; }

    [JsonPropertyName("reading")]
    public string Reading { get; set; } = string.Empty;
}

public sealed class MediaLinkAuthPayload
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

public sealed class MediaLinkSubscribePayload
{
    [JsonPropertyName("channels")]
    public List<string> Channels { get; set; } = [];
}

public sealed class MediaLinkUnsubscribePayload
{
    [JsonPropertyName("channels")]
    public List<string> Channels { get; set; } = [];
}

public sealed class MediaLinkErrorPayload
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public sealed class MediaLinkServerHelloPayload
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = MediaLinkProtocol.Version;

    [JsonPropertyName("authRequired")]
    public bool AuthRequired { get; set; } = true;

    /// <summary>
    /// listener 实例代号，进程内单调递增。<c>seq</c> 在 listener 重建后从 0 重新开始，
    /// 客户端据此判断"seq 变小"是服务端重启而非乱序，从而重置已处理水位。
    /// </summary>
    [JsonPropertyName("sessionEpoch")]
    public long SessionEpoch { get; set; }
}

public sealed class MediaLinkOkPayload
{
    [JsonPropertyName("for")]
    public string For { get; set; } = string.Empty;
}

public sealed class MediaLinkMediaInjectPayload
{
    [JsonPropertyName("sourceApp")]
    public string? SourceApp { get; set; }

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
    public string? PlaybackState { get; set; }

    [JsonPropertyName("playbackRate")]
    public double? PlaybackRate { get; set; }
}

public sealed class MediaLinkClearInjectPayload
{
    [JsonPropertyName("channels")]
    public List<string>? Channels { get; set; }
}

public sealed class MediaLinkPlaybackCommandPayload
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }
}