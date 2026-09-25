using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Shared.Helpers;
using MediaIsland.Models;

namespace MediaIsland.SettingsPages;

/// <summary>
/// MediaIsland 各设置页的公共基类：设置对象、属性通知、保存与离场标记。
/// </summary>
public abstract class MediaIslandSettingsPage : SettingsPageBase, INotifyPropertyChanged
{
    /// <summary>侧边栏里 MediaIsland 分组的 id，各页经 <c>[Group]</c> 挂到这里。</summary>
    public const string GroupId = "mediaisland";

    protected MediaIslandSettingsPage(Plugin plugin)
    {
        Plugin = plugin;
        Settings = plugin.Settings;
    }

    public Plugin Plugin { get; }

    public PluginSettings Settings { get; }

    /// <summary>页面已离开可视树；异步回调据此放弃写回界面。</summary>
    protected bool IsDetached { get; private set; }

    // UserControl 已有 Avalonia 自己的 PropertyChanged，这里显式实现接口以免同名冲突。
    private event PropertyChangedEventHandler? NotifyPropertyChanged;

    event PropertyChangedEventHandler? INotifyPropertyChanged.PropertyChanged
    {
        add => NotifyPropertyChanged += value;
        remove => NotifyPropertyChanged -= value;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        IsDetached = true;
        base.OnDetachedFromVisualTree(e);
    }

    protected void SaveSettings()
    {
        ConfigureFileHelper.SaveConfig<PluginSettings>(
            Path.Combine(Plugin.globalConfigFolder!, "Settings.json"),
            Settings);
    }

    /// <summary>回到 UI 线程执行界面更新；页面已离场则丢弃。</summary>
    protected async Task PostToUiAsync(Action update)
    {
        if (IsDetached)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!IsDetached)
            {
                update();
            }
        });
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        NotifyPropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
