using MediaIsland.Components;
using Xunit;

namespace MediaIsland.Tests.Components;

public class AudioVisualConfigBaseTests
{
    private sealed class Probe() : AudioFrequencyConfigBase(defaultWidth: 64);

    [Fact]
    public void DefaultWidth_ComesFromTheDerivedComponent()
    {
        Assert.Equal(64, new Probe().Width);
    }

    [Fact]
    public void Width_ClampsToNamedBounds()
    {
        var config = new Probe { Width = AudioVisualConfigBase.MinComponentWidth - 100 };
        Assert.Equal(AudioVisualConfigBase.MinComponentWidth, config.Width);

        config.Width = AudioVisualConfigBase.MaxComponentWidth + 100;
        Assert.Equal(AudioVisualConfigBase.MaxComponentWidth, config.Width);
    }

    [Fact]
    public void Decay_ClampsToNamedBounds()
    {
        var config = new Probe { DecayPerSecond = 0 };
        Assert.Equal(AudioVisualConfigBase.MinDecayPerSecond, config.DecayPerSecond);

        config.DecayPerSecond = 1000;
        Assert.Equal(AudioVisualConfigBase.MaxDecayPerSecond, config.DecayPerSecond);
    }

    [Fact]
    public void Frequencies_ClampToNamedBounds()
    {
        var config = new Probe
        {
            MinFrequencyHz = 1,
            MaxFrequencyHz = 1_000_000
        };

        Assert.Equal(AudioFrequencyConfigBase.MinFrequencyLowerHz, config.MinFrequencyHz);
        Assert.Equal(AudioFrequencyConfigBase.MaxFrequencyUpperHz, config.MaxFrequencyHz);
    }

    [Fact]
    public void NormalizedRange_SwapsInvertedBounds()
    {
        var config = new Probe { MinFrequencyHz = 5000, MaxFrequencyHz = 300 };

        Assert.Equal((300d, 5000d), config.NormalizedRange());
    }

    [Fact]
    public void NormalizedRange_EqualBounds_OpensOneHertz()
    {
        var config = new Probe { MinFrequencyHz = 1000, MaxFrequencyHz = 1000 };

        Assert.Equal((1000d, 1001d), config.NormalizedRange());
    }

    [Fact]
    public void TimbreColor_IsOffByDefault_AccentIsOn()
    {
        var config = new Probe();

        Assert.False(config.UseTimbreColor);
        Assert.True(config.UseAccentColor);
    }

    [Fact]
    public void DecimalMirrors_AgreeWithTheirSourceBounds()
    {
        Assert.Equal((decimal)AudioVisualConfigBase.MinComponentWidth, AudioVisualConfigBase.ComponentWidthMinimum);
        Assert.Equal((decimal)AudioVisualConfigBase.MaxComponentWidth, AudioVisualConfigBase.ComponentWidthMaximum);
        Assert.Equal((decimal)AudioVisualConfigBase.MinDecayPerSecond, AudioVisualConfigBase.DecayMinimum);
        Assert.Equal((decimal)AudioVisualConfigBase.MaxDecayPerSecond, AudioVisualConfigBase.DecayMaximum);
        Assert.Equal((decimal)AudioFrequencyConfigBase.MinFrequencyLowerHz, AudioFrequencyConfigBase.FrequencyLowerMinimum);
        Assert.Equal((decimal)AudioFrequencyConfigBase.MaxFrequencyLowerHz, AudioFrequencyConfigBase.FrequencyLowerMaximum);
        Assert.Equal((decimal)AudioFrequencyConfigBase.MinFrequencyUpperHz, AudioFrequencyConfigBase.FrequencyUpperMinimum);
        Assert.Equal((decimal)AudioFrequencyConfigBase.MaxFrequencyUpperHz, AudioFrequencyConfigBase.FrequencyUpperMaximum);
    }
}
