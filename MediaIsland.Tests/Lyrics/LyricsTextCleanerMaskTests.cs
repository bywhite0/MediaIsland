using MediaIsland.Services.Lyrics.Cleanup;
using MediaIsland.Services.Lyrics.Models;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsTextCleanerMaskTests
{
    private static LyricsCleanupOptions MaskOf(params string[] words) =>
        new() { MaskEnabled = true, MaskWords = words };

    private static LyricsLine Line(string text, IReadOnlyList<LyricsWord>? words = null) =>
        new(TimeSpan.Zero, TimeSpan.FromSeconds(1), text, words ?? []);

    private static LyricsWord Word(int ms, string text) =>
        new(TimeSpan.FromMilliseconds(ms), TimeSpan.FromMilliseconds(ms + 100), text);

    private static string Describe(LyricsLine line) =>
        $"{line.Text}|{string.Join(",", line.Words.Select(w => $"{w.StartTime.TotalMilliseconds}:{w.Text}"))}|{line.Translation}|{line.Romanization}|" +
        string.Join(",", (line.RubySpans ?? []).Select(r => $"{r.BaseStart}+{r.BaseLength}={r.Reading}"));

    [Fact]
    public void Mask_IsCaseInsensitive_AndKeepsLength()
    {
        var result = LyricsTextCleaner.Mask([Line("Oh SHIT again")], MaskOf("shit"));

        Assert.Equal("Oh **** again", result[0].Text);
    }

    [Theory]
    [InlineData("class starts", "class starts")]
    [InlineData("ass!", "***!")]
    [InlineData("an ass.", "an ***.")]
    [InlineData("ass你好", "***你好")]
    public void Mask_LatinWord_RequiresAsciiBoundary(string text, string expected)
    {
        Assert.Equal(expected, LyricsTextCleaner.Mask([Line(text)], MaskOf("ass"))[0].Text);
    }

    [Fact]
    public void Mask_CjkWord_MatchesAsSubstring()
    {
        Assert.Equal("他**真", LyricsTextCleaner.Mask([Line("他妈的真")], MaskOf("妈的"))[0].Text);
    }

    [Fact]
    public void Mask_UserRegex_Applies()
    {
        var options = new LyricsCleanupOptions { MaskEnabled = true, MaskRegexes = [LyricsCleanupOptions.TryCompile("b[a4]d")!] };

        Assert.Equal("so *** so ***", LyricsTextCleaner.Mask([Line("so bad so B4D")], options)[0].Text);
    }

    [Fact]
    public void Mask_ProjectsAcrossWordBoundaries_KeepingTimes()
    {
        var line = Line("fuck you", [Word(0, "fu"), Word(100, "ck "), Word(200, "you")]);

        var masked = LyricsTextCleaner.Mask([line], MaskOf("fuck"))[0];

        Assert.Equal("**** you", masked.Text);
        Assert.Equal(["**", "** ", "you"], masked.Words.Select(w => w.Text));
        Assert.Equal([0d, 100d, 200d], masked.Words.Select(w => w.StartTime.TotalMilliseconds));
    }

    [Fact]
    public void Mask_WordsDisagreeWithText_MasksEachWordIndependently()
    {
        var line = Line("我 fuck 了", [Word(0, "我"), Word(100, "fuck"), Word(200, "了")]);

        var masked = LyricsTextCleaner.Mask([line], MaskOf("fuck"))[0];

        Assert.Equal("我 **** 了", masked.Text);
        Assert.Equal(["我", "****", "了"], masked.Words.Select(w => w.Text));
    }

    [Fact]
    public void Mask_TranslationAndRomanization_AreMasked()
    {
        var line = Line("正文") with { Translation = "damn it", Romanization = "DAMN" };

        var masked = LyricsTextCleaner.Mask([line], MaskOf("damn"))[0];

        Assert.Equal("**** it", masked.Translation);
        Assert.Equal("****", masked.Romanization);
    }

    [Fact]
    public void Mask_DropsRubyIntersectingMaskedRange_KeepsOthers()
    {
        var line = Line("漢字です") with
        {
            RubySpans = [new LyricsRubySpan(0, 1, "かん"), new LyricsRubySpan(1, 1, "じ")]
        };

        var masked = LyricsTextCleaner.Mask([line], MaskOf("字"))[0];

        Assert.Equal("漢*です", masked.Text);
        Assert.Equal(["かん"], masked.RubySpans!.Select(r => r.Reading));
    }

    [Fact]
    public void Mask_Disabled_ReturnsSameInstance()
    {
        IReadOnlyList<LyricsLine> lines = [Line("shit")];

        Assert.Same(lines, LyricsTextCleaner.Mask(lines, new LyricsCleanupOptions { MaskWords = ["shit"] }));
    }

    [Fact]
    public void Mask_EnabledWithoutRules_ReturnsSameInstance()
    {
        IReadOnlyList<LyricsLine> lines = [Line("shit")];

        Assert.Same(lines, LyricsTextCleaner.Mask(lines, new LyricsCleanupOptions { MaskEnabled = true }));
    }

    [Fact]
    public void Mask_NoHit_ReturnsSameInstance()
    {
        IReadOnlyList<LyricsLine> lines = [Line("clean")];

        Assert.Same(lines, LyricsTextCleaner.Mask(lines, MaskOf("shit")));
    }

    [Fact]
    public void Apply_Null_ReturnsSameInstance()
    {
        IReadOnlyList<LyricsLine> lines = [Line("作词：青石")];

        Assert.Same(lines, LyricsTextCleaner.Apply(lines, null));
    }

    [Fact]
    public void Apply_StripsThenMasks_MaskDoesNotAffectCreditDetection()
    {
        var options = new LyricsCleanupOptions { StripCredits = true, MaskEnabled = true, MaskWords = ["作词", "damn"] };

        var result = LyricsTextCleaner.Apply([Line("作词：青石"), Line("damn 作词")], options);

        Assert.Equal(["**** **"], result.Select(line => line.Text));
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var options = new LyricsCleanupOptions
        {
            StripCredits = true,
            MaskEnabled = true,
            MaskWords = ["fuck", "字"],
            MaskRegexes = [LyricsCleanupOptions.TryCompile("b[a4]d")!]
        };
        LyricsLine[] lines =
        [
            Line("作词：青石"),
            Line("fuck you", [Word(0, "fu"), Word(100, "ck "), Word(200, "you")]) with { Translation = "bad" },
            Line("漢字") with { RubySpans = [new LyricsRubySpan(1, 1, "じ")] }
        ];

        var once = LyricsTextCleaner.Apply(lines, options);
        var twice = LyricsTextCleaner.Apply(once, options);

        Assert.Equal(once.Select(Describe), twice.Select(Describe));
    }
    [Fact]
    public void Mask_EmptyWord_IsIgnored()
    {
        var result = LyricsTextCleaner.Mask([Line("oh shit")], MaskOf("", "shit"));

        Assert.Equal("oh ****", result[0].Text);
    }

    [Fact]
    public void Mask_TimedOutRegex_IsSkippedForRestOfCall()
    {
        var text = CatastrophicRegex.Input;
        var options = new LyricsCleanupOptions { MaskEnabled = true, MaskRegexes = [CatastrophicRegex.Create()] };
        var lines = Enumerable.Range(0, 50).Select(_ => Line(text) with { Translation = text, Romanization = text }).ToArray();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = LyricsTextCleaner.Mask(lines, options);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < LyricsCleanupOptions.RegexTimeout * 10, $"耗时 {stopwatch.Elapsed}");
        Assert.All(result, line =>
        {
            Assert.Equal(text, line.Text);
            Assert.Equal(text, line.Translation);
            Assert.Equal(text, line.Romanization);
        });
    }
}
