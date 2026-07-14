using System.Text.Json;
using MediaIsland.Components;
using Xunit;

namespace MediaIsland.Tests;

public class LyricsComponentConfigTests
{
    [Fact]
    public void RenderFrameRate_DefaultsTo30Fps()
    {
        var settings = new LyricsComponentConfig();

        Assert.Equal(30, settings.RenderFrameRate);
        Assert.Equal(0, settings.RenderFrameRateIndex);
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(0, 30)]
    [InlineData(2, 30)]
    public void RenderFrameRateIndex_MapsToSupportedFrameRates(int index, int expectedFrameRate)
    {
        var settings = new LyricsComponentConfig
        {
            RenderFrameRateIndex = index
        };

        Assert.Equal(expectedFrameRate, settings.RenderFrameRate);
    }

    [Fact]
    public void RenderFrameRate_PersistsValueInsteadOfUiIndex()
    {
        var settings = new LyricsComponentConfig
        {
            RenderFrameRateIndex = 1
        };

        var json = JsonSerializer.Serialize(settings);

        Assert.Contains("\"RenderFrameRate\":60", json);
        Assert.DoesNotContain("RenderFrameRateIndex", json);
    }
}
