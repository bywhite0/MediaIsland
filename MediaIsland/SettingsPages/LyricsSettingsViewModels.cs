using System.ComponentModel;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.SettingsPages;

public sealed class LyricsSourceItemViewModel : INotifyPropertyChanged
{
    private bool _isEnabled;
    private bool _useWordSyncedLyrics;
    private int _globalOffsetMilliseconds;
    private string _globalOffsetMillisecondsText;
    private readonly Action _onChanged;

    public LyricsSourceItemViewModel(LyricsSourceEntry entry, Action onChanged)
    {
        Id = entry.Id;
        _isEnabled = entry.IsEnabled;
        _useWordSyncedLyrics = entry.UseWordSyncedLyrics;
        _globalOffsetMilliseconds = entry.GlobalOffsetMilliseconds;
        _globalOffsetMillisecondsText = _globalOffsetMilliseconds.ToString();
        _onChanged = onChanged;
    }

    public LyricsSourceId Id { get; }

    public string DisplayName => GetDisplayName(Id);

    public static string GetDisplayName(LyricsSourceId id) => id switch
    {
        LyricsSourceId.AmllTtml => "AMLL TTML DB",
        LyricsSourceId.QqMusic => "QQ 音乐",
        LyricsSourceId.Kugou => "酷狗音乐",
        LyricsSourceId.Netease => "网易云音乐",
        LyricsSourceId.SPlayerNext => "SPlayer-Next",
        LyricsSourceId.External => "外部来源",
        _ => id.ToString()
    };

    public string Capability => Id switch
    {
        LyricsSourceId.AmllTtml => "TTML 逐字",
        LyricsSourceId.QqMusic => "逐字 QRC / LRC",
        LyricsSourceId.Kugou => "逐字 KRC",
        LyricsSourceId.Netease => "LRC",
        LyricsSourceId.SPlayerNext => "外部 API 直出",
        _ => string.Empty
    };

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            _onChanged();
        }
    }

    public bool UseWordSyncedLyrics
    {
        get => _useWordSyncedLyrics;
        set
        {
            if (_useWordSyncedLyrics == value) return;
            _useWordSyncedLyrics = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseWordSyncedLyrics)));
            _onChanged();
        }
    }

    public int GlobalOffsetMilliseconds
    {
        get => _globalOffsetMilliseconds;
        set
        {
            if (_globalOffsetMilliseconds == value) return;
            _globalOffsetMilliseconds = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlobalOffsetMilliseconds)));
            _onChanged();
        }
    }

    public string GlobalOffsetMillisecondsText
    {
        get => _globalOffsetMillisecondsText;
        set
        {
            if (_globalOffsetMillisecondsText == value) return;
            _globalOffsetMillisecondsText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlobalOffsetMillisecondsText)));

            if (string.IsNullOrWhiteSpace(value) || !int.TryParse(value, out var offset))
            {
                return;
            }

            GlobalOffsetMilliseconds = offset;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class LyricsCandidateItemViewModel
{
    public LyricsCandidateItemViewModel(LyricsCandidate candidate)
    {
        Candidate = candidate;
        Source = LyricsSourceItemViewModel.GetDisplayName(candidate.Source);
        Title = string.IsNullOrWhiteSpace(candidate.Title) ? "未知标题" : candidate.Title;
        Artist = string.IsNullOrWhiteSpace(candidate.Artist) ? "未知艺术家" : candidate.Artist;
        Album = string.IsNullOrWhiteSpace(candidate.Album) ? "-" : candidate.Album;
        Score = candidate.Score;
        SyncCapability = candidate.SupportsWordSync ? "支持逐字" : "行级歌词";
    }

    public string Source { get; }

    public LyricsCandidate Candidate { get; }

    public string Title { get; }

    public string Artist { get; }

    public string Album { get; }

    public int Score { get; }

    public string SyncCapability { get; }
}
