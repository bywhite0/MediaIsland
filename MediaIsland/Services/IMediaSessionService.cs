using System;
using Microsoft.Extensions.Hosting;
using WindowsMediaController;
using static WindowsMediaController.MediaManager;

namespace MediaIsland.Services
{
    public interface IMediaSessionService : IHostedService
    {
        /// <summary>
        /// 活跃媒体会话发生变化（标题、状态、封面等）
        /// </summary>
        event EventHandler<MediaSession?>? MediaSessionChanged;

        /// <summary>
        /// 启动媒体会话监控
        /// </summary>
        void StartAsync();

        /// <summary>
        /// 停止媒体监控
        /// </summary>
        void StopAsync();

        /// <summary>
        /// 当前活跃会话
        /// </summary>
        MediaSession? CurrentSession { get; }
    }
}