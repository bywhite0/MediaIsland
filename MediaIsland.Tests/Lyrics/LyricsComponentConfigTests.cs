using System.Text.Json;
using MediaIsland.Components;
using MediaIsland.Services.Lyrics.Models;
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

    [Fact]
    public void DisplayPart_DefaultsToOriginalAndPersists()
    {
        var settings = new LyricsComponentConfig();
        Assert.Equal(LyricsDisplayPart.Original, settings.DisplayPart);
        Assert.Equal(0, settings.DisplayPartIndex);

        settings.DisplayPart = LyricsDisplayPart.Translation;
        Assert.Equal(1, settings.DisplayPartIndex);

        var json = JsonSerializer.Serialize(settings);
        Assert.Contains("\"DisplayPart\":1", json);
        Assert.DoesNotContain("DisplayPartIndex", json);

        var restored = JsonSerializer.Deserialize<LyricsComponentConfig>(json);
        Assert.NotNull(restored);
        Assert.Equal(LyricsDisplayPart.Translation, restored!.DisplayPart);
    }

    [Fact]
    public void IsShowQqMusicKana_DefaultsToFalseAndPersists()
    {
        var settings = new LyricsComponentConfig();
        Assert.False(settings.IsShowQqMusicKana);

        settings.IsShowQqMusicKana = true;
        var json = JsonSerializer.Serialize(settings);
        Assert.Contains("\"IsShowQqMusicKana\":true", json);

        var restored = JsonSerializer.Deserialize<LyricsComponentConfig>(json);
        Assert.NotNull(restored);
        Assert.True(restored!.IsShowQqMusicKana);
    }

    [Theory]
    [InlineData(-1, LyricsDisplayPart.Original)]
    [InlineData(0, LyricsDisplayPart.Original)]
    [InlineData(1, LyricsDisplayPart.Translation)]
    [InlineData(2, LyricsDisplayPart.Romanization)]
    [InlineData(99, LyricsDisplayPart.Original)]
    public void DisplayPartIndex_MapsToSupportedValues(int index, LyricsDisplayPart expected)
    {
        var settings = new LyricsComponentConfig
        {
            DisplayPartIndex = index
        };

        Assert.Equal(expected, settings.DisplayPart);
        Assert.Equal((int)expected, settings.DisplayPartIndex);
    }

    [Fact]
    public void LineSpacing_DefaultsToZeroAndPersists()
    {
        var settings = new LyricsComponentConfig();
        Assert.Equal(0, settings.LineSpacing);

        settings.LineSpacing = -2.5;
        var json = JsonSerializer.Serialize(settings);
        Assert.Contains("\"LineSpacing\":-2.5", json);

        var restored = JsonSerializer.Deserialize<LyricsComponentConfig>(json);
        Assert.NotNull(restored);
        Assert.Equal(-2.5, restored!.LineSpacing);
    }

    [Theory]
    [InlineData(-20, -8)]
    [InlineData(-8, -8)]
    [InlineData(0, 0)]
    [InlineData(3.5, 3.5)]
    [InlineData(8, 8)]
    [InlineData(20, 8)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void LineSpacing_ClampsToSupportedRange(double value, double expected)
    {
        var settings = new LyricsComponentConfig
        {
            LineSpacing = value
        };

        Assert.Equal(expected, settings.LineSpacing);
    }

    [Fact]
    public void LineSpacing_RaisesChangeNotification()
    {
        var settings = new LyricsComponentConfig();
        var changed = new List<string?>();
        settings.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        settings.LineSpacing = 2;
        settings.LineSpacing = 2;

        Assert.Equal([nameof(LyricsComponentConfig.LineSpacing)], changed);
    }
}
