using MediaIsland.Components;
using Xunit;

namespace MediaIsland.Tests.Components;

/// <summary>
/// 频谱配置七对区间的夹紧判据。
///
/// 每条都引用具名常量而不写死数字：写死的话，改常量时断言跟着「对」，
/// 于是「setter 用的是常量还是字面量」这件事就无从分辨——而那正是本组判据要钉的东西。
/// </summary>
public class SpectrumComponentConfigTests
{
    [Fact]
    public void BandCount_ClampsToNamedBounds()
    {
        var config = new SpectrumComponentConfig();

        config.BandCount = SpectrumComponentConfig.MaxBandCount + 1;
        Assert.Equal(SpectrumComponentConfig.MaxBandCount, config.BandCount);

        config.BandCount = SpectrumComponentConfig.MinBandCount - 1;
        Assert.Equal(SpectrumComponentConfig.MinBandCount, config.BandCount);
    }

    [Fact]
    public void Width_ClampsToNamedBounds()
    {
        var config = new SpectrumComponentConfig();

        config.Width = SpectrumComponentConfig.MaxComponentWidth + 1;
        Assert.Equal(SpectrumComponentConfig.MaxComponentWidth, config.Width);

        config.Width = SpectrumComponentConfig.MinComponentWidth - 1;
        Assert.Equal(SpectrumComponentConfig.MinComponentWidth, config.Width);
    }

    [Fact]
    public void BarGap_ClampsToNamedBounds()
    {
        var config = new SpectrumComponentConfig();

        config.BarGap = SpectrumComponentConfig.MaxBarGap + 1;
        Assert.Equal(SpectrumComponentConfig.MaxBarGap, config.BarGap);

        config.BarGap = SpectrumComponentConfig.MinBarGap - 1;
        Assert.Equal(SpectrumComponentConfig.MinBarGap, config.BarGap);
    }

    [Fact]
    public void BarCornerRadius_ClampsToNamedBounds()
    {
        var config = new SpectrumComponentConfig();

        config.BarCornerRadius = SpectrumComponentConfig.MaxBarCornerRadius + 1;
        Assert.Equal(SpectrumComponentConfig.MaxBarCornerRadius, config.BarCornerRadius);

        config.BarCornerRadius = SpectrumComponentConfig.MinBarCornerRadius - 1;
        Assert.Equal(SpectrumComponentConfig.MinBarCornerRadius, config.BarCornerRadius);
    }

    [Fact]
    public void MinFrequencyHz_ClampsToNamedBounds()
    {
        var config = new SpectrumComponentConfig();

        config.MinFrequencyHz = SpectrumComponentConfig.MaxFrequencyLowerHz + 1;
        Assert.Equal(SpectrumComponentConfig.MaxFrequencyLowerHz, config.MinFrequencyHz);

        config.MinFrequencyHz = SpectrumComponentConfig.MinFrequencyLowerHz - 1;
        Assert.Equal(SpectrumComponentConfig.MinFrequencyLowerHz, config.MinFrequencyHz);
    }

    [Fact]
    public void MaxFrequencyHz_ClampsToNamedBounds()
    {
        var config = new SpectrumComponentConfig();

        config.MaxFrequencyHz = SpectrumComponentConfig.MaxFrequencyUpperHz + 1;
        Assert.Equal(SpectrumComponentConfig.MaxFrequencyUpperHz, config.MaxFrequencyHz);

        config.MaxFrequencyHz = SpectrumComponentConfig.MinFrequencyUpperHz - 1;
        Assert.Equal(SpectrumComponentConfig.MinFrequencyUpperHz, config.MaxFrequencyHz);
    }

    [Fact]
    public void DecayPerSecond_ClampsToNamedBounds()
    {
        var config = new SpectrumComponentConfig();

        config.DecayPerSecond = SpectrumComponentConfig.MaxDecayPerSecond + 1;
        Assert.Equal(SpectrumComponentConfig.MaxDecayPerSecond, config.DecayPerSecond);

        config.DecayPerSecond = SpectrumComponentConfig.MinDecayPerSecond - 0.1;
        Assert.Equal(SpectrumComponentConfig.MinDecayPerSecond, config.DecayPerSecond);
    }

    [Fact]
    public void DecimalMirrors_AgreeWithTheirSourceBounds()
    {
        // 镜像存在的唯一理由是 NumericUpDown 的 Minimum / Maximum 是 decimal?，
        // 而 x:Static 不做类型转换。它们必须由上面那份区间推导而来。
        // 有人把某个镜像改回字面量，真值源就又变成两份，而设置页读的是镜像——
        // 那份分裂在界面上不会有任何提示，也不会让任何别的判据变红。
        Assert.Equal((decimal)SpectrumComponentConfig.MinBandCount, SpectrumComponentConfig.BandCountMinimum);
        Assert.Equal((decimal)SpectrumComponentConfig.MaxBandCount, SpectrumComponentConfig.BandCountMaximum);

        Assert.Equal((decimal)SpectrumComponentConfig.MinComponentWidth, SpectrumComponentConfig.ComponentWidthMinimum);
        Assert.Equal((decimal)SpectrumComponentConfig.MaxComponentWidth, SpectrumComponentConfig.ComponentWidthMaximum);

        Assert.Equal((decimal)SpectrumComponentConfig.MinBarGap, SpectrumComponentConfig.BarGapMinimum);
        Assert.Equal((decimal)SpectrumComponentConfig.MaxBarGap, SpectrumComponentConfig.BarGapMaximum);

        Assert.Equal((decimal)SpectrumComponentConfig.MinBarCornerRadius, SpectrumComponentConfig.BarCornerRadiusMinimum);
        Assert.Equal((decimal)SpectrumComponentConfig.MaxBarCornerRadius, SpectrumComponentConfig.BarCornerRadiusMaximum);

        Assert.Equal((decimal)SpectrumComponentConfig.MinFrequencyLowerHz, SpectrumComponentConfig.FrequencyLowerMinimum);
        Assert.Equal((decimal)SpectrumComponentConfig.MaxFrequencyLowerHz, SpectrumComponentConfig.FrequencyLowerMaximum);

        Assert.Equal((decimal)SpectrumComponentConfig.MinFrequencyUpperHz, SpectrumComponentConfig.FrequencyUpperMinimum);
        Assert.Equal((decimal)SpectrumComponentConfig.MaxFrequencyUpperHz, SpectrumComponentConfig.FrequencyUpperMaximum);

        Assert.Equal((decimal)SpectrumComponentConfig.MinDecayPerSecond, SpectrumComponentConfig.DecayMinimum);
        Assert.Equal((decimal)SpectrumComponentConfig.MaxDecayPerSecond, SpectrumComponentConfig.DecayMaximum);
    }
}
