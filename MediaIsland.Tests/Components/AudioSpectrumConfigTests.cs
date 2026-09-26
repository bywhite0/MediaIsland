using MediaIsland.Components;
using Xunit;

namespace MediaIsland.Tests.Components;

public class AudioSpectrumConfigTests
{
    [Fact]
    public void Defaults_MatchTheSpec()
    {
        var config = new AudioSpectrumConfig();

        Assert.Equal(SpectrumStyle.Capsule, config.Style);
        Assert.Equal(96, config.Width);
        Assert.Equal(7, config.BandCount);
    }

    [Fact]
    public void BandCount_IsStoredPerStyle()
    {
        var config = new AudioSpectrumConfig { BandCount = 9 };

        config.Style = SpectrumStyle.PeakCap;
        Assert.Equal(16, config.BandCount);
        config.BandCount = 24;

        config.Style = SpectrumStyle.Ridge;
        Assert.Equal(12, config.BandCount);

        config.Style = SpectrumStyle.Capsule;
        Assert.Equal(9, config.BandCount);
        config.Style = SpectrumStyle.PeakCap;
        Assert.Equal(24, config.BandCount);
    }

    [Fact]
    public void StyleChange_RaisesBandCountChanged()
    {
        var config = new AudioSpectrumConfig();
        var raised = new List<string?>();
        config.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        config.Style = SpectrumStyle.PeakCap;

        Assert.Contains(nameof(AudioSpectrumConfig.BandCount), raised);
    }

    [Fact]
    public void BandCount_ClampsToNamedBounds()
    {
        var config = new AudioSpectrumConfig { BandCount = 0 };
        Assert.Equal(AudioSpectrumConfig.MinBandCount, config.BandCount);

        config.BandCount = 999;
        Assert.Equal(AudioSpectrumConfig.MaxBandCount, config.BandCount);
    }

    [Fact]
    public void BarGap_ClampsToNamedBounds()
    {
        var config = new AudioSpectrumConfig { BarGap = -5 };
        Assert.Equal(AudioSpectrumConfig.MinBarGap, config.BarGap);

        config.BarGap = 999;
        Assert.Equal(AudioSpectrumConfig.MaxBarGap, config.BarGap);
    }

    [Theory]
    [InlineData(SpectrumStyle.Capsule, true, true, false)]
    [InlineData(SpectrumStyle.PeakCap, true, true, true)]
    [InlineData(SpectrumStyle.Ridge, true, false, true)]
    [InlineData(SpectrumStyle.Spectrogram, false, false, false)]
    [InlineData(SpectrumStyle.Tri, false, false, false)]
    [InlineData(SpectrumStyle.Chroma, false, false, false)]
    public void ApplicableSettings_FollowTheStyle(SpectrumStyle style, bool bandCount, bool gap, bool mirror)
    {
        var settings = AudioSpectrumConfig.ApplicableSettings(style);

        Assert.Equal(bandCount, settings.Contains(AudioVisualSetting.BandCount));
        Assert.Equal(gap, settings.Contains(AudioVisualSetting.BarGap));
        Assert.Equal(mirror, settings.Contains(AudioVisualSetting.Mirror));
        foreach (var always in new[]
                 {
                     AudioVisualSetting.Style, AudioVisualSetting.Width, AudioVisualSetting.FrequencyRange,
                     AudioVisualSetting.Decay, AudioVisualSetting.AccentColor, AudioVisualSetting.TimbreColor
                 })
        {
            Assert.Contains(always, settings);
        }
    }

    [Fact]
    public void DecimalMirrors_AgreeWithTheirSourceBounds()
    {
        Assert.Equal((decimal)AudioSpectrumConfig.MinBandCount, AudioSpectrumConfig.BandCountMinimum);
        Assert.Equal((decimal)AudioSpectrumConfig.MaxBandCount, AudioSpectrumConfig.BandCountMaximum);
        Assert.Equal((decimal)AudioSpectrumConfig.MinBarGap, AudioSpectrumConfig.BarGapMinimum);
        Assert.Equal((decimal)AudioSpectrumConfig.MaxBarGap, AudioSpectrumConfig.BarGapMaximum);
    }
}
