using System.ComponentModel;
using MediaIsland.Models;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Media;

namespace MediaIsland.Services.MediaLink;

public sealed class MediaSourceCoordinator : IEffectiveMediaSource, IDisposable
{
    private readonly IMediaService _media;
    private readonly LyricsSearchService _lyrics;
    private readonly MediaLinkInjectionStore _store;
    private readonly Func<PluginSettings> _settings;
    private readonly INotifyPropertyChanged? _settingsNotify;
    private readonly object _gate = new();

    private MediaInfo? _composedMedia;
    private LyricsSearchResult? _composedLyrics;
    private bool _isExternalMediaEffective;
    private MediaInfo? _uiMedia;
    private LyricsSearchResult? _uiLyrics;
    private bool _disposed;

    public MediaSourceCoordinator(
        IMediaService media,
        LyricsSearchService lyrics,
        MediaLinkInjectionStore store,
        Func<PluginSettings> settings)
    {
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _lyrics = lyrics ?? throw new ArgumentNullException(nameof(lyrics));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _media.MediaInfoChanged += OnMediaInfoChanged;
        _lyrics.CurrentResultChanged += OnLyricsChanged;
        _store.Changed += OnStoreChanged;

        var currentSettings = _settings();
        if (currentSettings is INotifyPropertyChanged notify)
        {
            _settingsNotify = notify;
            _settingsNotify.PropertyChanged += OnSettingsPropertyChanged;
        }

        Recompute();
    }

    public MediaInfo? EffectiveMediaInfo
    {
        get
        {
            lock (_gate)
            {
                return _uiMedia;
            }
        }
    }

    public LyricsSearchResult? EffectiveLyrics
    {
        get
        {
            lock (_gate)
            {
                return _uiLyrics;
            }
        }
    }

    public bool IsExternalMediaEffective
    {
        get
        {
            lock (_gate)
            {
                return _isExternalMediaEffective;
            }
        }
    }

    public event EventHandler<MediaInfoChangedEventArgs>? EffectiveMediaChanged;
    public event EventHandler<LyricsSearchResultChangedEventArgs>? EffectiveLyricsChanged;

    public MediaInfo? ComposeMedia()
    {
        lock (_gate)
        {
            return ComposeMediaUnlocked();
        }
    }

    public MediaInfo? GetMediaForUi()
    {
        var settings = _settings();
        lock (_gate)
        {
            return settings.MediaLinkUiUsesEffective
                ? ComposeMediaUnlocked()
                : _media.CurrentMediaInfo;
        }
    }

    public MediaInfo? GetMediaForPush()
    {
        var settings = _settings();
        lock (_gate)
        {
            return settings.MediaLinkPushUsesEffective
                ? ComposeMediaUnlocked()
                : _media.CurrentMediaInfo;
        }
    }

    public LyricsSearchResult? ComposeLyrics()
    {
        lock (_gate)
        {
            return ComposeLyricsUnlocked(ComposeMediaUnlocked());
        }
    }

    public LyricsSearchResult? GetLyricsForUi()
    {
        var settings = _settings();
        lock (_gate)
        {
            if (!settings.MediaLinkUiUsesEffective)
            {
                return _lyrics.GetCurrentResultFor(_media.CurrentMediaInfo);
            }

            return ComposeLyricsUnlocked(ComposeMediaUnlocked());
        }
    }

    public LyricsSearchResult? GetLyricsForPush()
    {
        var settings = _settings();
        lock (_gate)
        {
            if (!settings.MediaLinkPushUsesEffective)
            {
                return _lyrics.GetCurrentResultFor(_media.CurrentMediaInfo);
            }

            return ComposeLyricsUnlocked(ComposeMediaUnlocked());
        }
    }

    public void Recompute(MediaInfoChangeKind kind = MediaInfoChangeKind.CurrentSession)
    {
        MediaInfo? uiMedia;
        LyricsSearchResult? uiLyrics;
        MediaInfo? previousUiMedia;
        LyricsSearchResult? previousUiLyrics;
        bool mediaChanged;
        bool lyricsChanged;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var settings = _settings();
            var composedMedia = ComposeMediaUnlocked();
            var isExternal = IsExternalUnlocked(settings.MediaLinkMediaSourceMode, composedMedia);
            var composedLyrics = ComposeLyricsUnlocked(composedMedia);

            _composedMedia = composedMedia;
            _composedLyrics = composedLyrics;
            _isExternalMediaEffective = isExternal;

            uiMedia = settings.MediaLinkUiUsesEffective
                ? composedMedia
                : _media.CurrentMediaInfo;
            uiLyrics = settings.MediaLinkUiUsesEffective
                ? composedLyrics
                : _lyrics.GetCurrentResultFor(_media.CurrentMediaInfo);

            previousUiMedia = _uiMedia;
            previousUiLyrics = _uiLyrics;
            mediaChanged = !Equals(previousUiMedia, uiMedia);
            lyricsChanged = !Equals(previousUiLyrics, uiLyrics);

            _uiMedia = uiMedia;
            _uiLyrics = uiLyrics;
        }

        if (mediaChanged)
        {
            EffectiveMediaChanged?.Invoke(this, new MediaInfoChangedEventArgs(uiMedia, kind));
        }

        if (lyricsChanged)
        {
            EffectiveLyricsChanged?.Invoke(this, new LyricsSearchResultChangedEventArgs(uiLyrics));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _media.MediaInfoChanged -= OnMediaInfoChanged;
        _lyrics.CurrentResultChanged -= OnLyricsChanged;
        _store.Changed -= OnStoreChanged;
        if (_settingsNotify is not null)
        {
            _settingsNotify.PropertyChanged -= OnSettingsPropertyChanged;
        }
    }

    private void OnMediaInfoChanged(object? sender, MediaInfoChangedEventArgs e) =>
        Recompute(e.ChangeKind);

    private void OnLyricsChanged(object? sender, LyricsSearchResultChangedEventArgs e) =>
        Recompute(MediaInfoChangeKind.CurrentSession);

    private void OnStoreChanged(object? sender, EventArgs e) =>
        Recompute(MediaInfoChangeKind.CurrentSession);

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null
            or nameof(PluginSettings.MediaLinkMediaSourceMode)
            or nameof(PluginSettings.MediaLinkUiUsesEffective)
            or nameof(PluginSettings.MediaLinkPushUsesEffective))
        {
            Recompute(MediaInfoChangeKind.CurrentSession);
        }
    }

    private MediaInfo? ComposeMediaUnlocked()
    {
        var mode = _settings().MediaLinkMediaSourceMode;
        var platform = _media.CurrentMediaInfo;
        var inject = _store.GetMediaSnapshot();

        return mode switch
        {
            MediaLinkMediaSourceMode.PlatformOnly => platform,
            MediaLinkMediaSourceMode.ExternalOnly => inject,
            MediaLinkMediaSourceMode.ExternalPreferred => inject ?? platform,
            _ => platform
        };
    }

    private LyricsSearchResult? ComposeLyricsUnlocked(MediaInfo? effectiveMedia)
    {
        var mode = _settings().MediaLinkMediaSourceMode;
        var injectLyrics = _store.GetLyricsSnapshot();

        return mode switch
        {
            MediaLinkMediaSourceMode.PlatformOnly =>
                _lyrics.GetCurrentResultFor(_media.CurrentMediaInfo),
            MediaLinkMediaSourceMode.ExternalOnly => injectLyrics,
            MediaLinkMediaSourceMode.ExternalPreferred =>
                injectLyrics ?? _lyrics.GetCurrentResultFor(effectiveMedia),
            _ => _lyrics.GetCurrentResultFor(_media.CurrentMediaInfo)
        };
    }

    private bool IsExternalUnlocked(MediaLinkMediaSourceMode mode, MediaInfo? composedMedia)
    {
        if (composedMedia is null)
        {
            return false;
        }

        return mode switch
        {
            MediaLinkMediaSourceMode.ExternalOnly => _store.HasExternalMedia,
            MediaLinkMediaSourceMode.ExternalPreferred => _store.HasExternalMedia,
            _ => false
        };
    }
}
