namespace MediaIsland.Services.MediaLink.Protocol;

public static class MediaLinkProtocol
{
    public const int Version = 1;
    public const string Path = "/v1/ws";
    public const string ThumbnailPath = "/v1/thumbnail";

    public const string TypeEvent = "event";
    public const string TypeHello = "hello";
    public const string TypeAuth = "auth";
    public const string TypeAuthOk = "auth_ok";
    public const string TypeAuthFail = "auth_fail";
    public const string TypeSubscribe = "subscribe";
    public const string TypeSubscribeOk = "subscribe_ok";
    public const string TypeUnsubscribe = "unsubscribe";
    public const string TypeUnsubscribeOk = "unsubscribe_ok";
    public const string TypePing = "ping";
    public const string TypePong = "pong";
    public const string TypeError = "error";
    public const string TypeOk = "ok";
    public const string TypeMediaInject = "media.inject";
    public const string TypeLyricsInject = "lyrics.inject";
    public const string TypeMediaClearInject = "media.clear_inject";
    public const string TypePlaybackCommand = "playback.command";
    public const string TypeThumbnailGet = "thumbnail.get";
    public const string TypeThumbnail = "thumbnail";
    public const string TypeAudioPlayStart = "audio.play_start";
    public const string TypeAudioPlayStop = "audio.play_stop";

    public const string EventMediaUpdated = "media.updated";
    public const string EventLyricsUpdated = "lyrics.updated";
    public const string EventServerHello = "server.hello";

    public const string ChannelMedia = "media";
    public const string ChannelLyrics = "lyrics";
    public const string ChannelAudio = "audio";

    public const string ErrorUnauthorized = "unauthorized";
    public const string ErrorBadRequest = "bad_request";
    public const string ErrorProtocolError = "protocol_error";
    public const string ErrorInternal = "internal";
    public const string ErrorRateLimited = "rate_limited";
    public const string ErrorNotImplemented = "not_implemented";
    public const string ErrorNotSupported = "not_supported";
    public const string ErrorNoSession = "no_session";

    /// <summary>server.hello 的 capabilities 取值。</summary>
    public const string CapabilityAudio = "audio";

    /// <summary>
    /// 音频线格式。显式声明而非双方硬编码约定——AMLL 的做法是把 48000/2/i16
    /// 写死在两端代码里，换采样率就要改协议。
    /// </summary>
    public const int AudioSampleRate = 48000;
    public const int AudioChannels = 2;
    public const string AudioFormat = "s16le";

    public static readonly HashSet<string> KnownChannels = new(StringComparer.Ordinal)
    {
        ChannelMedia,
        ChannelLyrics,
        ChannelAudio
    };
}