using MediaIsland.Models;

namespace MediaIsland.Helpers;

public static class MediaSourceFilter
{
    public static bool IsEnabled(string sourceApp, IEnumerable<MediaSource>? sources)
    {
        foreach (var source in sources ?? [])
        {
            if (source != null && string.Equals(sourceApp, source.Source, StringComparison.Ordinal))
            {
                return source.IsEnabled;
            }
        }

        return true;
    }

    public static bool IsLyricsSearchEnabled(string sourceApp, IEnumerable<MediaSource>? sources)
    {
        foreach (var source in sources ?? [])
        {
            if (source != null && string.Equals(sourceApp, source.Source, StringComparison.Ordinal))
            {
                return source.IsLyricsSearchEnabled;
            }
        }

        return MediaSource.IsLyricsSearchEnabledByDefault(sourceApp);
    }
}
