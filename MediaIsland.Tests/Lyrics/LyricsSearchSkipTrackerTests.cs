using MediaIsland.Components;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsSearchSkipTrackerTests
{
    [Fact]
    public void TryRegister_ReportsEachSourceAndReasonOnlyOnceUntilReset()
    {
        var tracker = new LyricsSearchSkipTracker();

        Assert.True(tracker.TryRegister("top.imsyy.splayer-next", "已禁用歌词搜索"));
        Assert.False(tracker.TryRegister("top.imsyy.splayer-next", "已禁用歌词搜索"));
        Assert.True(tracker.TryRegister("top.imsyy.splayer-next", "已禁用"));

        tracker.Reset();

        Assert.True(tracker.TryRegister("top.imsyy.splayer-next", "已禁用歌词搜索"));
    }
}
