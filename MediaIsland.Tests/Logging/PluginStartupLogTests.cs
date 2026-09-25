using MediaIsland.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MediaIsland.Tests.Logging;

/// <summary>
/// 插件加载输出：ASCII 标志以主题色直接写控制台（加载最早期，排在本插件任何日志之前）；
/// 版本行与加载期暂存的消息在宿主启动后经 ILogger 输出，进入日志文件与应用内日志。
/// </summary>
public class PluginStartupLogTests
{
    private const string Esc = "\u001b";

    [Fact]
    public void PlainBanner_IsPrintableAsciiOnly()
    {
        var banner = PluginStartupLog.ComposeBanner(useColor: false);

        Assert.All(banner, c => Assert.True(c is '\r' or '\n' || c is >= ' ' and <= '~', $"非 ASCII 可打印字符：U+{(int)c:X4}"));
        Assert.Equal(PluginStartupLog.BannerLines, banner.Split(Environment.NewLine));
    }

    /// <summary>
    /// 每行各自上色并复位：即使别的输出插在两行之间，颜色也不会串到别人的行上。
    /// </summary>
    [Fact]
    public void ColoredBanner_WrapsEveryLineInThemeColorAndReset()
    {
        var lines = PluginStartupLog.ComposeBanner(useColor: true).Split(Environment.NewLine);

        Assert.Equal(PluginStartupLog.BannerLines.Length, lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            Assert.Equal($"{PluginStartupLog.ThemeColorAnsi}{PluginStartupLog.BannerLines[i]}{Esc}[0m", lines[i]);
        }
    }

    /// <summary>
    /// 主题色是 24 位真彩色前景转义序列；具体色值随图标重新设计而变，这里只约束格式。
    /// </summary>
    [Fact]
    public void ThemeColor_IsTrueColorForegroundSequence()
    {
        Assert.Matches(@"^\u001b\[38;2;\d{1,3};\d{1,3};\d{1,3}m$", PluginStartupLog.ThemeColorAnsi);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", false)]   // 遵循 NO_COLOR 约定：只要设了非空值就不上色
    [InlineData("0", false)]
    public void UseColor_FollowsNoColorConvention(string? noColor, bool expected)
    {
        Assert.Equal(expected, PluginStartupLog.ShouldUseColor(noColor));
    }

    [Fact]
    public void WriteBanner_WritesTheBannerFollowedByNewLine()
    {
        var writer = new StringWriter();

        PluginStartupLog.WriteBanner(writer, useColor: false);

        Assert.Equal(PluginStartupLog.ComposeBanner(useColor: false) + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public async Task Start_LogsVersionLineWithoutTagOrBanner()
    {
        var logger = new ListLogger();
        var startupLog = new PluginStartupLog("1.2.3.4") { Logger = logger };

        await startupLog.StartAsync(CancellationToken.None);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        // 分类已是 PluginStartupLog，再加 [插件] 标签是重复信息；标志只进控制台，不进日志。
        Assert.Equal("MediaIsland v1.2.3.4 已加载", entry.Message);
    }

    [Fact]
    public async Task Start_FlushesDeferredMessagesAfterVersionLineInOrder()
    {
        var logger = new ListLogger();
        var startupLog = new PluginStartupLog("1.0.0") { Logger = logger };
        var failure = new InvalidOperationException("boom");
        startupLog.Defer(LogLevel.Information, "第一条");
        startupLog.Defer(LogLevel.Error, "第二条", failure);

        await startupLog.StartAsync(CancellationToken.None);

        Assert.Equal(3, logger.Entries.Count);
        Assert.Equal("MediaIsland v1.0.0 已加载", logger.Entries[0].Message);
        Assert.Equal((LogLevel.Information, "第一条", (Exception?)null), logger.Entries[1]);
        Assert.Equal((LogLevel.Error, "第二条", (Exception?)failure), logger.Entries[2]);
    }

    /// <summary>
    /// 暂存消息只补记一次：同一实例若被再次启动，不应重复输出旧消息。
    /// </summary>
    [Fact]
    public async Task Start_DoesNotReplayDeferredMessagesTwice()
    {
        var logger = new ListLogger();
        var startupLog = new PluginStartupLog("1.0.0") { Logger = logger };
        startupLog.Defer(LogLevel.Warning, "只出现一次");

        await startupLog.StartAsync(CancellationToken.None);
        await startupLog.StartAsync(CancellationToken.None);

        Assert.Single(logger.Entries, e => e.Message == "只出现一次");
    }

    [Fact]
    public async Task Start_WithoutLogger_DoesNotThrow()
    {
        var startupLog = new PluginStartupLog("1.0.0");
        startupLog.Defer(LogLevel.Error, "无人接收");

        await startupLog.StartAsync(CancellationToken.None);
        await startupLog.StopAsync(CancellationToken.None);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
