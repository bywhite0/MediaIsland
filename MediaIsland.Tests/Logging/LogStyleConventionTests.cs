using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace MediaIsland.Tests.Logging;

/// <summary>
/// 插件日志统一样式的守护测试：扫描插件源码，逐条检查日志调用。
/// <list type="bullet">
/// <item>消息模板以 <c>[域]</c> 或 <c>[域:子域]</c> 开头，域取自 <see cref="Domains"/>；</item>
/// <item>模板是字面量且不插值，参数一律走结构化占位符；</item>
/// <item>句末不带「。」；</item>
/// <item>异常作为 exception 参数传入，不把 <c>ex.Message</c> 当普通参数（会丢堆栈）；</item>
/// <item>不用 Console 输出——它不进 ClassIsland 的日志文件与应用内日志。</item>
/// </list>
/// 豁免见 <see cref="ExemptFiles"/>。
/// </summary>
public class LogStyleConventionTests
{
    // 域标签标明日志的主题，用来把分散在多个类里的同主题日志归到一起；
    // 分类（类名）已标明日志出自哪里，只出自单个类的主题不设标签，例如插件加载。
    private static readonly string[] Domains = ["媒体", "歌词", "音频", "MediaLink"];

    // 插件加载输出：标志带颜色转义码，只能直接写控制台（写进日志文件与应用内日志会成乱码）；
    // 版本行只出自这一个类，分类已说明一切，不加域标签。
    private static readonly string[] ExemptFiles = [Path.Combine("MediaIsland", "Services", "PluginStartupLog.cs")];

    private static readonly string[] SourceDirectories = ["MediaIsland", "MediaIsland.Windows"];

    private const string Levels = "Trace|Debug|Information|Warning|Error|Critical";

    private static readonly Regex AnyLogCall = new($@"\.Log(?:{Levels})\(");

    private static readonly Regex LiteralLogCall = new(
        $@"\.Log(?:{Levels})\(\s*(?:[\w?.]+\s*,\s*)?(?<interp>\$?)""(?<head>(?:[^""\\]|\\.)*)""(?<tail>(?:\s*\+\s*""(?:[^""\\]|\\.)*"")*)");

    private static readonly Regex TailLiteral = new(@"""((?:[^""\\]|\\.)*)""");

    private static readonly Regex Prefix = new($@"^\[(?:{string.Join('|', Domains)})(?::[^\]\s]+)?\] \S");

    private static readonly Regex ExceptionMessageArgument = new(
        $@"\.Log(?:{Levels})\((?:(?!\);).)*?\bex\.Message\b", RegexOptions.Singleline);

    private static readonly Regex ConsoleOutput = new(@"\bConsole\.(?:Write|WriteLine|Error\.)");

    [Fact]
    public void ExemptFiles_StillExist()
    {
        // 豁免项指向的文件若被改名或删除，豁免就成了死条目，应一并清理。
        var root = RepositoryRoot();
        Assert.All(ExemptFiles, f => Assert.True(File.Exists(Path.Combine(root, f)), f));
    }

    [Fact]
    public void Scanner_FindsLogCalls()
    {
        // 防止路径或正则失效后守护测试因「什么都没扫到」而空转变绿。
        Assert.True(Sources().Sum(s => AnyLogCall.Matches(s.Text).Count) > 100);
    }

    [Fact]
    public void EveryLogTemplate_IsAPlainLiteral()
    {
        var violations = Sources()
            .Where(s => AnyLogCall.Matches(s.Text).Count != LiteralLogCall.Matches(s.Text).Count)
            .Select(s => $"{s.Path}：存在模板不是字面量的日志调用")
            .ToList();
        violations.AddRange(LiteralCalls()
            .Where(c => c.Match.Groups["interp"].Value == "$")
            .Select(c => c.Where + " 使用了插值字符串"));

        Assert.Empty(violations);
    }

    [Fact]
    public void EveryLogTemplate_StartsWithDomainTag()
    {
        var violations = LiteralCalls()
            .Where(c => !Prefix.IsMatch(c.Match.Groups["head"].Value))
            .Select(c => $"{c.Where} {c.Match.Groups["head"].Value}")
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void NoLogTemplate_EndsWithFullStop()
    {
        var violations = LiteralCalls()
            .Where(c => FullTemplate(c.Match).EndsWith('。'))
            .Select(c => $"{c.Where} {FullTemplate(c.Match)}")
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Exceptions_ArePassedAsExceptionArgument()
    {
        var violations = Sources()
            .SelectMany(s => ExceptionMessageArgument.Matches(s.Text).Select(m => $"{s.Path}:{LineOf(s.Text, m.Index)}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void PluginSource_DoesNotWriteToConsole()
    {
        var violations = Sources()
            .SelectMany(s => ConsoleOutput.Matches(s.Text).Select(m => $"{s.Path}:{LineOf(s.Text, m.Index)}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("[歌词] 未找到歌词：{Title}", true)]
    [InlineData("[歌词:{Provider}] 搜索超时", true)]
    [InlineData("[MediaLink:上游] 地址无效：{Error}", true)]
    [InlineData("[插件] MediaIsland v{Version} 已加载", false)] // 插件加载不设域标签
    [InlineData("未找到歌词", false)]            // 缺少域标签
    [InlineData("MediaLink 服务启动失败", false)] // 裸前缀
    [InlineData("[其它] 未知域", false)]          // 域不在清单内
    [InlineData("[歌词]未加空格", false)]
    [InlineData("[歌词] ", false)]               // 标签后没有正文
    public void Prefix_AcceptsOnlyKnownDomainTags(string template, bool expected)
    {
        Assert.Equal(expected, Prefix.IsMatch(template));
    }

    private static string FullTemplate(Match match) =>
        match.Groups["head"].Value
        + string.Concat(TailLiteral.Matches(match.Groups["tail"].Value).Select(m => m.Groups[1].Value));

    private static IEnumerable<(string Where, Match Match)> LiteralCalls() =>
        Sources().SelectMany(s => LiteralLogCall.Matches(s.Text)
            .Select(m => ($"{s.Path}:{LineOf(s.Text, m.Index)}", m)));

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static IEnumerable<(string Path, string Text)> Sources()
    {
        var root = RepositoryRoot();
        return SourceDirectories
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(p => !p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "bin" or "obj"))
            .Select(p => (Path: Path.GetRelativePath(root, p), Full: p))
            .Where(p => !ExemptFiles.Contains(p.Path))
            .Select(p => (p.Path, File.ReadAllText(p.Full)));
    }

    // 测试产物可能输出到仓库外的临时目录，所以按本源文件的编译期路径定位仓库根目录。
    private static string RepositoryRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
