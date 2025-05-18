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
        /// SMTC �Ự���¼�
        /// </summary>
        /// <param name="sender">�����¼��� SMTC �Ự</param>
        void OnAnySessionOpened(MediaSession sender)
        {
            Logger.LogDebug($"�� SMTC �Ự��{sender.Id}");
            CurrentSession = sender;
            MediaSessionChanged?.Invoke(this, sender);
        }

        /// <summary>
        /// SMTC �Ự�ر��¼�
        /// </summary>
        /// <param name="sender">�����¼��� SMTC �Ự</param>
        void OnAnySessionClosed(MediaSession sender)
        {
            Logger!.LogDebug($"SMTC �Ự�رգ�{sender.Id}");
            MediaSessionChanged?.Invoke(this, sender);
            //await RefreshMediaInfo(sender);
        }
        /// <summary>
        /// SMTC �Ự����ı��¼�
        /// </summary>
        /// <param name="sender">�����¼��� SMTC �Ự</param>
        void OnFocusedSessionChanged(MediaSession sender)
        {
            Logger!.LogDebug($"SMTC �Ự����ı䣺{sender?.ControlSession?.SourceAppUserModelId}");
            if (sender?.ControlSession == null)
            {
                CurrentSession = null;
                MediaSessionChanged?.Invoke(this, null);
            }
            CurrentSession = sender;
            MediaSessionChanged?.Invoke(this, sender);

        }
        /// <summary>
        /// SMTC ����״̬�ı��¼�
        /// </summary>
        /// <param name="sender">�����¼��� SMTC �Ự</param>
        void OnAnyPlaybackStateChanged(MediaSession sender, GlobalSystemMediaTransportControlsSessionPlaybackInfo args)
        {
            Logger!.LogDebug($"SMTC ����״̬�ı䣺{sender.Id} is now {args.PlaybackStatus}");
            CurrentSession = sender;
            MediaSessionChanged?.Invoke(this, sender);
            
        }
        /// <summary>
        /// SMTC ý�����Ըı��¼�
        /// </summary>
        /// <param name="sender">�����¼��� SMTC �Ự</param>
        void OnAnyMediaPropertyChanged(MediaSession sender, GlobalSystemMediaTransportControlsSessionMediaProperties args)
        {
            Logger!.LogDebug($"SMTC ý�����Ըı䣺{sender.Id} is now playing {args.Title} {(string.IsNullOrEmpty(args.Artist) ? "" : $"by {args.Artist}")}");
            CurrentSession = sender;
            MediaSessionChanged?.Invoke(this, sender);
        }
        /// <summary>
        /// SMTC ʱ�����Ըı��¼�
        /// </summary>
        /// <param name="sender">�����¼��� SMTC �Ự</param>
        void OnAnyTimelinePropertyChanged(MediaManager.MediaSession sender, GlobalSystemMediaTransportControlsSessionTimelineProperties args)
        {
            Logger!.LogDebug($"SMTC ʱ�����Ըı䣺{sender.Id} timeline is now {args.Position}/{args.EndTime}");
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