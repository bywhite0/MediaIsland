using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Lyrics.Parsers;
using MediaIsland.Services.Media;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsSearchServiceCleanupTests
{
    private const string Lrc = "[00:00.00]作词：青石\n[00:05.00]第一句\n[00:10.00]第二句";

    private static LyricsSourceSettings CreateSettings() => new()
    {
        Sources = [new LyricsSourceEntry { Id = LyricsSourceId.Netease, IsEnabled = true, UseWordSyncedLyrics = false }]
    };

    private static MediaInfo CreateMedia() => new(
        "Spotify.exe",
        "Song",
        "Artist",
        "Album",
        TimeSpan.Zero,
        TimeSpan.FromSeconds(180),
        new MediaPlaybackInfo(MediaPlaybackState.Playing),
        null,
        null);

    private static LyricsSearchService CreateService(LyricsSourceSettings settings) =>
        new([new LrcProvider(Lrc)], [new ManagedLyricsPayloadParser()], () => settings);

    [Fact]
    public async Task SearchAsync_DefaultSettings_StripsCreditLines()
    {
        var result = await CreateService(CreateSettings()).SearchAsync(CreateMedia());

        Assert.NotNull(result);
        Assert.Equal(["第一句", "第二句"], result!.Document.Lines.Select(l => l.Text));
    }

    [Fact]
    public async Task SearchAsync_StripDisabled_KeepsCreditLines()
    {
        var settings = CreateSettings();
        settings.StripCreditLines = false;

        var result = await CreateService(settings).SearchAsync(CreateMedia());

        Assert.Equal(3, result!.Document.Lines.Count);
    }

    [Fact]
    public async Task SearchAsync_RuleChange_TakesEffectOnlyAfterInvalidateCache()
    {
        var settings = CreateSettings();
        var service = CreateService(settings);
        await service.SearchAsync(CreateMedia());

        settings.MaskEnabled = true;
        settings.MaskWords = ["第一"];
        var cached = await service.SearchAsync(CreateMedia());
        service.InvalidateCache();
        var rebuilt = await service.SearchAsync(CreateMedia());

        // 规则不进设置指纹（否则改一次词表就让整库磁盘缓存失效），故内存缓存要靠 InvalidateCache 清掉。
        Assert.Equal("第一句", cached!.Document.Lines[0].Text);
        Assert.Equal("**句", rebuilt!.Document.Lines[0].Text);
    }

    [Fact]
    public void ComputeSettingsFingerprint_IgnoresCleanupRules()
    {
        var plain = CreateSettings();
        var withRules = CreateSettings();
        withRules.StripCreditLines = false;
        withRules.CreditKeywords = ["监修"];
        withRules.MaskEnabled = true;
        withRules.MaskWords = ["坏词"];

        Assert.Equal(
            LyricsSearchService.ComputeSettingsFingerprint(plain),
            LyricsSearchService.ComputeSettingsFingerprint(withRules));
    }

    [Fact]
    public async Task SearchAsync_WordSyncDowngrade_KeepsCleanup()
    {
        var settings = CreateSettings();
        settings.MaskEnabled = true;
        settings.MaskWords = ["第一"];
        var service = new LyricsSearchService(
            [new LrcProvider("[0,1000]作词：青石(0,1000)\n[5000,1000]第(5000,500)一句(5500,500)", LyricsFormat.Qrc)],
            [new ManagedLyricsPayloadParser()],
            () => settings);

        var result = await service.SearchAsync(CreateMedia());

        Assert.Equal(LyricsSyncMode.Line, result!.Document.SyncMode);
        Assert.DoesNotContain(result.Document.Lines, line => line.Text.Contains("作词"));
    }

    private sealed class LrcProvider(string content, LyricsFormat format = LyricsFormat.Lrc) : ILyricsProvider
    {
        public LyricsSourceId Id => LyricsSourceId.Netease;

        public Task<IReadOnlyList<LyricsCandidate>> SearchAsync(
            MediaInfo media,
            LyricsSourceSettings settings,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<LyricsCandidate> candidates =
            [
                new LyricsCandidate(Id, "item", media.Title ?? string.Empty, media.Artist ?? string.Empty,
                    media.AlbumTitle ?? string.Empty, media.Duration, 150, SupportsWordSync: false)
            ];
            return Task.FromResult(candidates);
        }

        public Task<LyricsPayload?> FetchAsync(
            LyricsCandidate candidate,
            LyricsSourceSettings settings,
            CancellationToken cancellationToken) =>
            Task.FromResult<LyricsPayload?>(new LyricsPayload(
                format,
                content,
                Id,
                candidate.ProviderItemId,
                new LyricsMetadata(candidate.Title, candidate.Artist, candidate.Album, candidate.Duration)));
    }
}
