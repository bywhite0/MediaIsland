using ClassIsland.Core.Abstractions.Controls;

namespace MediaIsland.Components;

/// <summary>
/// SpectrumComponentSettings.axaml 的交互逻辑。
///
/// 全部配置项都是本组件私有的——频段映射与包络都在组件侧，分析层只提供共享的原始谱，
/// 故岛上放两个频谱组件可以各调各的形态与灵敏度。
/// </summary>
public partial class SpectrumComponentSettings : ComponentBase<SpectrumComponentConfig>
{
    public SpectrumComponentSettings()
    {
        InitializeComponent();
    }
}
