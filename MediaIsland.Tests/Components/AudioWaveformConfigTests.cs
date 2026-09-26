using MediaIsland.Components;
using Xunit;

namespace MediaIsland.Tests.Components;

public class AudioWaveformConfigTests
{
    [Fact]
    public void DefaultWidth_Is96()
    {
        Assert.Equal(96, new AudioWaveformConfig().Width);
    }

    [Fact]
    public void ApplicableSettings_AreWidthAndColorsOnly()
    {
        Assert.Equal(
            new HashSet<AudioVisualSetting>
            {
                AudioVisualSetting.Width, AudioVisualSetting.ColorSource, AudioVisualSetting.TimbreColor
            },
            AudioWaveformConfig.ApplicableSettings());
    }
}
