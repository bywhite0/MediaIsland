using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using MaterialDesignThemes.Wpf;
using Windows.Media.Control;
using Windows.Storage.Streams;
using MediaIsland.Helpers;
using Microsoft.Extensions.Logging;

namespace MediaIsland.Components
{ 
    [ComponentInfo(
            "6E9C7C44-59EB-499C-A637-2C6C9253BF2B",
            "正在播放",
            PackIconKind.MusicBox,
            "显示当前播放的媒体信息。"
        )]
    public partial class NowPlayingComponent : ComponentBase
    {
        //private string titleLabel, artistLabel, albumLabel, timeLabel, sourceLabel;
        GlobalSystemMediaTransportControlsSessionManager? smtcManager;
        GlobalSystemMediaTransportControlsSession? currentSession;
        //TimeSpan currentDuration;
        //TimeSpan currentPosition;
        public NowPlayingComponent()
        {
            InitializeComponent();
            LoadCurrentPlayingInfoAsync();
        }


        /// <summary>
        /// 获取 SMTC 信息并更新 UI
        /// </summary>
        async void LoadCurrentPlayingInfoAsync()
        {
            try
            {
                smtcManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                smtcManager.SessionsChanged += SmtcManager_SessionsChanged;

                currentSession = smtcManager.GetCurrentSession();
                Console.WriteLine("[MI]尝试获取 SMTC 会话信息");
                if (currentSession != null)
                {
                    Console.WriteLine("[MI]存在 SMTC 会话信息，订阅事件");
                    currentSession.MediaPropertiesChanged += CurrentSession_MediaPropertiesChanged;
                    currentSession.PlaybackInfoChanged += CurrentSession_PlaybackInfoChanged;
                    currentSession.TimelinePropertiesChanged += CurrentSession_TimelinePropertiesChanged;
                    Console.WriteLine("[MI]刷新【正在播放】组件内容");
                    await RefreshMediaInfo(currentSession);
                }
                else
                {
                    Console.WriteLine("[MI]不存在 SMTC 会话信息，隐藏组件 UI");
                    await Dispatcher.InvokeAsync(() => {
                        InfoStackPanel.Visibility = Visibility.Collapsed;
                        CoverStackPanel.Visibility = Visibility.Collapsed;
                        SourceStackPanel.Visibility = Visibility.Collapsed;
                    });
                }
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => {
                    Console.WriteLine($"[MI]获取 SMTC 会话时发生错误: {ex.Message}");
                    InfoStackPanel.Visibility = Visibility.Collapsed;
                    CoverStackPanel.Visibility = Visibility.Collapsed;
                    SourceStackPanel.Visibility = Visibility.Collapsed;
                });
            }
        }

        /// <summary>
        /// SMTC 时间属性改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        /// <param name="args">事件参数</param>
        async void CurrentSession_TimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        {
            await RefreshMediaInfo(sender);
        }

        /// <summary>
        /// 使用获取到的 SMTC 信息刷新 UI
        /// </summary>
        /// <param name="session">SMTC 会话</param>
        /// <returns></returns>
        private async Task RefreshMediaInfo(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                string sourceApp = session.SourceAppUserModelId;
                var mediaProperties = await session.TryGetMediaPropertiesAsync();
                var timeline = session.GetTimelineProperties();
                var playbackInfo = session.GetPlaybackInfo();

                await Dispatcher.InvokeAsync(new Action(async () =>
                {
                    InfoStackPanel.Visibility = Visibility.Visible;
                    CoverStackPanel.Visibility = Visibility.Visible;
                    SourceStackPanel.Visibility = Visibility.Visible;
                    // 强制更新UI元素
                    titleText.Text = mediaProperties.Title ?? "未知标题";
                    artistText.Text = mediaProperties.Artist ?? "未知艺术家";
                    //albumText.Text = mediaProperties.AlbumTitle ?? "未知专辑";

                    var thumb = mediaProperties.Thumbnail;
                    if (thumb != null)
                    {
                        await LoadThumbnailAsync(thumb);
                    }

                    // 更新播放器信息
                    sourceText.Text = AppInfoHelper.GetFriendlyAppName(session.SourceAppUserModelId);
                    sourceIcon.ImageSource = IconHelper.IconToImageSourceConverter(IconHelper.GetAppIcon(session.SourceAppUserModelId));

                    // 进度处理
                    //UpdateProgressUI(timeline.Position, timeline.EndTime);
                    //progressBar.Maximum = (int)timeline.EndTime.TotalSeconds;
                    //progressBar.Value = (int)timeline.Position.TotalSeconds;
                    //timeLabel.Text = $"{timeline.Position:mm\\:ss} / {timeline.EndTime:mm\\:ss}";
                }));
            }
            catch
            {
                return;
            }
        }

        /// <summary>
        /// 处理从 SMTC 获取到的专辑封面流
        /// </summary>
        /// <param name="thumbRef">传入的 WinRT 流</param>
        /// <returns></returns>
        private async Task LoadThumbnailAsync(IRandomAccessStreamReference thumbRef)
        {
            if (thumbRef != null)
            {
                // 打开 WinRT 流
                IRandomAccessStreamWithContentType winRtStream = await thumbRef.OpenReadAsync();

                // 扩展方法 AsStreamForRead 转为 .NET Stream
                using Stream netStream = winRtStream.AsStreamForRead();

                // WPF BitmapImage 从 Stream 加载
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = netStream;
                bmp.EndInit();
                bmp.Freeze();
                AlbumArt.ImageSource = bmp;
            }
        }

        //private void UpdateProgressUI(TimeSpan position, TimeSpan duration)
        //{
        //    currentProgressBar.Value = (int)Math.Min(position.TotalSeconds, progressBar.Maximum);
        //    timeText.Text = $"{position:mm\\:ss} / {duration:mm\\:ss}";

        //    if (duration.TotalSeconds > 0)
        //    {
        //        double ratio = position.TotalSeconds / duration.TotalSeconds;
        //    }
        //}

        /// <summary>
        /// SMTC 媒体属性改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        /// <param name="args">事件参数</param>
        private async void CurrentSession_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        {
            await RefreshMediaInfo(sender);
        }

        /// <summary>
        /// SMTC 播放信息改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        /// <param name="args">事件参数</param>
        private async void CurrentSession_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, object args)
        {
            //var timeline = sender.GetTimelineProperties();
            //Dispatcher.Invoke((Action)(() => UpdateProgressUI(timeline.Position, timeline.EndTime)));
            await RefreshMediaInfo(sender);
        }

        /// <summary>
        /// SMTC 会话改变事件
        /// </summary>
        /// <param name="sender">发出事件的 SMTC 会话</param>
        /// <param name="args">事件参数</param>
        private async void SmtcManager_SessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        {
            var currentSession = sender.GetCurrentSession();
            if (currentSession != null)
            {
                // 如果存在旧会话，先取消订阅其事件
                if (this.currentSession != null)
                {
                    this.currentSession.MediaPropertiesChanged -= CurrentSession_MediaPropertiesChanged;
                    this.currentSession.PlaybackInfoChanged -= CurrentSession_PlaybackInfoChanged;
                    this.currentSession.TimelinePropertiesChanged -= CurrentSession_TimelinePropertiesChanged;
                }

                // 设置新会话并订阅事件
                this.currentSession = currentSession;
                this.currentSession.MediaPropertiesChanged += CurrentSession_MediaPropertiesChanged;
                this.currentSession.PlaybackInfoChanged += CurrentSession_PlaybackInfoChanged;
                this.currentSession.TimelinePropertiesChanged += CurrentSession_TimelinePropertiesChanged;
        
                // 刷新 UI
                await RefreshMediaInfo(this.currentSession);
            }
            else
            {
                // 无当前会话，隐藏 UI 元素
                await Dispatcher.InvokeAsync(() => {
                    InfoStackPanel.Visibility = Visibility.Collapsed;
                    CoverStackPanel.Visibility = Visibility.Collapsed;
                    SourceStackPanel.Visibility = Visibility.Collapsed;
                });
        
                // 设置 currentSession 为 null
                this.currentSession = null;
            }
        }
    }
}