using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MediaIsland.Helpers;
using MediaIsland.Services.MediaLink;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services.Media;

/// <summary>
/// 当前曲目的封面主题色，供选了「封面主题色」的音频组件共用。
///
/// 做成共享单例而不是各组件自己取：一次提取要把位图编码成 PNG 再解码、缩样、分桶，
/// 岛上放几个组件就重复几遍没有意义。只认封面对象的身份——进度与播放状态的更新会带着
/// 同一张封面反复到来，同一张只取一次；换了封面才重取。
///
/// 需求计数驱动订阅：没有组件选封面色时不订阅媒体变化、不做任何提取。
/// 数据源是生效媒体（<see cref="IEffectiveMediaSource"/>），本机与上游 MediaLink 的曲目都覆盖。
///
/// 提取在后台线程做，结果投递回 UI 线程再发布；换得快时旧封面可能后算完，
/// 用递增的版本号丢弃过期结果，否则颜色会退回上一首。
/// </summary>
public sealed class CoverColorService
{
    private readonly IEffectiveMediaSource _source;
    private readonly Func<MediaInfo, object?> _coverOf;
    private readonly Func<object, Color?> _extract;
    private readonly Func<Func<Color?>, Task<Color?>> _run;
    private readonly Action<Action> _post;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private int _demand;
    private object? _currentCover;
    private long _version;

    /// <summary>生产用构造：封面取 <see cref="MediaInfo.Thumbnail"/>，提取交给 <see cref="CoverThemeColorHelper"/>。</summary>
    public CoverColorService(IEffectiveMediaSource source, ILogger<CoverColorService>? logger = null)
        : this(
            source,
            info => info.Thumbnail,
            cover => CoverThemeColorHelper.TryExtract(cover as Bitmap),
            logger: logger)
    {
    }

    /// <summary>
    /// 封面、提取与线程调度都可注入：测试里不需要真的解码位图，也不需要 UI 线程。
    /// </summary>
    internal CoverColorService(
        IEffectiveMediaSource source,
        Func<MediaInfo, object?> coverOf,
        Func<object, Color?> extract,
        Func<Func<Color?>, Task<Color?>>? run = null,
        Action<Action>? post = null,
        ILogger? logger = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _coverOf = coverOf ?? throw new ArgumentNullException(nameof(coverOf));
        _extract = extract ?? throw new ArgumentNullException(nameof(extract));
        _run = run ?? Task.Run;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _logger = logger;
    }

    /// <summary>当前封面的主题色；没有封面、取不到或无人需要时为 null。只在 UI 线程上变化。</summary>
    public Color? CurrentColor { get; private set; }

    /// <summary>在 UI 线程上触发。</summary>
    public event Action? ColorChanged;

    /// <summary>登记需求。释放返回值即撤销；重复释放无副作用。</summary>
    public IDisposable Register()
    {
        bool first;
        lock (_gate)
        {
            first = ++_demand == 1;
        }

        if (first)
        {
            _source.EffectiveMediaChanged += OnEffectiveMediaChanged;
            Update(_source.EffectiveMediaInfo);
        }

        return new Registration(this);
    }

    private void Release()
    {
        bool last;
        lock (_gate)
        {
            last = --_demand == 0;
            if (last)
            {
                _currentCover = null;
                _version++;
            }
        }

        if (!last) return;
        _source.EffectiveMediaChanged -= OnEffectiveMediaChanged;
        _post(() => Publish(null));
    }

    private void OnEffectiveMediaChanged(object? sender, MediaInfoChangedEventArgs e) => Update(e.MediaInfo);

    private void Update(MediaInfo? info)
    {
        var cover = info is null ? null : _coverOf(info);
        long version;
        lock (_gate)
        {
            if (_demand == 0 || ReferenceEquals(cover, _currentCover)) return;
            _currentCover = cover;
            version = ++_version;
        }

        if (cover is null)
        {
            _post(() => PublishIfCurrent(version, null));
            return;
        }

        _ = ExtractAsync(cover, version);
    }

    private async Task ExtractAsync(object cover, long version)
    {
        Color? color;
        try
        {
            color = await _run(() => _extract(cover));
        }
        catch (Exception ex)
        {
            // 取色失败只意味着回落主题色，不该让一张坏图影响播放信息的其余部分。
            _logger?.LogDebug(ex, "[媒体] 封面取色失败，回落主题色");
            color = null;
        }

        _post(() => PublishIfCurrent(version, color));
    }

    private void PublishIfCurrent(long version, Color? color)
    {
        lock (_gate)
        {
            if (version != _version) return;
        }

        Publish(color);
    }

    private void Publish(Color? color)
    {
        if (CurrentColor == color) return;
        CurrentColor = color;
        ColorChanged?.Invoke();
    }

    private sealed class Registration(CoverColorService owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release();
            }
        }
    }
}
