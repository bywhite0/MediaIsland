using MediaIsland.Services;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Debugger;

static class Program
{
    static async Task Main(string[] args)
    {
        try
        {
            Console.WriteLine("MediaIsland Debugger Started.");

            var logger = new ConsoleLogger<MediaService>();
            var mediaService = new MediaService(logger);

            // Test Case 1 & 4: Title, Artist, Album AND Source on Media Change
            mediaService.OnMediaPropertiesChanged += (sender, info) =>
            {
                if (info == null) return;
                Console.WriteLine($"\n[Test Case 1] Metadata Changed:");
                Console.WriteLine($"Title:  {info.Title}");
                Console.WriteLine($"Artist: {info.Artist}");
                Console.WriteLine($"Album:  {info.AlbumTitle}");

                Console.WriteLine($"\n[Test Case 4] Source Info:");
                Console.WriteLine($"Source: {info.SourceApp}");
            };

            // Test Case 2: Timeline
            mediaService.OnTimelinePropertyChanged += (sender, args) =>
            {
                Console.WriteLine($"\n[Test Case 2] Timeline Updated:");
                Console.WriteLine($"Position: {args.Position}");
                Console.WriteLine($"End:      {args.EndTime}");
            };

            // Test Case 3: Status
            mediaService.OnPlaybackStateChanged += (sender, args) =>
            {
                Console.WriteLine($"\n[Test Case 3] Playback Status Changed:");
                Console.WriteLine($"Status: {args.PlaybackStatus}");
            };

            Console.WriteLine("Starting MediaService...");
            await mediaService.StartAsync();

            Console.WriteLine("MediaService started. Waiting for events... Press Enter to exit.");
            Console.ReadLine();

            mediaService.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine("FATAL ERROR:");
            Console.WriteLine(ex);
        }
    }
}

public class ConsoleLogger<T> : ILogger<T>
{
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Console.WriteLine($"[{logLevel}] {formatter(state, exception)}");
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => new NoopDisposable();

    private class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}