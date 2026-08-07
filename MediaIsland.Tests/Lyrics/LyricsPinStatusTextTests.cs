using MediaIsland.SettingsPages;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsPinStatusTextTests
{
    private static readonly DateTimeOffset PinnedAt = new(2026, 8, 6, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Describe_NoPin_SaysSo()
    {
        var text = LyricsPinStatusText.Describe(
            hasPin: false,
            pinSource: null,
            pinnedAtUtc: null,
            isSuppressedBySPlayerNext: false,
            isSearchDisabled: false);

        Assert.Contains("未固定", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ActivePin_ShowsSourceAndDate()
    {
        var text = LyricsPinStatusText.Describe(
            hasPin: true,
            pinSource: "QqMusic",
            pinnedAtUtc: PinnedAt,
            isSuppressedBySPlayerNext: false,
            isSearchDisabled: false);

        Assert.Contains("已固定", text, StringComparison.Ordinal);
        Assert.Contains("QqMusic", text, StringComparison.Ordinal);
        Assert.Contains("2026-08-06", text, StringComparison.Ordinal);
    }

    /// <summary>pin 存在但未生效时必须说明原因，否则用户会认为固定坏了。</summary>
    [Fact]
    public void Describe_SuppressedBySPlayerNext_ExplainsWhy()
    {
        var text = LyricsPinStatusText.Describe(
            hasPin: true,
            pinSource: "QqMusic",
            pinnedAtUtc: PinnedAt,
            isSuppressedBySPlayerNext: true,
            isSearchDisabled: false);

        Assert.Contains("SPlayer-Next", text, StringComparison.Ordinal);
        Assert.Contains("未生效", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_SearchDisabled_PointsAtTheSourceSwitch()
    {
        var text = LyricsPinStatusText.Describe(
            hasPin: true,
            pinSource: "QqMusic",
            pinnedAtUtc: PinnedAt,
            isSuppressedBySPlayerNext: false,
            isSearchDisabled: true);

        Assert.Contains("未生效", text, StringComparison.Ordinal);
        Assert.Contains("歌词搜索", text, StringComparison.Ordinal);
    }

    /// <summary>门禁比 SPlayer-Next 更靠前，两者同时成立时应报告门禁。</summary>
    [Fact]
    public void Describe_BothSuppressors_ReportsTheGateFirst()
    {
        var text = LyricsPinStatusText.Describe(
            hasPin: true,
            pinSource: "QqMusic",
            pinnedAtUtc: PinnedAt,
            isSuppressedBySPlayerNext: true,
            isSearchDisabled: true);

        Assert.Contains("歌词搜索", text, StringComparison.Ordinal);
    }

    /// <summary>没有 pin 时不该冒出「未生效」这种误导性文案。</summary>
    [Fact]
    public void Describe_NoPin_DoesNotMentionSuppression()
    {
        var text = LyricsPinStatusText.Describe(
            hasPin: false,
            pinSource: null,
            pinnedAtUtc: null,
            isSuppressedBySPlayerNext: true,
            isSearchDisabled: true);

        Assert.DoesNotContain("未生效", text, StringComparison.Ordinal);
    }
}
