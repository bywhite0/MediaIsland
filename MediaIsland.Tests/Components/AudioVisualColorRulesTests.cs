using Avalonia.Media;
using MediaIsland.Components;
using MediaIsland.Controls;
using Xunit;

namespace MediaIsland.Tests.Components;

/// <summary>
/// 颜色来源 → 基色。三种来源各一条，外加两条回落：封面取不到退主题色、主题色也取不到退白色。
/// 回落写错的两种后果——封面缺失时组件变白、或主题资源缺失时抛——都不报错，只在岛上看得出来。
/// </summary>
public class AudioVisualColorRulesTests
{
    private static readonly Color Accent = Color.FromRgb(218, 100, 95);
    private static readonly Color Cover = Color.FromRgb(40, 120, 200);

    [Fact]
    public void Accent_UsesTheAccentColor_EvenWhenACoverColorExists()
    {
        Assert.Equal(Accent, AudioVisualColorRules.ResolveBase(AudioVisualColorSource.Accent, Cover, Accent));
    }

    [Fact]
    public void Cover_UsesTheCoverColor()
    {
        Assert.Equal(Cover, AudioVisualColorRules.ResolveBase(AudioVisualColorSource.Cover, Cover, Accent));
    }

    [Fact]
    public void Cover_WithoutACoverColor_FallsBackToTheAccentColor()
    {
        Assert.Equal(Accent, AudioVisualColorRules.ResolveBase(AudioVisualColorSource.Cover, null, Accent));
    }

    [Fact]
    public void White_IgnoresBothOtherSources()
    {
        Assert.Equal(Colors.White, AudioVisualColorRules.ResolveBase(AudioVisualColorSource.White, Cover, Accent));
    }

    [Theory]
    [InlineData(AudioVisualColorSource.Accent)]
    [InlineData(AudioVisualColorSource.Cover)]
    public void MissingAccentResource_FallsBackToWhite(AudioVisualColorSource source)
    {
        Assert.Equal(Colors.White, AudioVisualColorRules.ResolveBase(source, null, null));
    }

    [Fact]
    public void Foreground_WithoutTimbre_IsTheBaseColor()
    {
        Assert.Equal(Cover, AudioVisualColorRules.Foreground(Cover, useTimbre: false, centroid: 0.9));
    }

    [Fact]
    public void Foreground_WithTimbre_MixesFromTheBaseColor()
    {
        Assert.Equal(TimbreColor.Mix(Cover, 0.9), AudioVisualColorRules.Foreground(Cover, useTimbre: true, centroid: 0.9));
    }
}
