using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Services;

/// <summary>
/// 插件加载输出。
/// <para>
/// ASCII 标志在 <see cref="Plugin.Initialize"/> 时以主题色直接写控制台：此时宿主尚未构建、异步控制台日志
/// 还没开始输出，标志因此稳定排在本插件任何日志之前。标志不走 ILogger——颜色转义码写进日志文件与
/// 应用内日志会成乱码，那里只需要版本这一条事实。
/// </para>
/// <para>
/// 版本行与加载期暂存的消息（加载时拿不到 ILogger）在宿主启动后经 ILogger 统一输出。
/// </para>
/// </summary>
internal sealed partial class PluginStartupLog(string version) : IHostedService
{
    internal static readonly string[] BannerLines =
    [
        @" __  __        _ _      ___    _              _ ",
        @"|  \/  |___ __| (_)__ _|_ _|__| |__ _ _ _  __| |",
        @"| |\/| / -_) _` | / _` || |(_-< / _` | ' \/ _` |",
        @"|_|  |_\___\__,_|_\__,_|___/__/_\__,_|_||_\__,_|",
    ];

    // 主题色 #00B0F0，取自当前图标的主色。图标只是临时沿用 ClassIsland 图标微调，重新设计后同步修改。
    internal const string ThemeColorAnsi = "\u001b[38;2;0;176;240m";

    private const string ResetAnsi = "\u001b[0m";

    private readonly List<(LogLevel Level, string Message, Exception? Exception)> _pending = [];

    public ILogger? Logger { get; set; }

    public void Defer(LogLevel level, string message, Exception? exception = null) =>
        _pending.Add((level, message, exception));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Logger?.LogInformation("MediaIsland v{Version} 已加载", version);
        foreach (var (level, message, exception) in _pending)
        {
            Logger?.Log(level, exception, "{Message}", message);
        }

        _pending.Clear();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>向控制台输出标志。控制台不可用时静默跳过，不影响插件加载。</summary>
    public static void WriteBanner()
    {
        try
        {
            var useColor = ShouldUseColor(Environment.GetEnvironmentVariable("NO_COLOR"));
            if (useColor)
            {
                TryEnableVirtualTerminal();
            }

            WriteBanner(Console.Out, useColor);
        }
        catch (Exception)
        {
            // 标志只是装饰，任何控制台异常都不应阻断插件加载。
        }
    }

    internal static void WriteBanner(TextWriter writer, bool useColor) =>
        writer.WriteLine(ComposeBanner(useColor));

    // 每行各自上色并复位：即使别的输出插在两行之间，颜色也不会串到别人的行上。
    internal static string ComposeBanner(bool useColor) =>
        string.Join(Environment.NewLine,
            useColor ? BannerLines.Select(line => ThemeColorAnsi + line + ResetAnsi) : BannerLines);

    // 遵循 NO_COLOR 约定（https://no-color.org）：设了非空值就不输出颜色，与 ClassIsland 所用的 Pastel 一致。
    internal static bool ShouldUseColor(string? noColor) => string.IsNullOrEmpty(noColor);

    // ClassIsland 首次经日志格式器输出时才由 Pastel 开启 VT 处理，插件加载时尚未开启；
    // 不先开启的话，传统控制台会把转义码原样打出来。输出被重定向时句柄不是控制台，调用失败即跳过。
    private static void TryEnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = GetStdHandle(StdOutputHandle);
        if (GetConsoleMode(handle, out var mode))
        {
            SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
    }

    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int stdHandle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint handle, uint mode);
}
