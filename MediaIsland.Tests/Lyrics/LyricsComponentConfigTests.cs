using System.Text.Json;
using MediaIsland.Components;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

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

    [Fact]
    public void IsShowNoteIcon_DefaultsToTrueAndPersists()
    {
        var settings = new LyricsComponentConfig();
        Assert.True(settings.IsShowNoteIcon);

        settings.IsShowNoteIcon = false;
        var json = JsonSerializer.Serialize(settings);
        Assert.Contains("\"IsShowNoteIcon\":false", json);

        var restored = JsonSerializer.Deserialize<LyricsComponentConfig>(json);
        Assert.NotNull(restored);
        Assert.False(restored!.IsShowNoteIcon);
    }

    [Fact]
    public void NegativeMargins_DefaultOffAndPersist()
    {
        var settings = new LyricsComponentConfig();
        Assert.False(settings.IsLeftNegativeMargin);
        Assert.False(settings.IsRightNegativeMargin);

        settings.IsLeftNegativeMargin = true;
        settings.IsRightNegativeMargin = true;
        var json = JsonSerializer.Serialize(settings);
        Assert.Contains("\"IsLeftNegativeMargin\":true", json);
        Assert.Contains("\"IsRightNegativeMargin\":true", json);

        var restored = JsonSerializer.Deserialize<LyricsComponentConfig>(json);
        Assert.NotNull(restored);
        Assert.True(restored!.IsLeftNegativeMargin);
        Assert.True(restored.IsRightNegativeMargin);
    }
    [Fact]
    public void IsFixedWidthToMaxLineEnabled_DefaultsToFalseAndPersists()
    {
        var settings = new LyricsComponentConfig();
        Assert.False(settings.IsFixedWidthToMaxLineEnabled);

        settings.IsFixedWidthToMaxLineEnabled = true;
        var json = JsonSerializer.Serialize(settings);
        Assert.Contains("\"IsFixedWidthToMaxLineEnabled\":true", json);

        var restored = JsonSerializer.Deserialize<LyricsComponentConfig>(json);
        Assert.NotNull(restored);
        Assert.True(restored!.IsFixedWidthToMaxLineEnabled);
    }
}
