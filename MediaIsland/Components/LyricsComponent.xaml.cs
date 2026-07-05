using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Parsers;
using Lyricify.Lyrics.Providers.Web.Netease;
using Lyricify.Lyrics.Searchers;
using MaterialDesignThemes.Wpf;
using MediaIsland.Models;
using MediaIsland.Services;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;

namespace MediaIsland.Components;

[ComponentInfo(
    "A681FD00-04F7-4E7B-9236-FAC85780D518",
    "实时歌词",
    PackIconKind.MusicNote,
    "根据当前播放媒体搜索并显示同步歌词。"
)]
public partial class LyricsComponent : ComponentBase<LyricsComponentConfig>
{
    private static readonly HttpClient NeteaseHttpClient = new();

    private readonly IMediaService _mediaService;
    private readonly ILogger<LyricsComponent> _logger;
    private readonly NeteaseSearcher _searcher = new();
    private readonly DispatcherTimer _lyricsTimer;
    private readonly object _syncLock = new();

    private LyricsData? _currentLyrics;
    private string? _currentLine;
    private string? _lastTitle;
    private string? _lastArtist;
    private TimeSpan _lastSmtcPosition;
    private DateTime _lastSmtcUpdateTime = DateTime.Now;
    private bool _isPlaying;
    private double _playbackRate = 1.0;
    private int _searchVersion;

    public LyricsComponent(IMediaService mediaService, ILogger<LyricsComponent> logger)
    {
        InitializeComponent();
        _mediaService = mediaService;
        _logger = logger;
        _lyricsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _lyricsTimer.Tick += LyricsTimer_OnTick;
    }

    private async void LyricsComponent_OnLoaded(object sender, RoutedEventArgs e)
    {
        _mediaService.OnMediaPropertiesChanged += MediaService_OnMediaPropertiesChanged;
        _mediaService.OnTimelinePropertyChanged += MediaService_OnTimelinePropertyChanged;
        _mediaService.OnPlaybackStateChanged += MediaService_OnPlaybackStateChanged;
        _mediaService.OnFocusedSessionChanged += MediaService_OnFocusedSessionChanged;
        Settings.PropertyChanged += Settings_OnPropertyChanged;

        _lyricsTimer.Start();
        SetStatus("等待媒体信息...");
        await _mediaService.StartAsync();
        if (_mediaService.CurrentMediaInfo != null)
        {
            await HandleMediaInfoAsync(_mediaService.CurrentMediaInfo);
        }
    }

    private void LyricsComponent_OnUnloaded(object sender, RoutedEventArgs e)
    {
        _lyricsTimer.Stop();
        _mediaService.OnMediaPropertiesChanged -= MediaService_OnMediaPropertiesChanged;
        _mediaService.OnTimelinePropertyChanged -= MediaService_OnTimelinePropertyChanged;
        _mediaService.OnPlaybackStateChanged -= MediaService_OnPlaybackStateChanged;
        _mediaService.OnFocusedSessionChanged -= MediaService_OnFocusedSessionChanged;
        Settings.PropertyChanged -= Settings_OnPropertyChanged;
    }

    private void Settings_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LyricsComponentConfig.IsHideWhenEmpty) or nameof(LyricsComponentConfig.IsShowStatusText))
        {
            UpdateEmptyVisibility();
        }
    }

    private async void MediaService_OnMediaPropertiesChanged(object? sender, MediaInfo? info)
    {
        await HandleMediaInfoAsync(info);
    }

    private void MediaService_OnTimelinePropertyChanged(object? sender, GlobalSystemMediaTransportControlsSessionTimelineProperties timeline)
    {
        lock (_syncLock)
        {
            _lastSmtcPosition = timeline.Position;
            _lastSmtcUpdateTime = DateTime.Now;
        }
    }

    private void MediaService_OnPlaybackStateChanged(object? sender, GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo)
    {
        lock (_syncLock)
        {
            _isPlaying = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            _playbackRate = playbackInfo.PlaybackRate ?? 1.0;
        }

        _logger.LogInformation("[Lyrics] Playback status: {Status}", playbackInfo.PlaybackStatus);
    }

    private void MediaService_OnFocusedSessionChanged(object? sender, EventArgs e)
    {
        if (_mediaService.CurrentMediaInfo == null)
        {
            ClearLyrics("没有可用的媒体会话");
        }
    }

    private async Task HandleMediaInfoAsync(MediaInfo? info)
    {
        if (info == null)
        {
            ClearLyrics("没有可用的媒体会话");
            return;
        }

        lock (_syncLock)
        {
            _lastSmtcPosition = info.Position;
            _lastSmtcUpdateTime = DateTime.Now;
            _isPlaying = info.PlaybackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            _playbackRate = info.PlaybackInfo?.PlaybackRate ?? 1.0;
        }

        if (info.Title == _lastTitle && info.Artist == _lastArtist)
        {
            return;
        }

        _lastTitle = info.Title;
        _lastArtist = info.Artist;
        var version = Interlocked.Increment(ref _searchVersion);

        lock (_syncLock)
        {
            _currentLyrics = null;
            _currentLine = null;
        }

        _logger.LogInformation(
            "[Metadata] {Title} - {Artist} ({Album}), duration {Duration}",
            info.Title,
            info.Artist,
            info.AlbumTitle,
            info.Duration);
        _logger.LogInformation("[Source] {Source}", info.SourceApp);
        SetStatus($"正在查找歌词: {info.Title}");

        var lyrics = await SearchLyricsAsync(info);
        if (version != _searchVersion)
        {
            return;
        }

        lock (_syncLock)
        {
            _currentLyrics = lyrics;
            _currentLine = null;
        }

        if (lyrics == null)
        {
            SetStatus("未找到歌词");
        }
    }

    private async Task<LyricsData?> SearchLyricsAsync(MediaInfo info)
    {
        try
        {
            foreach (var query in BuildSearchQueries(info))
            {
                _logger.LogInformation("[Lyrics] Searching: {Query}", query);
                var directResult = await SearchNeteaseApiAsync(query, info);
                if (directResult != null)
                {
                    _logger.LogInformation(
                        "[Lyrics] Found: {Title} - {Artist} ({Duration}) - {Id}, score {Score}",
                        directResult.Title,
                        directResult.Artist,
                        directResult.Duration,
                        directResult.Id,
                        directResult.Score);
                    var directLyricsString = await GetNeteaseLyricsAsync(directResult.Id);
                    if (string.IsNullOrWhiteSpace(directLyricsString))
                    {
                        _logger.LogInformation("[Lyrics] Start parsing failed: No text found in response.");
                        continue;
                    }

                    return LrcParser.Parse(directLyricsString.AsSpan());
                }

                if (info.Duration > TimeSpan.Zero)
                {
                    continue;
                }

                var searchResults = await _searcher.SearchForResults(query);
                var result = searchResults?.OfType<NeteaseSearchResult>().FirstOrDefault();
                if (result == null || !IsTitleLikelyMatch(info.Title, result.Title))
                {
                    continue;
                }

                _logger.LogInformation("[Lyrics] Found by package fallback: {Title} - {Id}", result.Title, result.Id);
                var packageLyricsString = await GetNeteaseLyricsAsync(result.Id);
                if (string.IsNullOrWhiteSpace(packageLyricsString))
                {
                    _logger.LogInformation("[Lyrics] Start parsing failed: No text found in response.");
                    continue;
                }

                return LrcParser.Parse(packageLyricsString.AsSpan());
            }

            _logger.LogInformation("[Lyrics] Not found.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Lyrics] Error while searching lyrics.");
            return null;
        }
    }

    private static async Task<DirectNeteaseSearchResult?> SearchNeteaseApiAsync(string query, MediaInfo info)
    {
        var url = $"https://music.163.com/api/search/get/web?s={Uri.EscapeDataString(query)}&type=1&limit=10&offset=0";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        request.Headers.Referrer = new Uri("https://music.163.com/");

        using var response = await NeteaseHttpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await System.Text.Json.JsonDocument.ParseAsync(stream);
        if (!json.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("songs", out var songs) ||
            songs.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return null;
        }

        DirectNeteaseSearchResult? bestResult = null;
        foreach (var song in songs.EnumerateArray())
        {
            if (!song.TryGetProperty("id", out var idElement) ||
                !idElement.TryGetInt64(out var id) ||
                !song.TryGetProperty("name", out var nameElement))
            {
                continue;
            }

            var title = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var artist = ReadArtists(song);
            var album = ReadAlbum(song);
            var duration = ReadDuration(song);
            var aliases = ReadStringArray(song, "alias")
                .Concat(ReadStringArray(song, "transNames"))
                .ToArray();
            var score = ScoreNeteaseResult(info, title, aliases, artist, album, duration);
            var candidate = new DirectNeteaseSearchResult(id, title, artist, album, duration, score);
            if (bestResult == null || candidate.Score > bestResult.Score)
            {
                bestResult = candidate;
            }
        }

        if (bestResult == null)
        {
            return null;
        }

        var minimumScore = info.Duration > TimeSpan.Zero ? 80 : 60;
        if (bestResult.Score < minimumScore)
        {
            return null;
        }

        return bestResult;
    }

    private static int ScoreNeteaseResult(
        MediaInfo info,
        string title,
        IEnumerable<string> aliases,
        string artist,
        string album,
        TimeSpan duration)
    {
        var score = 0;
        var infoTitle = NormalizeComparableText(info.Title);
        var infoArtist = NormalizeComparableText(info.Artist);
        var infoAlbum = NormalizeComparableText(info.AlbumTitle);
        var candidateTitles = aliases
            .Append(title)
            .Select(NormalizeComparableText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var candidateTitle = NormalizeComparableText(title);
        var candidateArtist = NormalizeComparableText(artist);
        var candidateAlbum = NormalizeComparableText(album);

        if (candidateTitles.Any(value => value.Equals(infoTitle, StringComparison.OrdinalIgnoreCase)))
        {
            score += 80;
        }
        else if (candidateTitles.Any(value => value.Contains(infoTitle, StringComparison.OrdinalIgnoreCase) ||
                                             infoTitle.Contains(value, StringComparison.OrdinalIgnoreCase)))
        {
            score += 45;
        }
        else
        {
            score -= 70;
        }

        if (!string.IsNullOrWhiteSpace(infoArtist) && !string.IsNullOrWhiteSpace(candidateArtist))
        {
            if (candidateArtist.Equals(infoArtist, StringComparison.OrdinalIgnoreCase))
            {
                score += 35;
            }
            else if (candidateArtist.Contains(infoArtist, StringComparison.OrdinalIgnoreCase) ||
                     infoArtist.Contains(candidateArtist, StringComparison.OrdinalIgnoreCase))
            {
                score += 22;
            }
        }

        if (!string.IsNullOrWhiteSpace(infoAlbum) && !string.IsNullOrWhiteSpace(candidateAlbum))
        {
            if (candidateAlbum.Equals(infoAlbum, StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }
            else if (candidateAlbum.Contains(infoAlbum, StringComparison.OrdinalIgnoreCase) ||
                     infoAlbum.Contains(candidateAlbum, StringComparison.OrdinalIgnoreCase))
            {
                score += 12;
            }
        }

        if (info.Duration > TimeSpan.Zero && duration > TimeSpan.Zero)
        {
            var durationDiff = Math.Abs((info.Duration - duration).TotalSeconds);
            score += durationDiff switch
            {
                <= 2 => 45,
                <= 5 => 35,
                <= 10 => 25,
                <= 20 => 10,
                _ => -80
            };
        }

        if (!LooksLikeSameVersion(infoTitle, candidateTitle))
        {
            score -= 25;
        }

        return score;
    }

    private static bool LooksLikeSameVersion(string infoTitle, string candidateTitle)
    {
        var versionWords = new[] { "live", "remix", "cover", "instrumental", "karaoke", "伴奏", "纯音乐" };
        foreach (var word in versionWords)
        {
            var infoHasWord = infoTitle.Contains(word, StringComparison.OrdinalIgnoreCase);
            var candidateHasWord = candidateTitle.Contains(word, StringComparison.OrdinalIgnoreCase);
            if (infoHasWord != candidateHasWord)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTitleLikelyMatch(string expectedTitle, string candidateTitle)
    {
        var expected = NormalizeComparableText(expectedTitle);
        var candidate = NormalizeComparableText(candidateTitle);
        return !string.IsNullOrWhiteSpace(expected) &&
               (candidate.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                candidate.Contains(expected, StringComparison.OrdinalIgnoreCase) ||
                expected.Contains(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadArtists(System.Text.Json.JsonElement song)
    {
        if (!song.TryGetProperty("artists", out var artists) ||
            artists.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            artists.EnumerateArray()
                .Select(artist => artist.TryGetProperty("name", out var name) ? name.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name)));
    }

    private static string ReadAlbum(System.Text.Json.JsonElement song)
    {
        return song.TryGetProperty("album", out var album) &&
               album.TryGetProperty("name", out var name)
            ? name.GetString() ?? string.Empty
            : string.Empty;
    }

    private static TimeSpan ReadDuration(System.Text.Json.JsonElement song)
    {
        return song.TryGetProperty("duration", out var durationElement) &&
               durationElement.TryGetInt64(out var duration)
            ? TimeSpan.FromMilliseconds(duration)
            : TimeSpan.Zero;
    }

    private static IEnumerable<string> ReadStringArray(System.Text.Json.JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var value in property.EnumerateArray())
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return text;
            }
        }
    }

    private static IEnumerable<string> BuildSearchQueries(MediaInfo info)
    {
        var title = NormalizeSearchText(info.Title);
        var artist = NormalizeSearchText(info.Artist);
        var album = NormalizeSearchText(info.AlbumTitle);

        if (string.IsNullOrWhiteSpace(title))
        {
            yield break;
        }

        var queries = new[]
        {
            string.IsNullOrWhiteSpace(artist) ? null : $"{title} - {artist}",
            string.IsNullOrWhiteSpace(artist) ? null : $"{title} {artist}",
            string.IsNullOrWhiteSpace(artist) ? null : $"{artist} - {title} {album}",
            string.IsNullOrWhiteSpace(album) ? null : $"{title} {album}",
            title
        };

        var usedQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var query in queries)
        {
            var normalizedQuery = NormalizeSearchText(query);
            if (!string.IsNullOrWhiteSpace(normalizedQuery) && usedQueries.Add(normalizedQuery))
            {
                yield return normalizedQuery;
            }
        }
    }

    private static string NormalizeSearchText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            text.Replace('/', ' ')
                .Replace('\\', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string NormalizeComparableText(string? text)
    {
        return NormalizeSearchText(NormalizeSearchText(text)
            .Replace("　", " ")
            .Replace("・", " ")
            .Replace("-", " ")
            .Replace("_", " ")
            .Replace("(", " ")
            .Replace(")", " ")
            .Replace("[", " ")
            .Replace("]", " "));
    }

    private sealed record DirectNeteaseSearchResult(
        long Id,
        string Title,
        string Artist,
        string Album,
        TimeSpan Duration,
        int Score);

    private static async Task<string?> GetNeteaseLyricsAsync(object id)
    {
        dynamic api = new Api();
        dynamic response = await api.GetLyric(id.ToString());

        try
        {
            string? lyric = response?.Lrc?.Lyric;
            if (!string.IsNullOrWhiteSpace(lyric))
            {
                return lyric;
            }
        }
        catch
        {
            // Different provider versions expose different response shapes.
        }

        try
        {
            string? lyric = response?.Lyric;
            if (!string.IsNullOrWhiteSpace(lyric))
            {
                return lyric;
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private void LyricsTimer_OnTick(object? sender, EventArgs e)
    {
        LyricsData? lyrics;
        double currentMs;

        lock (_syncLock)
        {
            lyrics = _currentLyrics;
            if (lyrics == null)
            {
                return;
            }

            currentMs = _lastSmtcPosition.TotalMilliseconds;
            if (_isPlaying)
            {
                var elapsed = DateTime.Now - _lastSmtcUpdateTime;
                currentMs += elapsed.TotalMilliseconds * _playbackRate;
            }
        }

        var line = lyrics.Lines?
            .Where(lineInfo => lineInfo.StartTime <= currentMs)
            .OrderByDescending(lineInfo => lineInfo.StartTime)
            .FirstOrDefault();

        if (line == null || line.Text == _currentLine)
        {
            return;
        }

        _currentLine = line.Text;
        if (string.IsNullOrWhiteSpace(line.Text))
        {
            SetStatus(string.Empty);
            return;
        }

        _logger.LogInformation("[Lyrics] {Line}", line.Text);
        SetText(line.Text, isStatusText: false);
    }

    private void ClearLyrics(string status)
    {
        Interlocked.Increment(ref _searchVersion);
        _lastTitle = null;
        _lastArtist = null;

        lock (_syncLock)
        {
            _currentLyrics = null;
            _currentLine = null;
            _lastSmtcPosition = TimeSpan.Zero;
            _lastSmtcUpdateTime = DateTime.Now;
            _isPlaying = false;
            _playbackRate = 1.0;
        }

        SetStatus(status);
    }

    private void SetStatus(string text)
    {
        SetText(Settings.IsShowStatusText ? text : string.Empty, isStatusText: true);
    }

    private void SetText(string text, bool isStatusText)
    {
        Dispatcher.InvokeAsync(() =>
        {
            lyricsText.Text = text;
            lyricsText.Opacity = isStatusText ? 0.72 : 1.0;
            UpdateEmptyVisibility();
        });
    }

    private void UpdateEmptyVisibility()
    {
        LyricsGrid.Visibility = Settings.IsHideWhenEmpty && string.IsNullOrWhiteSpace(lyricsText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
