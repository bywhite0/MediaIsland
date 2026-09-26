using MediaIsland.Services.Lyrics.Cleanup;
using MediaIsland.Services.Lyrics.Models;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsTextCleanerStripTests
{
    private static readonly LyricsCleanupOptions Strip = new() { StripCredits = true };

    private static LyricsLine Line(int second, string text, bool background = false) =>
        new(TimeSpan.FromSeconds(second), TimeSpan.FromSeconds(second + 1), text, [], IsBackground: background);

    private static string[] Texts(IReadOnlyList<LyricsLine> lines) => lines.Select(line => line.Text).ToArray();

    [Theory]
    [InlineData("词：青石")]
    [InlineData("曲：林一")]
    [InlineData("作词：青石")]
    [InlineData("作曲 : 林一")]
    [InlineData("编曲：林一")]
    [InlineData("词/曲：青石")]
    [InlineData("作词&作曲：青石")]
    [InlineData("编曲Arranger：林一")]
    [InlineData("【作词：青石】")]
    [InlineData("(作曲：林一)")]
    [InlineData("作词-青石")]
    [InlineData("编曲（林一）")]
    [InlineData("作词")]
    [InlineData("制作人")]
    [InlineData("制作人：某人")]
    [InlineData("混音：某人")]
    [InlineData("OP：某公司")]
    [InlineData("Lyrics by: Aki")]
    [InlineData("Composed by：Aki")]
    [InlineData("Lyricist: Aki")]
    [InlineData("Producer: A")]
    [InlineData("Mixing Engineer: A")]
    [InlineData("未经许可，不得翻唱或使用")]
    [InlineData("（网易云音乐人）")]
    [InlineData("联系邮箱 test@example.com")]
    [InlineData("纯音乐，请欣赏")]
    public void StripCredits_RemovesCreditLine(string credit)
    {
        var result = LyricsTextCleaner.StripCredits([Line(0, credit), Line(1, "正文一句")], Strip);

        Assert.Equal(["正文一句"], Texts(result));
    }

    [Theory]
    [InlineData("你说：我们走吧")]
    [InlineData("我爱你：这是真的")]
    [InlineData("曲终人散")]
    [InlineData("作曲家的梦")]
    [InlineData("词")]
    [InlineData("曲")]
    [InlineData("Composed by Aki")]
    [InlineData("Vocal: A")]
    public void StripCredits_KeepsLyricLine(string lyric)
    {
        var result = LyricsTextCleaner.StripCredits([Line(0, lyric), Line(1, "正文一句")], Strip);

        Assert.Equal([lyric, "正文一句"], Texts(result));
    }

    [Fact]
    public void StripCredits_ScansWholeText_NotOnlyHeadAndTail()
    {
        var result = LyricsTextCleaner.StripCredits(
            [Line(0, "第一句"), Line(1, "和声：某人"), Line(2, "第二句")], Strip);

        Assert.Equal(["第一句", "第二句"], Texts(result));
    }

    [Fact]
    public void StripCredits_Disabled_ReturnsSameInstance()
    {
        IReadOnlyList<LyricsLine> lines = [Line(0, "作词：青石"), Line(1, "正文")];

        Assert.Same(lines, LyricsTextCleaner.StripCredits(lines, new LyricsCleanupOptions()));
    }

    [Fact]
    public void StripCredits_NothingToRemove_ReturnsSameInstance()
    {
        IReadOnlyList<LyricsLine> lines = [Line(0, "第一句"), Line(1, ""), Line(2, "第二句")];

        Assert.Same(lines, LyricsTextCleaner.StripCredits(lines, Strip));
    }

    [Fact]
    public void StripCredits_UserKeywordAndRegex_Apply()
    {
        var options = LyricsCleanupOptions.From(new LyricsSourceSettings
        {
            CreditKeywords = ["监修"],
            CreditRegexes = ["^特别鸣谢"]
        });

        var result = LyricsTextCleaner.StripCredits(
            [Line(0, "监修：某人"), Line(1, "特别鸣谢所有人"), Line(2, "正文")], options);

        Assert.Equal(["正文"], Texts(result));
    }

    [Fact]
    public void StripCredits_TitleArtistLine_RemovedWithinFirstFiveLines()
    {
        var options = Strip with { Title = "春风十里", Artists = ["鹿先森乐队"] };

        var result = LyricsTextCleaner.StripCredits(
            [Line(0, "春风十里 - 鹿先森乐队"), Line(1, "正文")], options);

        Assert.Equal(["正文"], Texts(result));
    }

    [Fact]
    public void StripCredits_TitleArtistLine_KeptAfterFifthContentLine()
    {
        var options = Strip with { Title = "春风十里", Artists = ["鹿先森乐队"] };
        LyricsLine[] lines =
        [
            Line(0, "一"), Line(1, "二"), Line(2, "三"), Line(3, "四"), Line(4, "五"),
            Line(5, "春风十里 - 鹿先森乐队")
        ];

        Assert.Equal(6, LyricsTextCleaner.StripCredits(lines, options).Count);
    }

    [Theory]
    [InlineData(null, "鹿先森乐队")]
    [InlineData("", "鹿先森乐队")]
    [InlineData("春风十里", null)]
    public void StripCredits_TitleArtistLine_KeptWhenTitleOrArtistMissing(string? title, string? artist)
    {
        var options = Strip with { Title = title, Artists = artist is null ? [] : [artist] };

        var result = LyricsTextCleaner.StripCredits([Line(0, "春风十里 - 鹿先森乐队"), Line(1, "正文")], options);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void StripCredits_TitleArtistLine_RequiresOnlyPunctuationBesidesNames()
    {
        var options = Strip with { Title = "爱", Artists = ["我"] };

        var result = LyricsTextCleaner.StripCredits([Line(0, "我爱你"), Line(1, "《爱》 — 我")], options);

        Assert.Equal(["我爱你"], Texts(result));
    }

    [Fact]
    public void StripCredits_BackgroundOfRemovedMain_IsRemovedToo()
    {
        // 背景行文本必须本身不命中规则（「(和声)」会被关键词直接删掉，证明不了联动）。
        var result = LyricsTextCleaner.StripCredits(
            [Line(0, "作词：青石"), Line(0, "(啊啊)", background: true), Line(1, "正文"), Line(1, "(啊)", background: true)],
            Strip);

        Assert.Equal(["正文", "(啊)"], Texts(result));
    }

    [Fact]
    public void StripCredits_BackgroundWithoutPrecedingMain_IsRemoved()
    {
        var result = LyricsTextCleaner.StripCredits(
            [Line(0, "(啊)", background: true), Line(1, "正文")], Strip);

        Assert.Equal(["正文"], Texts(result));
    }

    [Fact]
    public void StripCredits_TrimsLeadingAndTrailingBlankLines_KeepsInnerBlanks()
    {
        var result = LyricsTextCleaner.StripCredits(
            [Line(0, ""), Line(1, "作词：青石"), Line(2, ""), Line(3, "第一句"), Line(4, ""), Line(5, "第二句"), Line(6, ""), Line(7, "  ")],
            Strip);

        Assert.Equal(["第一句", "", "第二句"], Texts(result));
    }

    [Fact]
    public void StripCredits_AllLinesRemoved_ReturnsEmpty()
    {
        Assert.Empty(LyricsTextCleaner.StripCredits([Line(0, "作词：青石"), Line(1, ""), Line(2, "作曲：林一")], Strip));
    }

    [Fact]
    public void StripCredits_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(LyricsTextCleaner.StripCredits([], Strip));
    }

    [Fact]
    public void StripCredits_LoneSurrogateLine_DoesNotThrow()
    {
        var result = LyricsTextCleaner.StripCredits([Line(0, "\uD800作词：青石"), Line(1, "正文")], Strip);

        Assert.Contains("正文", Texts(result));
    }

    [Fact]
    public void StripCredits_IsIdempotent()
    {
        var options = Strip with { Title = "春风十里", Artists = ["鹿先森乐队"] };
        LyricsLine[] lines =
        [
            Line(0, ""), Line(1, "春风十里 - 鹿先森乐队"), Line(2, "作词：青石"), Line(3, "第一句"),
            Line(3, "(和声)", background: true), Line(4, ""), Line(5, "第二句"), Line(6, "未经许可不得翻唱")
        ];

        var once = LyricsTextCleaner.StripCredits(lines, options);
        var twice = LyricsTextCleaner.StripCredits(once, options);

        Assert.Equal(Texts(once), Texts(twice));
    }
}
