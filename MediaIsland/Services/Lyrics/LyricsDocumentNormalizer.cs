using Lyricify.Lyrics.Models;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Services.Lyrics;

public static class LyricsDocumentNormalizer
{
    public static LyricsDocument FromLyricify(
        LyricsData data,
        LyricsMetadata metadata,
        LyricsSourceId source,
        string providerItemId,
        LyricsFormat format,
        bool preferWordSync,
        IReadOnlyList<string>? translations = null)
    {
        var lines = new List<LyricsLine>();
        var sourceLines = data.Lines ?? [];
        for (var i = 0; i < sourceLines.Count; i++)
        {
            var line = sourceLines[i];
            var start = Clamp(FromMilliseconds(line.StartTime));
            var end = line.EndTime.HasValue
                ? Clamp(FromMilliseconds(line.EndTime))
                : TimeSpan.Zero;
            if (end <= start)
            {
                end = i + 1 < sourceLines.Count && sourceLines[i + 1].StartTime.HasValue
                    ? Clamp(FromMilliseconds(sourceLines[i + 1].StartTime))
                    : (metadata.Duration ?? start);
                if (end < start)
                {
                    end = start;
                }
            }

            var words = new List<LyricsWord>();
            if (preferWordSync && line is SyllableLineInfo syllableLine && syllableLine.Syllables is { Count: > 0 })
            {
                for (var wordIndex = 0; wordIndex < syllableLine.Syllables.Count; wordIndex++)
                {
                    var syllable = syllableLine.Syllables[wordIndex];
                    var wordStart = Clamp(TimeSpan.FromMilliseconds(syllable.StartTime));
                    var wordEnd = Clamp(TimeSpan.FromMilliseconds(syllable.EndTime));
                    if (wordEnd < wordStart)
                    {
                        wordEnd = wordIndex + 1 < syllableLine.Syllables.Count
                            ? Clamp(TimeSpan.FromMilliseconds(syllableLine.Syllables[wordIndex + 1].StartTime))
                            : end;
                    }

                    if (wordEnd < wordStart)
                    {
                        wordEnd = wordStart;
                    }

                    words.Add(new LyricsWord(wordStart, wordEnd, syllable.Text ?? string.Empty));
                }
            }

            var text = line.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text) && words.Count > 0)
            {
                text = string.Concat(words.Select(word => word.Text));
            }

            string? translation = null;
            if (translations != null && i < translations.Count)
            {
                translation = translations[i];
            }
            else if (line.SubLine != null && !string.IsNullOrWhiteSpace(line.SubLine.Text))
            {
                translation = line.SubLine.Text;
            }

            lines.Add(new LyricsLine(
                start,
                end,
                text,
                words,
                translation));
        }

        lines = NormalizeLines(lines, metadata.Duration).ToList();
        var syncMode = preferWordSync && lines.Any(line => line.Words.Count > 0)
            ? LyricsSyncMode.Word
            : lines.Count > 0
                ? LyricsSyncMode.Line
                : LyricsSyncMode.Unsynced;

        if (!preferWordSync)
        {
            lines = lines.Select(line => line with { Words = Array.Empty<LyricsWord>() }).ToList();
            syncMode = lines.Count > 0 ? LyricsSyncMode.Line : LyricsSyncMode.Unsynced;
        }

        return new LyricsDocument(metadata, lines, syncMode, source, providerItemId, format);
    }

    public static LyricsDocument Create(
        IEnumerable<LyricsLine> lines,
        LyricsMetadata metadata,
        LyricsSourceId source,
        string providerItemId,
        LyricsFormat format,
        bool preferWordSync)
    {
        var normalized = NormalizeLines(lines, metadata.Duration).ToList();
        if (!preferWordSync)
        {
            normalized = normalized.Select(line => line with { Words = Array.Empty<LyricsWord>() }).ToList();
        }

        var syncMode = preferWordSync && normalized.Any(line => line.Words.Count > 0)
            ? LyricsSyncMode.Word
            : normalized.Count > 0
                ? LyricsSyncMode.Line
                : LyricsSyncMode.Unsynced;

        return new LyricsDocument(metadata, normalized, syncMode, source, providerItemId, format);
    }

    public static IReadOnlyList<LyricsLine> NormalizeLines(IEnumerable<LyricsLine> lines, TimeSpan? trackDuration)
    {
        var ordered = lines
            .Select(line => line with
            {
                StartTime = Clamp(line.StartTime),
                EndTime = Clamp(line.EndTime),
                Words = line.Words
                    .Select(word => new LyricsWord(Clamp(word.StartTime), Clamp(word.EndTime), word.Text ?? string.Empty))
                    .OrderBy(word => word.StartTime)
                    .ThenBy(word => word.EndTime)
                    .ToArray()
            })
            .OrderBy(line => line.StartTime)
            .ThenBy(line => line.EndTime)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var line = ordered[i];
            var end = line.EndTime;
            if (end <= line.StartTime)
            {
                end = i + 1 < ordered.Count
                    ? ordered[i + 1].StartTime
                    : trackDuration ?? line.StartTime;
                if (end < line.StartTime)
                {
                    end = line.StartTime;
                }
            }

            var words = new List<LyricsWord>();
            for (var wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
            {
                var word = line.Words[wordIndex];
                var wordEnd = word.EndTime;
                if (wordEnd < word.StartTime)
                {
                    wordEnd = wordIndex + 1 < line.Words.Count
                        ? line.Words[wordIndex + 1].StartTime
                        : end;
                }

                if (wordEnd < word.StartTime)
                {
                    wordEnd = word.StartTime;
                }

                words.Add(word with { EndTime = wordEnd });
            }

            ordered[i] = line with { EndTime = end, Words = words };
        }

        return ordered;
    }

    private static TimeSpan FromMilliseconds(int? value) =>
        value.HasValue ? TimeSpan.FromMilliseconds(Math.Max(0, value.Value)) : TimeSpan.Zero;

    private static TimeSpan Clamp(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
