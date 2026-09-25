using Avalonia.Media;
using ClassIsland.Core.Attributes;

namespace MediaIsland.SettingsPages;

/// <summary>「显示效果」页：歌词渲染效果、字体与「正在播放」外观。</summary>
[SettingsPageInfo("mediaisland.display", "显示效果", "\uEC4A", "\uEC49")]
[Group(GroupId)]
public partial class DisplaySettingsPage : MediaIslandSettingsPage
{
    private const string FollowGlobalFontOption = "跟随全局字体";

    public DisplaySettingsPage(Plugin plugin) : base(plugin)
    {
        InitializeComponent();
    }

    /// <summary>
    /// 字体下拉选项：首项为“跟随全局字体”，其余为系统已安装字体。
    /// </summary>
    public IReadOnlyList<string> FontFamilyOptions { get; } = BuildFontFamilyOptions();

    /// <summary>字重下拉选项，下标即设置里存的 FontWeightIndex，0 为跟随全局。</summary>
    public IReadOnlyList<string> FontWeightOptions { get; } =
    [
        "跟随全局",
        "100 Thin",
        "200 ExtraLight",
        "300 Light",
        "400 Regular",
        "500 Medium",
        "600 SemiBold",
        "700 Bold",
        "800 ExtraBold",
        "900 Black"
    ];

    public string LyricsOriginalFontFamilySelection
    {
        get => ToFontOption(Settings.LyricsOriginalFontFamily);
        set
        {
            Settings.LyricsOriginalFontFamily = FromFontOption(value);
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public string LyricsTranslationFontFamilySelection
    {
        get => ToFontOption(Settings.LyricsTranslationFontFamily);
        set
        {
            Settings.LyricsTranslationFontFamily = FromFontOption(value);
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public string LyricsRomanizationFontFamilySelection
    {
        get => ToFontOption(Settings.LyricsRomanizationFontFamily);
        set
        {
            Settings.LyricsRomanizationFontFamily = FromFontOption(value);
            OnPropertyChanged();
            SaveSettings();
        }
    }

    private static string ToFontOption(string? family) =>
        string.IsNullOrWhiteSpace(family) ? FollowGlobalFontOption : family;

    private static string FromFontOption(string? option) =>
        string.IsNullOrWhiteSpace(option) || option == FollowGlobalFontOption
            ? string.Empty
            : option;

    private static IReadOnlyList<string> BuildFontFamilyOptions()
    {
        var options = new List<string> { FollowGlobalFontOption };
        try
        {
            options.AddRange(FontManager.Current.SystemFonts
                .Select(font => font.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCulture));
        }
        catch (Exception)
        {
            // 系统字体枚举失败时至少保留“跟随全局字体”，不影响设置页打开。
        }

        return options;
    }
}
