using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIsland.Core.Attributes;
using MediaIsland.Models;
using MediaIsland.Services.MediaLink;

namespace MediaIsland.SettingsPages;

/// <summary>「Link to the MEDIA」页：本机作为发送端的共享服务、凭据与音频预算。</summary>
[SettingsPageInfo("mediaisland.medialink.share", "Link to the MEDIA", "\uEF39", "\uEF38")]
[Group(GroupId)]
public partial class MediaLinkShareSettingsPage : MediaIslandSettingsPage
{
    private readonly IMediaLinkGateway? _mediaLinkGateway;
    private readonly MediaLinkInjectionStore? _injectionStore;
    private string _mediaLinkStatusText = "未启用";
    private string _mediaLinkExposureWarning = string.Empty;
    private string _mediaLinkConfigCodeHint = string.Empty;

    public string MediaLinkStatusText
    {
        get => _mediaLinkStatusText;
        private set => SetProperty(ref _mediaLinkStatusText, value);
    }

    /// <summary>非回环监听时的明文暴露提示；为空表示无需提示。</summary>
    public string MediaLinkExposureWarning
    {
        get => _mediaLinkExposureWarning;
        private set
        {
            if (SetProperty(ref _mediaLinkExposureWarning, value))
            {
                OnPropertyChanged(nameof(HasMediaLinkExposureWarning));
            }
        }
    }

    public bool HasMediaLinkExposureWarning => !string.IsNullOrEmpty(_mediaLinkExposureWarning);

    /// <summary>复制配置码后的反馈；为空时不显示。</summary>
    public string MediaLinkConfigCodeHint
    {
        get => _mediaLinkConfigCodeHint;
        private set
        {
            if (SetProperty(ref _mediaLinkConfigCodeHint, value))
            {
                OnPropertyChanged(nameof(HasMediaLinkConfigCodeHint));
            }
        }
    }

    public bool HasMediaLinkConfigCodeHint => !string.IsNullOrEmpty(_mediaLinkConfigCodeHint);

    public MediaLinkShareSettingsPage(
        Plugin plugin,
        IMediaLinkGateway? mediaLinkGateway = null,
        MediaLinkInjectionStore? injectionStore = null) : base(plugin)
    {
        _mediaLinkGateway = mediaLinkGateway;
        _injectionStore = injectionStore;
        InitializeComponent();
        Settings.PropertyChanged += OnPluginSettingsChanged;
        RefreshMediaLinkStatus();
        // gateway 已实现 INotifyPropertyChanged，事件驱动既即时又无空转，不再轮询。
        if (_mediaLinkGateway is not null)
        {
            _mediaLinkGateway.PropertyChanged += OnMediaLinkGatewayChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Settings.PropertyChanged -= OnPluginSettingsChanged;
        if (_mediaLinkGateway is not null)
        {
            _mediaLinkGateway.PropertyChanged -= OnMediaLinkGatewayChanged;
        }
    }

    private void RefreshMediaLinkStatus()
    {
        if (_mediaLinkGateway is null)
        {
            MediaLinkStatusText = "共享服务不可用";
            MediaLinkExposureWarning = string.Empty;
            return;
        }

        MediaLinkExposureWarning = BuildExposureWarning();
        if (!_mediaLinkGateway.IsRunning)
        {
            MediaLinkStatusText = string.IsNullOrWhiteSpace(_mediaLinkGateway.LastError)
                ? "未开启"
                : $"无法启动：{_mediaLinkGateway.LastError}";
            return;
        }

        var count = _mediaLinkGateway.ActiveSessionCount;
        var clients = count == 0 ? "暂无程序连接" : $"{count} 个程序已连接";
        var inject = _injectionStore is not null &&
                     (_injectionStore.HasExternalMedia || _injectionStore.HasExternalLyrics)
            ? "；正在显示外部来源的内容"
            : string.Empty;

        MediaLinkStatusText = $"运行中 · {_mediaLinkGateway.Endpoint ?? "-"} · {clients}{inject}";
    }

    /// <summary>
    /// 监听地址非回环时给出明文暴露提示。传输无 TLS，同网段可嗅探 Token
    /// 与全部播放信息，这个代价必须让用户看见。
    /// </summary>
    private string BuildExposureWarning()
    {
        if (!Settings.MediaLinkIsEnabled)
        {
            return string.Empty;
        }

        var address = Settings.MediaLinkListenAddress?.Trim();
        if (string.IsNullOrEmpty(address))
        {
            return string.Empty;
        }

        var isLoopback = address is "127.0.0.1" or "::1" or "[::1]" ||
                         address.StartsWith("127.", StringComparison.Ordinal) ||
                         address.Equals("localhost", StringComparison.OrdinalIgnoreCase);

        return isLoopback
            ? string.Empty
            : "⚠ 当前设置允许局域网内的其它设备连接。传输过程未加密，同一网络下的人可能截获连接密钥与播放信息，请仅在自己信任的网络中这样设置。";
    }

    private void OnMediaLinkGatewayChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsDetached)
        {
            return;
        }

        // gateway 的通知来自后台线程（listener 重建、会话增减），必须回到 UI 线程。
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsDetached)
            {
                RefreshMediaLinkStatus();
            }
        });
    }

    private void OnPluginSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PluginSettings.MediaLinkIsEnabled)
            or nameof(PluginSettings.MediaLinkListenAddress)
            or nameof(PluginSettings.MediaLinkPort)
            or nameof(PluginSettings.MediaLinkToken)
            or nameof(PluginSettings.MediaLinkTimelineMinIntervalMs)
            or nameof(PluginSettings.MediaLinkMediaSourceMode)
            or nameof(PluginSettings.MediaLinkUiUsesEffective)
            or nameof(PluginSettings.MediaLinkPushUsesEffective)))
        {
            return;
        }

        // 暴露提示只取决于设置本身，立即更新；不能等 gateway 那 300ms，
        // 否则用户改成非回环地址后要过一会儿才看到警告。
        MediaLinkExposureWarning = _mediaLinkGateway is null ? string.Empty : BuildExposureWarning();

        // 运行状态是热更新异步完成的，短延迟后再读 gateway。
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(300);
            if (!IsDetached)
            {
                RefreshMediaLinkStatus();
            }
        });
    }

    private async void CopyMediaLinkTokenOnClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(Settings.MediaLinkToken ?? string.Empty);
    }

    private void RegenerateMediaLinkTokenOnClick(object? sender, RoutedEventArgs e)
    {
        Settings.MediaLinkToken = MediaLinkAuth.GenerateToken();
        RefreshMediaLinkStatus();
    }

    /// <summary>
    /// 复制本机的配置码，供另一台设备一键导入，免去手抄 32 字节密钥。
    /// 配置码含密钥且未加密，故提示语必须说明这一点。
    /// </summary>
    private async void CopyMediaLinkConfigCodeOnClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        var code = MediaLinkConfigCode.ForLocalInstance(
            Settings.MediaLinkListenAddress,
            Settings.MediaLinkPort,
            Settings.MediaLinkToken);

        if (code is null)
        {
            // 拿不到局域网地址时给出的配置码必然连不上，不如不给。
            MediaLinkConfigCodeHint = string.IsNullOrWhiteSpace(Settings.MediaLinkToken)
                ? "请先开启共享以生成连接密钥"
                : "无法获取本机的局域网地址，请手动把地址与密钥填到对方设备";
            return;
        }

        await clipboard.SetTextAsync(code.Encode());
        MediaLinkConfigCodeHint = "已复制。这串文本包含连接密钥，请仅发给信任的设备。";
    }
}
