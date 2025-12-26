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

            mediaService.OnMediaPropertiesChanged += (sender, info) =>
            {
                if (info == null)
                {
                    Console.WriteLine("\n[Media Info] No media playing or info unavailable.");
                    return;
                }

                Console.WriteLine($"\n[Media Info] Update at {DateTime.Now:T}");
                Console.WriteLine($"Title:    {info.Title}");
                Console.WriteLine($"Artist:   {info.Artist}");
                Console.WriteLine($"Album:    {info.AlbumTitle}");
                Console.WriteLine($"Timeline: {info.Position} / {info.Duration}");
                Console.WriteLine($"Status:   {info.PlaybackInfo?.PlaybackStatus}");
                Console.WriteLine($"Source:   {info.SourceApp}");
            };

            Console.WriteLine("Starting MediaService...");
            await mediaService.StartAsync();

            Console.WriteLine("MediaService started. Press Enter to exit.");
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