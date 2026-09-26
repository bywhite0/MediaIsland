using Avalonia.Media;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Media;
using MediaIsland.Services.MediaLink;
using Xunit;

namespace MediaIsland.Tests.Media;

/// <summary>
/// 封面取色服务。提取本身（PNG 编解码 + 色相分桶）已由 CoverThemeColorHelper 负责，
/// 这里只锁住服务层的契约：没人要就不动、同一张封面只取一次、换封面重取、没封面发布 null、
/// 慢的旧结果不得覆盖新封面的结果。提取器与线程调度都注入，用例里全部同步执行。
/// </summary>
public class CoverColorServiceTests
{
    private static readonly Color Red = Color.FromRgb(200, 30, 30);
    private static readonly Color Blue = Color.FromRgb(30, 30, 200);

    private sealed class FakeSource : IEffectiveMediaSource
    {
        public MediaInfo? EffectiveMediaInfo { get; set; }
        public LyricsSearchResult? EffectiveLyrics => null;
        public bool IsExternalMediaEffective => false;
        public LyricsSearchResult? GetLyricsForUi() => null;
        public int Subscribers => EffectiveMediaChanged?.GetInvocationList().Length ?? 0;

        public event EventHandler<MediaInfoChangedEventArgs>? EffectiveMediaChanged;
        public event EventHandler<LyricsSearchResultChangedEventArgs>? EffectiveLyricsChanged
        {
            add { }
            remove { }
        }

        public void Raise(MediaInfo? info)
        {
            EffectiveMediaInfo = info;
            EffectiveMediaChanged?.Invoke(this, new MediaInfoChangedEventArgs(info, MediaInfoChangeKind.MediaProperties));
        }
    }

    /// <summary>用封面对象身份当「这张封面是什么颜色」：测试里不需要真的解码位图。</summary>
    private sealed class FakeExtractor
    {
        public Dictionary<object, Color> Colors { get; } = [];
        public int Calls { get; private set; }

        public Color? Extract(object thumbnail)
        {
            Calls++;
            return Colors.TryGetValue(thumbnail, out var c) ? c : null;
        }
    }

    private static MediaInfo Track(string title) =>
        new("app", title, "a", null, TimeSpan.Zero, TimeSpan.Zero,
            new MediaPlaybackInfo(MediaPlaybackState.Playing), null, null);

    private static (CoverColorService Service, FakeSource Source, FakeExtractor Extractor, Dictionary<MediaInfo, object?> Covers)
        NewService()
    {
        var source = new FakeSource();
        var extractor = new FakeExtractor();
        var covers = new Dictionary<MediaInfo, object?>(ReferenceEqualityComparer.Instance);
        var service = new CoverColorService(
            source,
            info => covers.TryGetValue(info, out var cover) ? cover : null,
            extractor.Extract,
            run: work => Task.FromResult(work()),
            post: action => action());
        return (service, source, extractor, covers);
    }

    private static MediaInfo WithCover(Dictionary<MediaInfo, object?> covers, object? cover, string title = "t")
    {
        var info = Track(title);
        covers[info] = cover;
        return info;
    }

    [Fact]
    public void WithoutAnyRegistration_DoesNotSubscribeOrExtract()
    {
        var (service, source, extractor, covers) = NewService();

        source.Raise(WithCover(covers, new object()));

        Assert.Equal(0, source.Subscribers);
        Assert.Equal(0, extractor.Calls);
        Assert.Null(service.CurrentColor);
    }

    [Fact]
    public void FirstRegistration_ExtractsTheCurrentCoverImmediately()
    {
        var (service, source, extractor, covers) = NewService();
        var cover = new object();
        extractor.Colors[cover] = Red;
        source.EffectiveMediaInfo = WithCover(covers, cover);

        using var registration = service.Register();

        Assert.Equal(Red, service.CurrentColor);
        Assert.Equal(1, extractor.Calls);
    }

    [Fact]
    public void SameCover_AcrossManyMediaUpdates_IsExtractedOnce()
    {
        var (service, source, extractor, covers) = NewService();
        var cover = new object();
        extractor.Colors[cover] = Red;
        using var registration = service.Register();

        // 进度与播放状态的更新也会带着同一张封面到来，不该每次都重新解码。
        for (var i = 0; i < 5; i++) source.Raise(WithCover(covers, cover));

        Assert.Equal(1, extractor.Calls);
    }

    [Fact]
    public void NewCover_IsExtractedAndRaisesChanged()
    {
        var (service, source, extractor, covers) = NewService();
        var first = new object();
        var second = new object();
        extractor.Colors[first] = Red;
        extractor.Colors[second] = Blue;
        using var registration = service.Register();
        source.Raise(WithCover(covers, first));
        var changes = 0;
        service.ColorChanged += () => changes++;

        source.Raise(WithCover(covers, second, "next"));

        Assert.Equal(Blue, service.CurrentColor);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void NoCover_PublishesNullWithoutExtracting()
    {
        var (service, source, extractor, covers) = NewService();
        var cover = new object();
        extractor.Colors[cover] = Red;
        using var registration = service.Register();
        source.Raise(WithCover(covers, cover));

        source.Raise(WithCover(covers, null, "no-art"));
        source.Raise(null);

        Assert.Null(service.CurrentColor);
        Assert.Equal(1, extractor.Calls);
    }

    [Fact]
    public void LastRegistrationReleased_UnsubscribesAndForgetsTheColor()
    {
        var (service, source, extractor, covers) = NewService();
        var cover = new object();
        extractor.Colors[cover] = Red;
        source.EffectiveMediaInfo = WithCover(covers, cover);
        var a = service.Register();
        var b = service.Register();

        a.Dispose();
        Assert.Equal(1, source.Subscribers);

        b.Dispose();
        b.Dispose(); // 重复释放不得把计数减成负数
        Assert.Equal(0, source.Subscribers);
        Assert.Null(service.CurrentColor);

        using var again = service.Register();
        Assert.Equal(1, source.Subscribers);
        Assert.Equal(Red, service.CurrentColor);
    }

    [Fact]
    public void SlowOldExtraction_DoesNotOverwriteTheNewerCover()
    {
        var source = new FakeSource();
        var covers = new Dictionary<MediaInfo, object?>(ReferenceEqualityComparer.Instance);
        var pending = new List<(Func<Color?> Work, TaskCompletionSource<Color?> Done)>();
        var old = new object();
        var fresh = new object();
        var service = new CoverColorService(
            source,
            info => covers.TryGetValue(info, out var c) ? c : null,
            thumb => ReferenceEquals(thumb, old) ? Red : Blue,
            run: work =>
            {
                var tcs = new TaskCompletionSource<Color?>();
                pending.Add((work, tcs));
                return tcs.Task;
            },
            post: action => action());
        using var registration = service.Register();

        source.Raise(WithCover(covers, old));
        source.Raise(WithCover(covers, fresh, "next"));
        // 新封面先算完，旧封面后算完。
        pending[1].Done.SetResult(pending[1].Work());
        pending[0].Done.SetResult(pending[0].Work());

        Assert.Equal(Blue, service.CurrentColor);
    }

    [Fact]
    public void ExtractorThrows_PublishesNullInsteadOfCrashing()
    {
        var source = new FakeSource();
        var covers = new Dictionary<MediaInfo, object?>(ReferenceEqualityComparer.Instance);
        var service = new CoverColorService(
            source,
            info => covers.TryGetValue(info, out var c) ? c : null,
            _ => throw new InvalidOperationException("坏图"),
            run: work => Task.FromResult(work()),
            post: action => action());
        using var registration = service.Register();

        source.Raise(WithCover(covers, new object()));

        Assert.Null(service.CurrentColor);
    }
}
