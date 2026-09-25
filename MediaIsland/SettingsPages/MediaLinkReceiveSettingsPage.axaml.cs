using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIsland.Core.Attributes;
using MediaIsland.Models;
using MediaIsland.Services.Audio.Playback;
using MediaIsland.Services.MediaLink;

namespace MediaIsland.SettingsPages;

/// <summary>「Link-Like MediaLink」页：本机作为接收端的连接、显示来源与音频播放。</summary>
[SettingsPageInfo("mediaisland.medialink.receive", "Link-Like MediaLink", "\uE0D3", "\uE0D2")]
[Group(GroupId)]
public partial class MediaLinkReceiveSettingsPage : MediaIslandSettingsPage
{
    private readonly MediaLinkUpstreamHostedService? _upstream;
    private readonly AudioPlaybackService? _playback;
    private readonly DefaultEndpointWatcher? _endpointWatcher;
    private string _mediaLinkUpstreamStatusText = "未启用";
    private string _mediaLinkUpstreamCodeHint = string.Empty;
    private string _mediaLinkAlignmentStatusText = MediaLinkAlignmentStatusLine.Unavailable;

    /// <summary>
    /// 对齐状态行的刷新循环，页面可见时每秒一拍，离开即停（与退订同点，成对）。
    /// 判定快照没有变更事件可订阅——协调方按探测节奏重算，这里轮询是数据源的形态
    /// 决定的，不是偷懒。
    /// </summary>
    private readonly DispatcherTimer _alignmentStatusTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };

    public string MediaLinkUpstreamStatusText
    {
        get => _mediaLinkUpstreamStatusText;
        private set => SetProperty(ref _mediaLinkUpstreamStatusText, value);
    }

    /// <summary>粘贴配置码后的反馈；为空时不显示。</summary>
    public string MediaLinkUpstreamCodeHint
    {
        get => _mediaLinkUpstreamCodeHint;
        private set
        {
            if (SetProperty(ref _mediaLinkUpstreamCodeHint, value))
            {
                OnPropertyChanged(nameof(HasMediaLinkUpstreamCodeHint));
            }
        }
    }

    public bool HasMediaLinkUpstreamCodeHint => !string.IsNullOrEmpty(_mediaLinkUpstreamCodeHint);

    public int MediaLinkMediaSourceModeIndex
    {
        get => (int)Settings.MediaLinkMediaSourceMode;
        set
        {
            var normalized = value is < 0 or > 2 ? 0 : value;
            var mode = (MediaLinkMediaSourceMode)normalized;
            if (Settings.MediaLinkMediaSourceMode == mode)
            {
                return;
            }

            Settings.MediaLinkMediaSourceMode = mode;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 当前输出设备的手动出声偏移，毫秒。读写都按当前设备的标识落进 per-device
    /// 存储；写后重发通知让控件回读，越界输入由此显示为夹紧后的生效值。
    /// </summary>
    public int MediaLinkManualOffsetMs
    {
        get => Settings.GetManualOffsetMs(_playback?.CurrentPlaybackDeviceId);
        set
        {
            Settings.SetManualOffsetMs(_playback?.CurrentPlaybackDeviceId, value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 拿不到设备标识（无音频设备、播放层未注册）时禁用偏移控件。
    /// 不禁用的话输入会被静默丢弃——看起来能调、实际什么都没记。
    /// </summary>
    public bool HasPlaybackDevice => _playback?.CurrentPlaybackDeviceId is not null;

    /// <summary>对齐状态行文案，组装在 <see cref="MediaLinkAlignmentStatusLine"/>。</summary>
    public string MediaLinkAlignmentStatusText
    {
        get => _mediaLinkAlignmentStatusText;
        private set => SetProperty(ref _mediaLinkAlignmentStatusText, value);
    }

    public MediaLinkReceiveSettingsPage(
        Plugin plugin,
        MediaLinkUpstreamHostedService? upstream = null,
        AudioPlaybackService? playback = null,
        DefaultEndpointWatcher? endpointWatcher = null) : base(plugin)
    {
        _upstream = upstream;
        _playback = playback;
        _endpointWatcher = endpointWatcher;
        InitializeComponent();
        _alignmentStatusTimer.Tick += (_, _) => RefreshAlignmentStatusRow();
        Settings.PropertyChanged += OnPluginSettingsChanged;
        if (_upstream is not null)
        {
            _upstream.StateChanged += OnUpstreamStateChanged;
        }

        RefreshUpstreamStatus();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 页面可见时为设备 watcher 开门：HasPlaybackDevice 与 per-device 偏移读的是
        // watcher 缓存，没有这扇门，无会话时打开设置页会读到停更的旧值。
        // 进场泵一拍后缓存已有值，回发通知让两个绑定立即回读。
        _endpointWatcher?.SetUiVisible(true);
        OnPropertyChanged(nameof(HasPlaybackDevice));
        OnPropertyChanged(nameof(MediaLinkManualOffsetMs));
        // 进场先刷一拍再起表，状态行不空等第一秒。
        RefreshAlignmentStatusRow();
        _alignmentStatusTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _endpointWatcher?.SetUiVisible(false);
        _alignmentStatusTimer.Stop();
        Settings.PropertyChanged -= OnPluginSettingsChanged;
        if (_upstream is not null)
        {
            _upstream.StateChanged -= OnUpstreamStateChanged;
        }
    }

    /// <summary>
    /// 状态行与 HasPlaybackDevice 的一拍刷新。快照读取失败只降级为「暂无」——
    /// 诊断行炸不得设置页。HasPlaybackDevice 挂同一拍：此前只在进场时通知一次，
    /// 页面开着时设备到场离场，偏移控件的可用性不会跟着变。
    /// </summary>
    private void RefreshAlignmentStatusRow()
    {
        MediaLinkAlignmentSnapshot? snapshot;
        try
        {
            snapshot = _upstream?.ReadAlignmentDecision();
        }
        catch (Exception)
        {
            snapshot = null;
        }

        MediaLinkAlignmentStatusText = MediaLinkAlignmentStatusLine.ComposeLine(snapshot);
        OnPropertyChanged(nameof(HasPlaybackDevice));
    }

    private void OnUpstreamStateChanged(object? sender, EventArgs e)
    {
        if (IsDetached)
        {
            return;
        }

        // 来自后台线程：重连循环与设置热更新都不在 UI 线程上。
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsDetached)
            {
                RefreshUpstreamStatus();
            }
        });
    }

    private void RefreshUpstreamStatus()
    {
        if (_upstream is null)
        {
            MediaLinkUpstreamStatusText = "接收服务不可用";
            return;
        }

        if (!Settings.MediaLinkUpstreamIsEnabled)
        {
            MediaLinkUpstreamStatusText = "未开启";
            return;
        }

        if (!string.IsNullOrWhiteSpace(_upstream.LastError))
        {
            MediaLinkUpstreamStatusText = $"未连接：{_upstream.LastError}";
            return;
        }

        // 来源设为「本机播放器」时收到的内容不会显示出来，这是最容易踩的坑。
        var modeHint = Settings.MediaLinkMediaSourceMode == MediaLinkMediaSourceMode.PlatformOnly
            ? "。⚠ 但下方「媒体信息来源」当前为「本机播放器」，接收到的内容不会显示，请改为「外部来源优先」"
            : string.Empty;

        MediaLinkUpstreamStatusText = _upstream.IsConnected
            ? $"已连接{modeHint}"
            : $"正在连接对方设备…{modeHint}";
    }

    private void OnPluginSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PluginSettings.MediaLinkMediaSourceMode))
        {
            OnPropertyChanged(nameof(MediaLinkMediaSourceModeIndex));
            RefreshUpstreamStatus();   // 提示语依赖当前媒体源模式
            return;
        }

        if (e.PropertyName is nameof(PluginSettings.MediaLinkUpstreamIsEnabled)
            or nameof(PluginSettings.MediaLinkUpstreamEndpoint)
            or nameof(PluginSettings.MediaLinkUpstreamToken))
        {
            RefreshUpstreamStatus();
        }
    }

    /// <summary>从剪贴板粘贴对方的配置码，一次填好地址与密钥。</summary>
    private async void PasteMediaLinkConfigCodeOnClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        var text = await clipboard.TryGetTextAsync();
        if (!MediaLinkConfigCode.TryParse(text, out var code, out var error))
        {
            MediaLinkUpstreamCodeHint = error ?? "配置码无法识别";
            return;
        }

        Settings.MediaLinkUpstreamEndpoint = code!.Endpoint;
        Settings.MediaLinkUpstreamToken = code.Token;
        MediaLinkUpstreamCodeHint = $"已填入 {code.Endpoint}";
        RefreshUpstreamStatus();
    }
}
