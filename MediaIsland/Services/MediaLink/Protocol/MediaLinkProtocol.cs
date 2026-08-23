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

    /// <summary>
    /// 回程对时。请求与应答共用这一个类型名：应答带 <c>for</c>，与 thumbnail 一样
    /// 是「请求换一份数据」而非「请求换一个确认」，故不走 ok。
    /// </summary>
    public const string TypeAudioClock = "audio.clock";

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
    /// 跨机对时能力。取值与 <see cref="TypeAudioClock"/> 同字符串，与 audio 频道
    /// 同形——能力名即它启用的那个消息名，少一层需要两边同时记住的映射。
    /// </summary>
    public const string CapabilityAudioClock = "audio.clock";

    /// <summary>
    /// 播放延迟预算的默认值，毫秒。发送端在 <c>server.hello</c> 的 <c>audioClock.dMs</c>
    /// 里声明它，接收端一律照声明值执行。
    ///
    /// 它必须全局一致，否则对齐不可能——各接收端自己配一个不同的值就直接失败。
    /// 故这里是发送端的默认值，不是接收端的回落值：接收端读不到声明时退回非对齐模式，
    /// 而不是也取 300。两端各自默认成 300 看起来一致，但那是巧合，改了一端就静默失配。
    ///
    /// 300 毫秒的来由：它要盖住一次 TCP 重传加抖动缓冲的稳态深度，同时不至于让
    /// 「按下暂停到真的安静」有明显延迟。
    /// </summary>
    public const int AudioClockDefaultBudgetMs = 300;

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