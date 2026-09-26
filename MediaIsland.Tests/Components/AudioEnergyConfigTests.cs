using MediaIsland.Components;
using Xunit;

namespace MediaIsland.Tests.Components;

public class AudioEnergyConfigTests
{
    [Fact]
    public void Defaults_MatchTheSpec()
    {
        var config = new AudioEnergyConfig();

        Assert.Equal(EnergyStyle.Orb, config.Style);
        Assert.Equal(48, config.Width);
    }

    [Theory]
    [InlineData(EnergyStyle.Orb)]
    [InlineData(EnergyStyle.Ripple)]
    [InlineData(EnergyStyle.GlowLine)]
    [InlineData(EnergyStyle.Dots)]
    [InlineData(EnergyStyle.History)]
    [InlineData(EnergyStyle.Beat)]
    public void ApplicableSettings_AreTheSameForEveryStyle(EnergyStyle style)
    {
        Assert.Equal(
            new HashSet<AudioVisualSetting>
            {
                AudioVisualSetting.Style, AudioVisualSetting.Width, AudioVisualSetting.FrequencyRange,
                AudioVisualSetting.Decay, AudioVisualSetting.ColorSource, AudioVisualSetting.TimbreColor
            },
            AudioEnergyConfig.ApplicableSettings(style));
    }
}
