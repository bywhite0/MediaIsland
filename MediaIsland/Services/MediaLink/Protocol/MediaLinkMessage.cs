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

    /// <summary>
    /// 服务端支持的可选能力。老客户端忽略未知字段；新客户端据此决定是否订阅 audio，
    /// 避免向不支持的服务端发订阅请求换来 bad_request。
    /// </summary>
    [JsonPropertyName("capabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Capabilities { get; set; }

    [JsonPropertyName("audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MediaLinkAudioFormatPayload? Audio { get; set; }
}

/// <summary>音频线格式声明。取值恒为协议常量，作为字段传出使其可被客户端读取而非猜测。</summary>
public sealed class MediaLinkAudioFormatPayload
{
    [JsonPropertyName("sampleRate")]
    public int SampleRate { get; set; } = MediaLinkProtocol.AudioSampleRate;

    [JsonPropertyName("channels")]
    public int Channels { get; set; } = MediaLinkProtocol.AudioChannels;

    [JsonPropertyName("format")]
    public string Format { get; set; } = MediaLinkProtocol.AudioFormat;
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

    /// <summary>
    /// <paramref name="PositionMs"/> 的采样距今已过去多少毫秒。省略或 0 表示"就是当前值"。
    ///
    /// 用相对量而非绝对时间戳（如发送方的 Unix 毫秒）是刻意的：绝对值要求两台机器
    /// 时钟同步，而相对值只依赖发送方自己的时钟差，跨机转发时无需对时。
    ///
    /// 转发场景下这个字段是必需的：上游 <c>media.updated</c> 的位置在传输与处理期间
    /// 已经继续前进，不回填这段时间的话每转发一跳都会让进度落后一次。
    /// </summary>
    [JsonPropertyName("positionAgeMs")]
    public long? PositionAgeMs { get; set; }
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

public sealed class MediaLinkThumbnailGetPayload
{
    /// <summary>
    /// 期望的曲目标识。与当前有效媒体不一致时服务端返回空封面，
    /// 避免客户端把上一首的封面配到新曲目上。省略则不做校验。
    /// </summary>
    [JsonPropertyName("trackToken")]
    public string? TrackToken { get; set; }
}

public sealed class MediaLinkThumbnailPayload
{
    /// <summary>本次封面所属曲目的标识；无媒体时为 null。</summary>
    [JsonPropertyName("trackToken")]
    public string? TrackToken { get; set; }

    /// <summary>有封面数据时为 MIME 类型，否则为 null。</summary>
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    /// <summary>
    /// base64 编码的图片数据。无封面、封面过大或 trackToken 失配时为 null——
    /// 这三种情况都不是错误，客户端按"当前无可用封面"处理即可。
    /// </summary>
    [JsonPropertyName("dataBase64")]
    public string? DataBase64 { get; set; }
}

/// <summary>
/// audio.clock 请求。客户端只带 t1，其余三个时刻由服务端与客户端各自补齐。
/// </summary>
public sealed class MediaLinkAudioClockRequestPayload
{
    /// <summary>
    /// 客户端发出时刻，单位 100ns，取自客户端单调时钟。
    ///
    /// 必须是单调时钟而非墙钟：offset 要跨分钟级持续维持，而墙钟会被 NTP
    /// slew 或 step 调整，一次调整就让此前所有样本失效且无从察觉。
    /// 信封的 ts 是 Unix 毫秒墙钟，与本字段不是同一个时钟，不可互换。
    /// </summary>
    [JsonPropertyName("t1")]
    public long? T1 { get; set; }
}

/// <summary>
/// audio.clock 应答。四个时刻缺一不可。
///
/// 只有 t1 / t3 / t4 就必须假设服务端处理耗时为零，而服务端一次 GC 或锁竞争
/// 就是几十毫秒，那段时间会被算进网络往返，同时污染 offset 与它的可信度估计。
/// ping / pong 只够三个时刻，这也是本消息不复用 ping 的原因。
///
/// 三个字段都可空，是为了让「对端没实现」与「对端报了 0」可区分：
/// 缺字段时客户端按不支持处理，而不是退化成三时间戳估计——那会给出一个
/// 看起来可用的错值，比报不可用坏得多。
/// </summary>
public sealed class MediaLinkAudioClockPayload
{
    [JsonPropertyName("for")]
    public string? For { get; set; }

    /// <summary>
    /// 回显请求里的 t1。客户端因此不必维护 id 到 t1 的映射，
    /// 且迟到或重复的应答无法与错误的 t1 配成一个样本。
    /// </summary>
    [JsonPropertyName("t1")]
    public long? T1 { get; set; }

    /// <summary>服务端收到请求的时刻，取在解析 JSON 之前。</summary>
    [JsonPropertyName("t2")]
    public long? T2 { get; set; }

    /// <summary>服务端发出应答的时刻，取在拿到发送权之后。</summary>
    [JsonPropertyName("t3")]
    public long? T3 { get; set; }
}
