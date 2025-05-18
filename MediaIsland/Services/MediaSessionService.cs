using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Windows.Threading;
using Windows.Media.Control;
using WindowsMediaController;
using static WindowsMediaController.MediaManager;

namespace MediaIsland.Services
{
    public class MediaSessionService : IMediaSessionService
    {
        private readonly MediaManager _mediaManager;

        private ILogger<MediaSessionService> Logger { get; }

        public event EventHandler<MediaSession?>? MediaSessionChanged;

        public MediaSession? CurrentSession { get; private set; }

        public MediaSessionService(ILogger<MediaSessionService> logger)
        {
            Logger = logger;
            _mediaManager = new MediaManager();
            _mediaManager.OnAnySessionOpened += OnAnySessionOpened;
            _mediaManager.OnAnySessionClosed += OnAnySessionClosed;
            _mediaManager.OnFocusedSessionChanged += OnFocusedSessionChanged;
            _mediaManager.OnAnyPlaybackStateChanged += OnAnyPlaybackStateChanged;
            _mediaManager.OnAnyMediaPropertyChanged += OnAnyMediaPropertyChanged;
            _mediaManager.OnAnyTimelinePropertyChanged += OnAnyTimelinePropertyChanged;
        }

        public async void StartAsync()
        {
            await _mediaManager.StartAsync();
        }

        public void StopAsync()
        {
            _mediaManager.Dispose();
        }

        /// <summary>
        /// SMTC 会话打开事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        void OnAnySessionOpened(MediaSession sender)
        {
            Logger.LogDebug($"新 SMTC 会话：{sender.Id}");
            CurrentSession = sender;
            MediaSessionChanged?.Invoke(this, sender);
        }

        /// <summary>
        /// SMTC 会话关闭事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        void OnAnySessionClosed(MediaSession sender)
        {
            Logger!.LogDebug($"SMTC 会话关闭：{sender.Id}");
            MediaSessionChanged?.Invoke(this, sender);
            //await RefreshMediaInfo(sender);
        }
        /// <summary>
        /// SMTC 会话焦点改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        void OnFocusedSessionChanged(MediaSession sender)
        {
            Logger!.LogDebug($"SMTC 会话焦点改变：{sender?.ControlSession?.SourceAppUserModelId}");
            if (sender?.ControlSession == null)
            {
                CurrentSession = null;
                MediaSessionChanged?.Invoke(this, null);
            }
            CurrentSession = sender;
            MediaSessionChanged?.Invoke(this, sender);

        }
        /// <summary>
        /// SMTC 播放状态改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        void OnAnyPlaybackStateChanged(MediaSession sender, GlobalSystemMediaTransportControlsSessionPlaybackInfo args)
        {
            Logger!.LogDebug($"SMTC 播放状态改变：{sender.Id} is now {args.PlaybackStatus}");
            CurrentSession = sender;
            MediaSessionChanged?.Invoke(this, sender);
            
        }
        /// <summary>
        /// SMTC 媒体属性改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        void OnAnyMediaPropertyChanged(MediaSession sender, GlobalSystemMediaTransportControlsSessionMediaProperties args)
        {
            Logger!.LogDebug($"SMTC 媒体属性改变：{sender.Id} is now playing {args.Title} {(string.IsNullOrEmpty(args.Artist) ? "" : $"by {args.Artist}")}");
            CurrentSession = sender;
            MediaSessionChanged?.Invoke(this, sender);
        }
        /// <summary>
        /// SMTC 时间属性改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        void OnAnyTimelinePropertyChanged(MediaManager.MediaSession sender, GlobalSystemMediaTransportControlsSessionTimelineProperties args)
        {
            Logger!.LogDebug($"SMTC 时间属性改变：{sender.Id} timeline is now {args.Position}/{args.EndTime}");
            CurrentSession = sender;
            MediaSessionChanged?.Invoke(this, sender);
        }
        public void Dispose()
        {
            StopAsync();
            _mediaManager.OnAnySessionOpened -= OnAnySessionOpened;
            _mediaManager.OnAnySessionClosed -= OnAnySessionClosed;
            _mediaManager.OnAnyPlaybackStateChanged -= OnAnyPlaybackStateChanged;
            _mediaManager.OnAnyMediaPropertyChanged -= OnAnyMediaPropertyChanged;
            _mediaManager.OnAnyTimelinePropertyChanged -= OnAnyTimelinePropertyChanged;
            _mediaManager.OnFocusedSessionChanged -= OnFocusedSessionChanged;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}