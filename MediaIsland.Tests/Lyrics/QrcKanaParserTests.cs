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
        // "111ざん" => (1, ""), (1, ""), (1, "ざん") over 高/橋/残; empty tokens still consume a base char.
        const string content = """
            [kana:111ざん]
            [0,1000]高(0,300)橋(300,300)残(600,400)
            """;

        var line = Assert.Single(QrcLyricsParser.Parse(content));
        Assert.Equal("高橋残", line.Text);
        var span = Assert.Single(line.RubySpans!);
        Assert.Equal(2, span.BaseStart);
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

    [Fact]
    public void Parse_DigitRunConsumesOneBaseCharacter()
    {
        // 無敵級*ビリーバー 的真实形态：「曲：DECO*27」的 27 占一个基字符（读音为空），
        // 旧实现漏算它，导致「编」之后的全部注音整体前移一位。
        const string content = """
            [kana:1きょく11へん1きょく1わら]
            [0,1000]曲(0,250)：(250,250)DECO*(500,250)27(750,250)
            [1000,1000]编(1000,250)曲(1250,250)：(1500,250)Rockwell(1750,250)
            [2000,1000]ほら笑(2000,500)って(2500,500)
            """;

        var lines = QrcLyricsParser.Parse(content);
        Assert.Equal(3, lines.Count);

        var credit = Assert.Single(lines[0].RubySpans!);
        Assert.Equal(0, credit.BaseStart);
        Assert.Equal("きょく", credit.Reading);

        Assert.Collection(
            lines[1].RubySpans!,
            span =>
            {
                Assert.Equal(0, span.BaseStart);
                Assert.Equal("へん", span.Reading);
            },
            span =>
            {
                Assert.Equal(1, span.BaseStart);
                Assert.Equal("きょく", span.Reading);
            });

        var lyric = Assert.Single(lines[2].RubySpans!);
        Assert.Equal(2, lyric.BaseStart);
        Assert.Equal("わら", lyric.Reading);
    }

    [Fact]
    public void Parse_AnnotatedDigitKeepsReadingOnTheDigitRun()
    {
        // 不思議と君とライブラリー：「午前0時」的 0 被注上「れい」。
        const string content = """
            [kana:1ご1ぜん1れい1じ]
            [0,1000]午前(0,500)0時(500,500)
            """;

        var line = Assert.Single(QrcLyricsParser.Parse(content));
        Assert.Equal("午前0時", line.Text);
        Assert.Collection(
            line.RubySpans!,
            span => Assert.Equal((0, 1, "ご"), (span.BaseStart, span.BaseLength, span.Reading)),
            span => Assert.Equal((1, 1, "ぜん"), (span.BaseStart, span.BaseLength, span.Reading)),
            span => Assert.Equal((2, 1, "れい"), (span.BaseStart, span.BaseLength, span.Reading)),
            span => Assert.Equal((3, 1, "じ"), (span.BaseStart, span.BaseLength, span.Reading)));
    }

    [Fact]
    public void Parse_MultiDigitRunIsCoveredAsOneBase()
    {
        const string content = """
            [kana:1にじゅうよ1じ1かん]
            [0,1000]24(0,500)時間(500,500)
            """;

        var line = Assert.Single(QrcLyricsParser.Parse(content));
        var first = line.RubySpans![0];
        Assert.Equal(0, first.BaseStart);
        Assert.Equal(2, first.BaseLength);
        Assert.Equal("にじゅうよ", first.Reading);
    }

    [Fact]
    public void Parse_CoverChecksumMismatch_DropsRubyInsteadOfMisaligning()
    {
        // 载荷少覆盖一个基字符：任何分配都会整体错位，宁可不显示。
        const string content = """
            [kana:1ざん1こく1てん]
            [0,1000]残(0,250)酷(250,250)な(500,250)天(750,125)使(875,125)
            """;

        var line = Assert.Single(QrcLyricsParser.Parse(content));
        Assert.Null(line.RubySpans);
    }

    [Fact]
    public void Parse_KatakanaKeIsNotABaseCharacter()
    {
        // 「虹ヶ咲」：ヶ 不占基字符位，实测自 未来ハーモニー 的 QRC。
        const string content = """
            [kana:1にじ1さき]
            [0,1000]虹ヶ咲(0,1000)
            """;

        var line = Assert.Single(QrcLyricsParser.Parse(content));
        Assert.Collection(
            line.RubySpans!,
            span => Assert.Equal((0, "にじ"), (span.BaseStart, span.Reading)),
            span => Assert.Equal((2, "さき"), (span.BaseStart, span.Reading)));
    }

    [Fact]
    public void Parse_IterationMarkIsABaseCharacter()
    {
        // 「日々」：々 占一个基字符位并单独注音，实测自 青春の輪郭 的 QRC。
        // 仅汉字+数字段的口径会少算它，靠 cover 校验和回退到含々的口径。
        const string content = """
            [kana:1ふ1あん1い1ひ1び]
            [0,1000]不(0,100)安(100,100)に(200,100)生(300,100)きる(400,200)日(600,100)々(700,100)を(800,200)
            """;

        var line = Assert.Single(QrcLyricsParser.Parse(content));
        Assert.Equal("不安に生きる日々を", line.Text);
        Assert.Collection(
            line.RubySpans!,
            span => Assert.Equal((0, "ふ"), (span.BaseStart, span.Reading)),
            span => Assert.Equal((1, "あん"), (span.BaseStart, span.Reading)),
            span => Assert.Equal((3, "い"), (span.BaseStart, span.Reading)),
            span => Assert.Equal((6, "ひ"), (span.BaseStart, span.Reading)),
            span => Assert.Equal((7, "び"), (span.BaseStart, span.Reading)));
    }
}
