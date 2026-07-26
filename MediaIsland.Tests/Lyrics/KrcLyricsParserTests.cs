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
}
