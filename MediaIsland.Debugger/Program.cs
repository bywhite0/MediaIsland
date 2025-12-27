using MediaIsland.Services;
using Microsoft.Extensions.Logging;
using Lyricify.Lyrics.Searchers;
using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Parsers;
using System.Text;
using System.Text.Json;

namespace MediaIsland.Debugger;

static class Program
{
    private static readonly HttpClient client = new HttpClient();

    static async Task Main(string[] args)
    {
        bool showLyricsOnly = args.Contains("--lyrics");
        LyricsData? currentLyrics = null;
        string? currentLine = null;
        

        if (!showLyricsOnly)
        {
            Console.WriteLine("MediaIsland Debugger Started.");
            Console.WriteLine("Starting MediaService...");
        }

        try
        {
            var logger = new ConsoleLogger<MediaService>();
            var mediaService = new MediaService(logger);

            var searcher = new NeteaseSearcher();

            // Timer State
            TimeSpan lastSmtcPosition = TimeSpan.Zero;
            DateTime lastSmtcUpdateTime = DateTime.Now;
            bool isPlaying = false;
            double playbackRate = 1.0;
            object syncLock = new object();

            string? lastTitle = null;
            string? lastArtist = null;

            mediaService.OnMediaPropertiesChanged += async (sender, info) =>
            {
                if (info == null) return;

                // Only update lyrics if the song has actually changed
                if (info.Title == lastTitle && info.Artist == lastArtist) return;

                lastTitle = info.Title;
                lastArtist = info.Artist;

                lock (syncLock) { currentLyrics = null; currentLine = null; }

                if (!showLyricsOnly)
                {
                    Console.WriteLine($"\n[Metadata] {info.Title} - {info.Artist} ({info.AlbumTitle})");
                    Console.WriteLine($"[Source]   {info.SourceApp}");
                }

                try
                {
                    var query = $"{info.Title} - {info.Artist.Replace("/", "")}";
                    var searchResults = await searcher.SearchForResults(query);

                    if (searchResults is null or { Count: 0 })
                    {
                        query = $"{info.Artist} - {info.Title} {info.AlbumTitle}";
                        searchResults = await searcher.SearchForResults(query);
                    }

                    if (searchResults is { Count: > 0 })
                    {
                        var result = searchResults.First() as NeteaseSearchResult;
                        if (result != null)
                        {
                            if (!showLyricsOnly) Console.WriteLine($"[Lyrics] Found: {result.Title} - {result.Id}");

                            dynamic api = new Lyricify.Lyrics.Providers.Web.Netease.Api();
                            var response = await api.GetLyric(result.Id);

                            string? lyricsString = null;
                            try
                            {
                                if (response != null)
                                {
                                    try { lyricsString = response.Lrc.Lyric; } catch { }
                                    if (string.IsNullOrEmpty(lyricsString)) try { lyricsString = response.Lyric; } catch { }
                                }
                            }
                            catch { }

                            if (!string.IsNullOrEmpty(lyricsString))
                            {
                                lock (syncLock)
                                {
                                    currentLyrics = LrcParser.Parse(lyricsString.AsSpan());
                                }
                            }
                            else
                            {
                                if (!showLyricsOnly) Console.WriteLine("[Lyrics] Start parsing failed: No text found in response.");
                            }
                        }
                    }
                    else
                    {
                        if (!showLyricsOnly) Console.WriteLine("[Lyrics] Not found.");
                    }
                }
                catch (Exception ex)
                {
                    if (!showLyricsOnly) Console.WriteLine($"[Lyrics] Error: {ex.Message}");
                }
            };

            mediaService.OnTimelinePropertyChanged += (sender, args) =>
            {
                lock (syncLock)
                {
                    lastSmtcPosition = args.Position;
                    lastSmtcUpdateTime = DateTime.Now;
                }
            };

            mediaService.OnPlaybackStateChanged += (sender, args) =>
            {
                lock (syncLock)
                {
                    isPlaying = args.PlaybackStatus == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    playbackRate = args.PlaybackRate ?? 1.0;
                }
                if (!showLyricsOnly)
                {
                    Console.WriteLine($"[Status] {args.PlaybackStatus}");
                }
            };

            await mediaService.StartAsync();

            if (!showLyricsOnly) Console.WriteLine("MediaService started. Waiting for events... Press Enter to exit.");

            // Simulated Timer Loop
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        await Task.Delay(50);

                        double currentMs = 0;
                        LyricsData? lyrics = null;

                        lock (syncLock)
                        {
                            if (currentLyrics == null) continue;
                            lyrics = currentLyrics;

                            if (isPlaying)
                            {
                                var elapsed = DateTime.Now - lastSmtcUpdateTime;
                                currentMs = lastSmtcPosition.TotalMilliseconds + (elapsed.TotalMilliseconds * playbackRate);
                            }
                            else
                            {
                                currentMs = lastSmtcPosition.TotalMilliseconds;
                            }
                        }

                        if (lyrics != null && lyrics.Lines != null)
                        {
                            var lineObj = lyrics.Lines
                                .Where(l => l.StartTime <= currentMs)
                                .OrderByDescending(l => l.StartTime)
                                .FirstOrDefault();

                            if (lineObj != null)
                            {
                                string lineContent = lineObj.Text;
                                if (lineContent != currentLine)
                                {
                                    currentLine = lineContent;
                                    if (!string.IsNullOrEmpty(lineContent))
                                    {
                                        Console.WriteLine(lineContent);
                                        _ = PostLyric(lineContent);
                                    }
                                }
                            }
                        }
                    }
                    catch { /* Ignore timer errors */ }
                }
            });

            Console.ReadLine();

            mediaService.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    private static async Task PostLyric(string lyric)
    {
        try
        {
            var json = JsonSerializer.Serialize(new { lyric, extra = "" });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            await client.PostAsync("http://127.0.0.1:50063/component/lyrics/lyrics/", content);
        }
        catch { }
    }
}
public class ConsoleLogger<T> : ILogger<T>
{
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    public bool IsEnabled(LogLevel logLevel) => true;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
}