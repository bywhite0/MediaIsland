using Avalonia.Controls;
using Avalonia.Threading;
using MediaIsland.Controls;
using MediaIsland.Services.Audio.Visualization;

namespace MediaIsland.Components;

/// <summary>
/// 四个音频组件设置页的共同页头：实验性徽标、一句说明、当前数据来源或不可用原因。
/// 设置页可见期间每秒轮询一次——状态只在设置页打开时有人看，不值得为它新增事件。
/// </summary>
public partial class AudioVisualSettingsHeader : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private IAudioVisualSourceInfo? _info;
    private AudioVisualizationService? _visualization;
    private long _lastRevision = -1;
    private IDisposable? _statusBrushBinding;
    private bool _statusIsWarning;

    public AudioVisualSettingsHeader()
    {
        InitializeComponent();
        ExplanationText.Text = AudioVisualStatusRules.Explanation;
        _timer.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) => { Refresh(); _timer.Start(); };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    public void Attach(IAudioVisualSourceInfo info, AudioVisualizationService visualization)
    {
        _info = info;
        _visualization = visualization;
        Refresh();
    }

    private void Refresh()
    {
        if (_info is null || _visualization is null) return;

        var snapshot = _visualization.Capture();
        var silent = snapshot.IsSilent || snapshot.Revision == _lastRevision;
        _lastRevision = snapshot.Revision;

        var (text, warning) = AudioVisualStatusRules.Describe(
            _info.IsLocalSourceAvailable, _info.LocalFailureReason, _info.IsConsumingUpstream, silent);
        StatusText.Text = text;
        BindStatusBrush(warning);
    }

    /// <summary>
    /// 以资源绑定而非一次性查找来取色。设置控件在构造时就会刷新一次，那时还没挂进视觉树，
    /// 查找返回的是 <c>UnsetValue</c> 而不是 null——强转成画刷会抛异常，整个设置页随之加载失败。
    /// 绑定把「暂时找不到」当作未设置，挂树后自动补上，换主题时也会跟着变。
    /// </summary>
    private void BindStatusBrush(bool warning)
    {
        if (_statusBrushBinding is not null && _statusIsWarning == warning) return;

        _statusBrushBinding?.Dispose();
        _statusIsWarning = warning;
        _statusBrushBinding = StatusText.Bind(
            TextBlock.ForegroundProperty,
            this.GetResourceObservable(warning ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"));
    }
}
