using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.Lyrics.Storage;
using Xunit;

namespace MediaIsland.Tests.Lyrics;

public class LyricsStoreWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MediaIslandStoreWiringTests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>存储必须懒建目录：插件加载时不该在用户配置目录里凭空造出空文件夹。</summary>
    [Fact]
    public void Constructor_DoesNotCreateFoldersEagerly()
    {
        _ = new LyricsFileStore(Path.Combine(_root, "Lyrics"));

        Assert.False(Directory.Exists(Path.Combine(_root, "Lyrics")));
    }

    [Fact]
    public async Task FirstWrite_CreatesCacheAndPinFolders()
    {
        var store = new LyricsFileStore(Path.Combine(_root, "Lyrics"));

        await store.SaveCacheAsync("key-1", CreateEntry(), CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_root, "Lyrics", "cache")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Lyrics", "pins")));
    }

    private static StoredLyrics CreateEntry() =>
        StoredLyrics.FromPayload(
            new LyricsPayload(
                LyricsFormat.Lrc,
                "[00:01.00]Hello",
                LyricsSourceId.Netease,
                "song-1",
                new LyricsMetadata("A", "B", "C", TimeSpan.FromSeconds(60))),
            "A",
            "B",
            "C",
            TimeSpan.FromSeconds(60),
            "fp",
            DateTimeOffset.UtcNow);
}
