using System;
using System.Threading.Tasks;
using Windows.Media.Control;
using MediaIsland.Models;

namespace MediaIsland.Services;

public interface IMediaService : IDisposable
{
    /// <summary>
    /// 媒体属性改变事件
    /// </summary>
    event EventHandler<MediaInfo?>? OnMediaPropertiesChanged;
    /// <summary>
    /// 播放状态改变事件
    /// </summary>
    event EventHandler<GlobalSystemMediaTransportControlsSessionPlaybackInfo>? OnPlaybackStateChanged;
    /// <summary>
    /// SMTC 会话改变事件
    /// </summary>
    event EventHandler? OnFocusedSessionChanged;

    /// <summary>
    /// 启动媒体服务
    /// </summary>
    Task StartAsync();
    /// <summary>
    /// 当前媒体信息
    /// </summary>
    MediaInfo? CurrentMediaInfo { get; }
}