using MediaIsland.Components;
using Xunit;

namespace MediaIsland.Tests.Components;

/// <summary>
/// 目标数组的长度只由配置决定：组件在没有新快照（空谱）时改设置，靠这一点立刻换成新样式的点数。
/// </summary>
public class AudioSpectrumTargetsTests
{
    private const int SampleRate = 48000;

    [Theory]
    [InlineData(SpectrumStyle.Capsule, 7)]
    [InlineData(SpectrumStyle.PeakCap, 16)]
    [InlineData(SpectrumStyle.Ridge, 12)]
    [InlineData(SpectrumStyle.Spectrogram, 6)]
    [InlineData(SpectrumStyle.Tri, 3)]
    [InlineData(SpectrumStyle.Chroma, 12)]
    public void EmptySpectrum_YieldsZerosOfTheStylesLength(SpectrumStyle style, int expected)
    {
        var config = new AudioSpectrumConfig { Style = style };

        var targets = AudioSpectrumConfig.MapTargets(config, [], SampleRate);

        Assert.Equal(expected, targets.Length);
        Assert.All(targets, v => Assert.Equal(0f, v));
    }

    [Theory]
    [InlineData(SpectrumStyle.Capsule)]
    [InlineData(SpectrumStyle.PeakCap)]
    [InlineData(SpectrumStyle.Ridge)]
    public void BandStyles_FollowTheirOwnBandCount(SpectrumStyle style)
    {
        var config = new AudioSpectrumConfig { Style = style, BandCount = 21 };

        Assert.Equal(21, AudioSpectrumConfig.MapTargets(config, [], SampleRate).Length);
    }

    [Fact]
    public void StyleChange_ChangesTheLengthToTheNewStyles()
    {
        var config = new AudioSpectrumConfig { BandCount = 9 };
        Assert.Equal(9, AudioSpectrumConfig.MapTargets(config, [], SampleRate).Length);

        config.Style = SpectrumStyle.PeakCap;
        Assert.Equal(16, AudioSpectrumConfig.MapTargets(config, [], SampleRate).Length);

        config.Style = SpectrumStyle.Tri;
        Assert.Equal(3, AudioSpectrumConfig.MapTargets(config, [], SampleRate).Length);

        config.Style = SpectrumStyle.Capsule;
        Assert.Equal(9, AudioSpectrumConfig.MapTargets(config, [], SampleRate).Length);
    }

    [Fact]
    public void InvertedFrequencyRange_DoesNotThrow()
    {
        var config = new AudioSpectrumConfig { MinFrequencyHz = 5000, MaxFrequencyHz = 200 };
        var spectrum = new float[1024];
        spectrum[20] = 1f;

        foreach (var style in Enum.GetValues<SpectrumStyle>())
        {
            config.Style = style;
            _ = AudioSpectrumConfig.MapTargets(config, spectrum, SampleRate);
        }
    }
}
