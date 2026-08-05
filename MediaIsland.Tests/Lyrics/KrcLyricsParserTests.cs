using System.Text;
using System.Text.Json;
using MediaIsland.Services.Lyrics.Parsers;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class KrcLyricsParserTests
{
    [Fact]
    public void Parse_TimedLine_ProducesWordsOffsetFromLineStart()
    {
        const string content = "[1000,1000]<0,500,0>Hello<500,500,0> world";

        var line = Assert.Single(KrcLyricsParser.Parse(content));

        Assert.Equal("Hello world", line.Text);
        Assert.Collection(
            line.Words,
            word =>
            {
                Assert.Equal("Hello", word.Text);
                Assert.Equal(TimeSpan.FromMilliseconds(1000), word.StartTime);
                Assert.Equal(TimeSpan.FromMilliseconds(1500), word.EndTime);
            },
            word =>
            {
                Assert.Equal(" world", word.Text);
                Assert.Equal(TimeSpan.FromMilliseconds(1500), word.StartTime);
                Assert.Equal(TimeSpan.FromMilliseconds(2000), word.EndTime);
            });
    }

    [Fact]
    public void Parse_LineTiming_SpansFirstAndLastWord()
    {
        const string content = "[1000,5000]<200,300,0>a<600,400,0>b";

        var line = Assert.Single(KrcLyricsParser.Parse(content));

        Assert.Equal(TimeSpan.FromMilliseconds(1200), line.StartTime);
        Assert.Equal(TimeSpan.FromMilliseconds(2000), line.EndTime);
    }

    [Fact]
    public void Parse_MetadataLines_AreIgnored()
    {
        const string content = """
            id:$00000000]
            [ar:Artist]
            [ti:Song]
            [offset:0]
            [language:eyJjb250ZW50IjpbXX0=]
            [1000,1000]<0,500,0>Hello<500,500,0> world
            """;

        var line = Assert.Single(KrcLyricsParser.Parse(content));

        Assert.Equal(2, line.Words.Count);
    }

    [Fact]
    public void Parse_MultipleLines_PreserveDocumentOrder()
    {
        const string content = """
            [0,900]<0,400,0>a<400,500,0>b
            [1000,900]<0,300,0>c<300,600,0>d
            """;

        var lines = KrcLyricsParser.Parse(content);

        Assert.Equal(2, lines.Count);
        Assert.Equal("ab", lines[0].Text);
        Assert.Equal("cd", lines[1].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), lines[1].StartTime);
    }

    [Fact]
    public void Parse_MultiByteText_IsPreserved()
    {
        const string content = "[500,1500]<0,700,0>晴天<700,800,0>娃娃";

        var line = Assert.Single(KrcLyricsParser.Parse(content));

        Assert.Equal("晴天娃娃", line.Text);
        Assert.Equal("晴天", line.Words[0].Text);
        Assert.Equal("娃娃", line.Words[1].Text);
    }

    [Fact]
    public void Parse_MalformedWordTiming_SkipsThatWordOnly()
    {
        const string content = "[0,1000]<bad,timing,0>skipped<0,500,0>kept";

        var line = Assert.Single(KrcLyricsParser.Parse(content));

        var word = Assert.Single(line.Words);
        Assert.Equal("kept", word.Text);
    }

    [Fact]
    public void Parse_CrlfInput_IsHandled()
    {
        const string content = "[0,900]<0,900,0>alpha\r\n[1000,900]<0,900,0>beta\r\n";

        var lines = KrcLyricsParser.Parse(content);

        Assert.Equal(2, lines.Count);
        Assert.Equal("alpha", lines[0].Text);
        Assert.Equal("beta", lines[1].Text);
    }

    [Theory]
    [InlineData("[1000,1000]no syllable markup")]
    [InlineData("[1000]missing duration")]
    [InlineData("[a,b]<0,500,0>bad line timing")]
    [InlineData("no leading bracket")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_UnusableContent_ReturnsEmpty(string? content)
    {
        Assert.Empty(KrcLyricsParser.Parse(content));
    }

    [Fact]
    public void Parse_LanguageTag_AttachesTranslationPerLine()
    {
        var content = BuildContentWithTags(
            """
            [0,900]<0,900,0>first
            [1000,900]<0,900,0>second
            """,
            LanguageTag(TranslationBlock(1, ["第一行", "第二行"])));

        var lines = KrcLyricsParser.Parse(content);

        Assert.Equal("第一行", lines[0].Translation);
        Assert.Equal("第二行", lines[1].Translation);
    }

    [Fact]
    public void Parse_TranslationPlaceholder_LeavesLineUntranslated()
    {
        var content = BuildContentWithTags(
            """
            [0,900]<0,900,0>first
            [1000,900]<0,900,0>second
            """,
            LanguageTag(TranslationBlock(1, ["//", "有翻译"])));

        var lines = KrcLyricsParser.Parse(content);

        Assert.Null(lines[0].Translation);
        Assert.Equal("有翻译", lines[1].Translation);
    }

    [Fact]
    public void Parse_TranslationShorterThanLyrics_LeavesRemainingLinesUntranslated()
    {
        var content = BuildContentWithTags(
            """
            [0,900]<0,900,0>first
            [1000,900]<0,900,0>second
            [2000,900]<0,900,0>third
            """,
            LanguageTag(TranslationBlock(1, ["只有一行"])));

        var lines = KrcLyricsParser.Parse(content);

        Assert.Equal("只有一行", lines[0].Translation);
        Assert.Null(lines[1].Translation);
        Assert.Null(lines[2].Translation);
    }

    [Fact]
    public void Parse_TimedLineWithoutWords_DoesNotShiftTranslationAlignment()
    {
        // The second timed line yields no words and is dropped, but it still consumes a
        // translation slot, so the third line must keep its own translation.
        var content = BuildContentWithTags(
            """
            [0,900]<0,900,0>first
            [1000,900]dropped
            [2000,900]<0,900,0>third
            """,
            LanguageTag(TranslationBlock(1, ["一", "二", "三"])));

        var lines = KrcLyricsParser.Parse(content);

        Assert.Equal(2, lines.Count);
        Assert.Equal("一", lines[0].Translation);
        Assert.Equal("三", lines[1].Translation);
    }

    [Fact]
    public void Parse_NonTranslationContentType_IsIgnored()
    {
        var content = BuildContentWithTags(
            "[0,900]<0,900,0>first",
            LanguageTag(TranslationBlock(2, ["not a translation"])));

        var line = Assert.Single(KrcLyricsParser.Parse(content));

        Assert.Null(line.Translation);
        Assert.Null(line.Romanization);
    }

    [Fact]
    public void Parse_RomanizationBlock_JoinsSyllableCellsIntoLine()
    {
        var content = BuildContentWithTags(
            """
            [0,900]<0,300,0>米<300,300,0>津<600,300,0>玄
            [1000,900]<0,900,0>春
            """,
            LanguageTag(RomanizationBlock([["yo ne ", "tsu ", "ge n "], ["ha ru "]])));

        var lines = KrcLyricsParser.Parse(content);

        Assert.Equal("yo ne tsu ge n", lines[0].Romanization);
        Assert.Equal("ha ru", lines[1].Romanization);
    }

    [Fact]
    public void Parse_TranslationAndRomanizationBlocks_BothAttach()
    {
        var content = BuildContentWithTags(
            "[0,900]<0,450,0>春<450,450,0>雨",
            LanguageTag(
                TranslationBlock(1, ["春雨"]),
                RomanizationBlock([["ha ru ", "sa me "]])));

        var line = Assert.Single(KrcLyricsParser.Parse(content));

        Assert.Equal("春雨", line.Translation);
        Assert.Equal("ha ru sa me", line.Romanization);
    }

    [Fact]
    public void Parse_RomanizationRowWithFewerCellsThanWords_StillJoinsAvailableCells()
    {
        var content = BuildContentWithTags(
            "[0,900]<0,300,0>a<300,300,0>b<600,300,0>c",
            LanguageTag(RomanizationBlock([["x ", "y "]])));

        var line = Assert.Single(KrcLyricsParser.Parse(content));

        Assert.Equal("x y", line.Romanization);
    }

    [Fact]
    public void Parse_EmptyRomanizationRow_LeavesLineWithoutRomanization()
    {
        var content = BuildContentWithTags(
            """
            [0,900]<0,900,0>first
            [1000,900]<0,900,0>second
            """,
            LanguageTag(RomanizationBlock([[], ["ni "]])));

        var lines = KrcLyricsParser.Parse(content);

        Assert.Null(lines[0].Romanization);
        Assert.Equal("ni", lines[1].Romanization);
    }

    [Fact]
    public void Parse_TimedLineWithoutWords_DoesNotShiftRomanizationAlignment()
    {
        var content = BuildContentWithTags(
            """
            [0,900]<0,900,0>first
            [1000,900]dropped
            [2000,900]<0,900,0>third
            """,
            LanguageTag(RomanizationBlock([["i chi "], ["ni "], ["sa n "]])));

        var lines = KrcLyricsParser.Parse(content);

        Assert.Equal(2, lines.Count);
        Assert.Equal("i chi", lines[0].Romanization);
        Assert.Equal("sa n", lines[1].Romanization);
    }

    [Theory]
    [InlineData("[language:not-base64]")]
    [InlineData("[language:]")]
    [InlineData("[language:eyJib2d1cyI6dHJ1ZX0=]")]         // {"bogus":true}
    [InlineData("[language:eyJjb250ZW50IjpbXX0=]")]         // {"content":[]}
    [InlineData("[language:AAAA")]                          // unterminated tag
    public void Parse_MalformedLanguageTag_FallsBackToNoSecondaryText(string languageTag)
    {
        var content = $"{languageTag}\n[0,900]<0,900,0>first";

        var line = Assert.Single(KrcLyricsParser.Parse(content));

        Assert.Equal("first", line.Text);
        Assert.Null(line.Translation);
        Assert.Null(line.Romanization);
    }

    [Fact]
    public void Parse_Kana_AttachesRubySpansToMatchingKanji()
    {
        const string content = """
            [kana:1ざん1こく]
            [0,1000]<0,500,0>残<500,500,0>酷
            """;

        var line = Assert.Single(KrcLyricsParser.Parse(content));

        Assert.Equal("残酷", line.Text);
        Assert.Collection(
            line.RubySpans!,
            span =>
            {
                Assert.Equal(0, span.BaseStart);
                Assert.Equal(1, span.BaseLength);
                Assert.Equal("ざん", span.Reading);
            },
            span =>
            {
                Assert.Equal(1, span.BaseStart);
                Assert.Equal(1, span.BaseLength);
                Assert.Equal("こく", span.Reading);
            });
    }

    [Fact]
    public void Parse_Kana_StripsEmbeddedTimingFromReading()
    {
        const string content = """
            [kana:1み(100,50)つ]
            [0,1000]<0,1000,0>密
            """;

        var line = Assert.Single(KrcLyricsParser.Parse(content));
        var span = Assert.Single(line.RubySpans!);
        Assert.Equal("みつ", span.Reading);
    }

    [Fact]
    public void Parse_Kana_SkipsAsciiAndKanaWhenAligning()
    {
        const string content = """
            [kana:1む1てき]
            [0,1000]<0,250,0>無<250,250,0>敵<500,250,0>の<750,250,0>A
            """;

        var line = Assert.Single(KrcLyricsParser.Parse(content));
        Assert.Equal("無敵のA", line.Text);
        Assert.Collection(
            line.RubySpans!,
            span =>
            {
                Assert.Equal(0, span.BaseStart);
                Assert.Equal("む", span.Reading);
            },
            span =>
            {
                Assert.Equal(1, span.BaseStart);
                Assert.Equal("てき", span.Reading);
            });
    }

    [Fact]
    public void Parse_Kana_MultiCharCover_SpansContiguousKanji()
    {
        const string content = """
            [kana:2きょう]
            [0,1000]<0,500,0>今<500,500,0>日
            """;

        var line = Assert.Single(KrcLyricsParser.Parse(content));
        var span = Assert.Single(line.RubySpans!);
        Assert.Equal(0, span.BaseStart);
        Assert.Equal(2, span.BaseLength);
        Assert.Equal("きょう", span.Reading);
    }

    [Fact]
    public void Parse_WithoutKana_LeavesRubyNull_AndKeepsTranslation()
    {
        var content = BuildContentWithTags(
            "[0,900]<0,900,0>残酷",
            LanguageTag(TranslationBlock(1, ["cruel"])));

        var line = Assert.Single(KrcLyricsParser.Parse(content));

        Assert.Equal("残酷", line.Text);
        Assert.Equal("cruel", line.Translation);
        Assert.Null(line.RubySpans);
    }

    [Fact]
    public void Parse_KanaAndLanguage_Coexist()
    {
        var language = LanguageTag(
            TranslationBlock(1, ["cruel"]),
            RomanizationBlock([["zan ", "koku"]]));
        var content = $"""
            [kana:1ざん1こく]
            {language}
            [0,1000]<0,500,0>残<500,500,0>酷
            """;

        var line = Assert.Single(KrcLyricsParser.Parse(content));

        Assert.Equal("残酷", line.Text);
        Assert.Equal("cruel", line.Translation);
        Assert.Equal("zan koku", line.Romanization);
        Assert.Collection(
            line.RubySpans!,
            span => Assert.Equal("ざん", span.Reading),
            span => Assert.Equal("こく", span.Reading));
    }

    [Fact]
    public void Parse_Kana_EmptyReading_DoesNotCreateSpanButConsumesKanji()
    {
        const string content = """
            [kana:111ざん]
            [0,1000]<0,300,0>高<300,300,0>橋<600,400,0>残
            """;

        var line = Assert.Single(KrcLyricsParser.Parse(content));
        Assert.Equal("高橋残", line.Text);
        var span = Assert.Single(line.RubySpans!);
        Assert.Equal(2, span.BaseStart);
        Assert.Equal(1, span.BaseLength);
        Assert.Equal("ざん", span.Reading);
    }

    private static string BuildContentWithTags(string lyrics, string languageTag) =>
        $"[id:$00000000]\n[ar:Artist]\n{languageTag}\n{lyrics}";

    /// <summary>Builds the <c>[language:...]</c> tag the way Kugou ships it.</summary>
    private static string LanguageTag(params string[] blocks) =>
        "[language:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $$"""{"content":[{{string.Join(",", blocks)}}],"version":1}""")) + "]";

    /// <summary>A translation block stores the whole line in the first cell of each row.</summary>
    private static string TranslationBlock(int type, string[] translations) =>
        Block(type, translations.Select(text => new[] { text }).ToArray());

    /// <summary>A romanization block stores one cell per KRC syllable.</summary>
    private static string RomanizationBlock(string[][] rows) => Block(0, rows);

    private static string Block(int type, string[][] rows)
    {
        var content = string.Join(",", rows.Select(row =>
            $"[{string.Join(",", row.Select(cell => JsonSerializer.Serialize<string>(cell)))}]"));
        return $$"""{"language":0,"type":{{type}},"lyricContent":[{{content}}]}""";
    }
}
