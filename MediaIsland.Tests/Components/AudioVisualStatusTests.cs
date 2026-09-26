using MediaIsland.Controls;
using Xunit;

namespace MediaIsland.Tests.Components;

public class AudioVisualStatusTests
{
    [Theory]
    [InlineData(true, true, true, AudioVisualStatus.Active)]
    [InlineData(true, false, true, AudioVisualStatus.Active)]
    [InlineData(false, true, true, AudioVisualStatus.Active)]
    [InlineData(false, false, true, AudioVisualStatus.Active)]
    [InlineData(true, true, false, AudioVisualStatus.Idle)]
    [InlineData(true, false, false, AudioVisualStatus.Idle)]
    [InlineData(false, true, false, AudioVisualStatus.Idle)]
    [InlineData(false, false, false, AudioVisualStatus.Unavailable)]
    public void Resolve_CoversTheFullTruthTable(
        bool localAvailable, bool consumingUpstream, bool hasRecentData, AudioVisualStatus expected)
    {
        Assert.Equal(expected, AudioVisualStatusRules.Resolve(localAvailable, consumingUpstream, hasRecentData));
    }

    [Fact]
    public void Opacity_DimsOnlyWhenUnavailable()
    {
        Assert.Equal(1, AudioVisualStatusRules.OpacityFor(AudioVisualStatus.Active));
        Assert.Equal(1, AudioVisualStatusRules.OpacityFor(AudioVisualStatus.Idle));
        Assert.Equal(0.35, AudioVisualStatusRules.OpacityFor(AudioVisualStatus.Unavailable));
    }

    [Fact]
    public void Describe_Upstream_NamesTheUpstreamSource()
    {
        var (text, warning) = AudioVisualStatusRules.Describe(true, null, consumingUpstream: true, silent: false);

        Assert.Equal("数据来源：Link-Like MediaLink 上游", text);
        Assert.False(warning);
    }

    [Fact]
    public void Describe_Local_Silent_AppendsNoSound()
    {
        var (text, warning) = AudioVisualStatusRules.Describe(true, null, consumingUpstream: false, silent: true);

        Assert.Equal("数据来源：本机采集（当前无声音）", text);
        Assert.False(warning);
    }

    [Fact]
    public void Describe_Unavailable_IsAWarningWithTheReason()
    {
        var (text, warning) = AudioVisualStatusRules.Describe(false, "缺少 MediaIsland.Audio", false, true);

        Assert.Equal("无法采集本机音频：缺少 MediaIsland.Audio。上游有音频时仍可显示。", text);
        Assert.True(warning);
    }

    [Fact]
    public void Describe_UnavailableWithoutReason_StillReadsWell()
    {
        var (text, _) = AudioVisualStatusRules.Describe(false, null, false, true);

        Assert.Equal("无法采集本机音频：原因未知。上游有音频时仍可显示。", text);
    }
}
