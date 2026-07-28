using MediaIsland.Services.Lyrics.Parsers;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class QrcKanaParserTests
{
    [Fact]
    public void Parse_AttachesRubySpansToMatchingKanji()
    {
        const string content = """
            [kana:1ざん1こく1てん1し]
            [0,1000]残(0,250)酷(250,250)な(500,250)天(750,125)使(875,125)
            """;

        var line = Assert.Single(QrcLyricsParser.Parse(content));

        Assert.Equal("残酷な天使", line.Text);
        Assert.NotNull(line.RubySpans);
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
            },
            span =>
            {
                Assert.Equal(3, span.BaseStart);
                Assert.Equal(1, span.BaseLength);
                Assert.Equal("てん", span.Reading);
            },
            span =>
            {
                Assert.Equal(4, span.BaseStart);
                Assert.Equal(1, span.BaseLength);
                Assert.Equal("し", span.Reading);
            });
    }

    [Fact]
    public void Parse_MultiCharCover_SpansContiguousKanji()
    {
        const string content = """
            [kana:2きょう]
            [0,1000]今(0,500)日(500,500)
            """;

        var line = Assert.Single(QrcLyricsParser.Parse(content));
        var span = Assert.Single(line.RubySpans!);
        Assert.Equal(0, span.BaseStart);
        Assert.Equal(2, span.BaseLength);
        Assert.Equal("きょう", span.Reading);
    }

    [Fact]
    public void Parse_StripsEmbeddedTimingFromReading()
    {
        const string content = """
            [kana:1み(4304,88)つ(4392,136)]
            [0,1000]密(0,1000)
            """;

        var line = Assert.Single(QrcLyricsParser.Parse(content));
        var span = Assert.Single(line.RubySpans!);
        Assert.Equal("みつ", span.Reading);
    }

    [Fact]
    public void Parse_EmptyReading_DoesNotCreateSpanButConsumesKanji()
    {
        // "11ざん" => (1, ""), (1, "ざん") over 高/橋/残; empty first token is skipped.
        const string content = """
            [kana:11ざん]
            [0,1000]高(0,300)橋(300,300)残(600,400)
            """;

        var line = Assert.Single(QrcLyricsParser.Parse(content));
        Assert.Equal("高橋残", line.Text);
        var span = Assert.Single(line.RubySpans!);
        Assert.Equal(1, span.BaseStart);
        Assert.Equal(1, span.BaseLength);
        Assert.Equal("ざん", span.Reading);
    }

    [Fact]
    public void Parse_WithoutKana_LeavesRubyNull()
    {
        const string content = "[0,1000]Hello(0,500) world(500,500)";
        var line = Assert.Single(QrcLyricsParser.Parse(content));
        Assert.Null(line.RubySpans);
    }

    [Fact]
    public void Parse_SkipsAsciiAndKanaWhenAligning()
    {
        const string content = """
            [kana:1む1てき]
            [0,1000]無(0,250)敵(250,250)の(500,250)A(750,250)
            """;

        var line = Assert.Single(QrcLyricsParser.Parse(content));
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
    public void ParseTokens_AcceptsZeroCoverWithoutConsuming()
    {
        var tokens = LyricsKanaRubyParser.ParseTokens("01あ");
        Assert.Collection(
            tokens,
            token =>
            {
                Assert.Equal(0, token.Cover);
                Assert.Equal(string.Empty, token.Reading);
            },
            token =>
            {
                Assert.Equal(1, token.Cover);
                Assert.Equal("あ", token.Reading);
            });
    }
}
