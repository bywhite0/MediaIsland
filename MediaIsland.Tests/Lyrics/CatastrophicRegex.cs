using System.Text.RegularExpressions;
using MediaIsland.Services.Lyrics.Cleanup;

namespace MediaIsland.Tests.Lyrics;

/// <summary>在 <see cref="Input"/> 上必然超时的灾难回溯正则，超时取生产值。</summary>
internal static class CatastrophicRegex
{
    public static readonly string Input = new string('a', 40) + "!";

    public static Regex Create() => new("^(a|aa)+$", RegexOptions.None, LyricsCleanupOptions.RegexTimeout);
}
