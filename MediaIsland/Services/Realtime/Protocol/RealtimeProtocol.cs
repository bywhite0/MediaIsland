namespace MediaIsland.Services.Realtime.Protocol;

public static class RealtimeProtocol
{
    public const int Version = 1;
    public const string Path = "/v1/ws";

    public const string TypeEvent = "event";
    public const string TypeHello = "hello";
    public const string TypeAuth = "auth";
    public const string TypeAuthOk = "auth_ok";
    public const string TypeAuthFail = "auth_fail";
    public const string TypeSubscribe = "subscribe";
    public const string TypeSubscribeOk = "subscribe_ok";
    public const string TypePing = "ping";
    public const string TypePong = "pong";
    public const string TypeError = "error";

    public const string EventMediaUpdated = "media.updated";
    public const string EventLyricsUpdated = "lyrics.updated";
    public const string EventServerHello = "server.hello";

    public const string ChannelMedia = "media";
    public const string ChannelLyrics = "lyrics";

    public const string ErrorUnauthorized = "unauthorized";
    public const string ErrorBadRequest = "bad_request";
    public const string ErrorProtocolError = "protocol_error";
    public const string ErrorInternal = "internal";
    public const string ErrorRateLimited = "rate_limited";
    public const string ErrorNotImplemented = "not_implemented";

    public static readonly HashSet<string> KnownChannels = new(StringComparer.Ordinal)
    {
        ChannelMedia,
        ChannelLyrics
    };
}
