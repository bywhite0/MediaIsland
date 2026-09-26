using MediaIsland.Components;
using Xunit;

namespace MediaIsland.Tests.Components;

public class AudioLevelConfigTests
{
    [Fact]
    public void Defaults_MatchTheSpec()
    {
        var config = new AudioLevelConfig();

        Assert.Equal(LevelStyle.Meter, config.Style);
        Assert.Equal(80, config.Width);
    }

    [Theory]
    [InlineData(LevelStyle.Meter)]
    [InlineData(LevelStyle.Pan)]
    public void ApplicableSettings_ExcludeFrequencyAndSpectrumOptions(LevelStyle style)
    {
        Assert.Equal(
            new HashSet<AudioVisualSetting>
            {
                AudioVisualSetting.Style, AudioVisualSetting.Width, AudioVisualSetting.Decay,
                AudioVisualSetting.ColorSource, AudioVisualSetting.TimbreColor
            },
            AudioLevelConfig.ApplicableSettings(style));
    }
}
