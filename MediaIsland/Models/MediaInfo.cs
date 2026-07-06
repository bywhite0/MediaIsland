using System;
using System.Windows.Media;
using Windows.Media.Control;

namespace MediaIsland.Models;

public class MediaInfo(
    string title,
    string artist,
    string albumTitle,
    TimeSpan position,
    TimeSpan duration,
    string sourceApp,
    GlobalSystemMediaTransportControlsSessionPlaybackInfo? playbackInfo,
    ImageSource? thumbnail)
{
    public string Title { get; init; } = title;
    public string Artist { get; init; } = artist;
    public string AlbumTitle { get; init; } = albumTitle;
    public TimeSpan Position { get; init; } = position;
    public TimeSpan Duration { get; init; } = duration;
    public string SourceApp { get; init; } = sourceApp;
    public GlobalSystemMediaTransportControlsSessionPlaybackInfo? PlaybackInfo { get; init; } = playbackInfo;
    public ImageSource? Thumbnail { get; init; } = thumbnail;
}
