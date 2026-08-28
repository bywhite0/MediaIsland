using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ClassIsland.Core.Abstractions;
using MediaIsland.Services.Lyrics;
using MediaIsland.Services.Lyrics.Models;
using MediaIsland.Services.MediaLink;

namespace MediaIsland.Models
{
    public class PluginSettings : ObservableRecipient
    {
        public Action? needRestart;
        
        // Not implemented
        private bool _isLyricGetterEnabled = false;

        public bool IsLyricGetterEnabled
        {
            get => _isLyricGetterEnabled;
            set
            {
                if (_isLyricGetterEnabled == value) return;
                _isLyricGetterEnabled = value;
                OnPropertyChanged();
            }
        }
        private bool _isCutSpotifyTrademarkEnabled = false;

        public bool IsCutSpotifyTrademarkEnabled
        {
            get => _isCutSpotifyTrademarkEnabled;
            set
            {
                if (_isCutSpotifyTrademarkEnabled  == value) return;
                _isCutSpotifyTrademarkEnabled = value;
                OnPropertyChanged();
                needRestart?.Invoke();
            }
        }

        public ObservableCollection<MediaSource> MediaSourceList { get; set; } = [];

        public event EventHandler? MediaSourceSettingsSaved;

        public void NotifyMediaSourceSettingsSaved()
        {
            MediaSourceSettingsSaved?.Invoke(this, EventArgs.Empty);
        }

        private LyricsSourceSettings _lyrics = LyricsSourceSettings.Normalize(new LyricsSourceSettings());

        public LyricsSourceSettings Lyrics
        {
            get => _lyrics;
            set
            {
                _lyrics = LyricsSourceSettings.Normalize(value ?? new LyricsSourceSettings());
                OnPropertyChanged();
            }
        }

        [JsonIgnore]
        public bool HasConfiguredLyricSources => Lyrics.Sources.Any(source => source.IsEnabled);

        private bool _isWordLyricsEnabled = true;

        public bool IsWordLyricsEnabled
        {
            get => _isWordLyricsEnabled;
            set
            {
                if (_isWordLyricsEnabled == value) return;
                _isWordLyricsEnabled = value;
                OnPropertyChanged();
            }
        }

        private bool _isLyricsInterludeAnimationEnabled = true;

        public bool IsLyricsInterludeAnimationEnabled
        {
            get => _isLyricsInterludeAnimationEnabled;
            set
            {
                if (_isLyricsInterludeAnimationEnabled == value) return;
                _isLyricsInterludeAnimationEnabled = value;
                OnPropertyChanged();
            }
        }

        private bool _isLyricsTransitionEnabled = true;

        public bool IsLyricsTransitionEnabled
        {
            get => _isLyricsTransitionEnabled;
            set
            {
                if (_isLyricsTransitionEnabled == value) return;
                _isLyricsTransitionEnabled = value;
                OnPropertyChanged();
            }
        }

        private bool _isWordLyricsLiftEnabled = true;

        public bool IsWordLyricsLiftEnabled
        {
            get => _isWordLyricsLiftEnabled;
            set
            {
                if (_isWordLyricsLiftEnabled == value) return;
                _isWordLyricsLiftEnabled = value;
                OnPropertyChanged();
            }
        }

        private bool _isWordLyricsEmphasisEnabled = true;

        public bool IsWordLyricsEmphasisEnabled
        {
            get => _isWordLyricsEmphasisEnabled;
            set
            {
                if (_isWordLyricsEmphasisEnabled == value) return;
                _isWordLyricsEmphasisEnabled = value;
                OnPropertyChanged();
            }
        }

        private bool _isWordLyricsEmphasisGlowEnabled = true;

        public bool IsWordLyricsEmphasisGlowEnabled
        {
            get => _isWordLyricsEmphasisGlowEnabled;
            set
            {
                if (_isWordLyricsEmphasisGlowEnabled == value) return;
                _isWordLyricsEmphasisGlowEnabled = value;
                OnPropertyChanged();
            }
        }

        private bool _isWordLyricsEdgeFeatherEnabled = true;

        public bool IsWordLyricsEdgeFeatherEnabled
        {
            get => _isWordLyricsEdgeFeatherEnabled;
            set
            {
                if (_isWordLyricsEdgeFeatherEnabled == value) return;
                _isWordLyricsEdgeFeatherEnabled = value;
                OnPropertyChanged();
            }
        }

        private string _lyricsOriginalFontFamily = string.Empty;
        private string _lyricsTranslationFontFamily = string.Empty;
        private string _lyricsRomanizationFontFamily = string.Empty;
        private int _lyricsOriginalFontWeight;
        private int _lyricsTranslationFontWeight;
        private int _lyricsRomanizationFontWeight;

        /// <summary>
        /// 原文歌词字体，留空表示跟随 ClassIsland 全局字体。
        /// </summary>
        public string LyricsOriginalFontFamily
        {
            get => _lyricsOriginalFontFamily;
            set => SetLyricsFontFamily(ref _lyricsOriginalFontFamily, value);
        }

        /// <summary>
        /// 翻译歌词字体，留空表示跟随 ClassIsland 全局字体。
        /// </summary>
        public string LyricsTranslationFontFamily
        {
            get => _lyricsTranslationFontFamily;
            set => SetLyricsFontFamily(ref _lyricsTranslationFontFamily, value);
        }

        /// <summary>
        /// 音译歌词字体，留空表示跟随 ClassIsland 全局字体。
        /// </summary>
        public string LyricsRomanizationFontFamily
        {
            get => _lyricsRomanizationFontFamily;
            set => SetLyricsFontFamily(ref _lyricsRomanizationFontFamily, value);
        }

        /// <summary>
        /// 原文歌词字重，0 表示跟随全局。
        /// </summary>
        public int LyricsOriginalFontWeight
        {
            get => _lyricsOriginalFontWeight;
            set => SetLyricsFontWeight(
                ref _lyricsOriginalFontWeight,
                value,
                nameof(LyricsOriginalFontWeightIndex));
        }

        /// <summary>
        /// 翻译歌词字重，0 表示跟随全局。
        /// </summary>
        public int LyricsTranslationFontWeight
        {
            get => _lyricsTranslationFontWeight;
            set => SetLyricsFontWeight(
                ref _lyricsTranslationFontWeight,
                value,
                nameof(LyricsTranslationFontWeightIndex));
        }

        /// <summary>
        /// 音译歌词字重，0 表示跟随全局。
        /// </summary>
        public int LyricsRomanizationFontWeight
        {
            get => _lyricsRomanizationFontWeight;
            set => SetLyricsFontWeight(
                ref _lyricsRomanizationFontWeight,
                value,
                nameof(LyricsRomanizationFontWeightIndex));
        }

        [JsonIgnore]
        public int LyricsOriginalFontWeightIndex
        {
            get => LyricsTypography.ToFontWeightIndex(LyricsOriginalFontWeight);
            set => LyricsOriginalFontWeight = LyricsTypography.FromFontWeightIndex(value);
        }

        [JsonIgnore]
        public int LyricsTranslationFontWeightIndex
        {
            get => LyricsTypography.ToFontWeightIndex(LyricsTranslationFontWeight);
            set => LyricsTranslationFontWeight = LyricsTypography.FromFontWeightIndex(value);
        }

        [JsonIgnore]
        public int LyricsRomanizationFontWeightIndex
        {
            get => LyricsTypography.ToFontWeightIndex(LyricsRomanizationFontWeight);
            set => LyricsRomanizationFontWeight = LyricsTypography.FromFontWeightIndex(value);
        }

        private void SetLyricsFontFamily(
            ref string field,
            string? value,
            [CallerMemberName] string? propertyName = null)
        {
            var normalizedValue = LyricsTypography.NormalizeFontFamily(value);
            if (field == normalizedValue) return;
            field = normalizedValue;
            OnPropertyChanged(propertyName);
        }

        private void SetLyricsFontWeight(
            ref int field,
            int value,
            string indexPropertyName,
            [CallerMemberName] string? propertyName = null)
        {
            var normalizedValue = LyricsTypography.NormalizeFontWeight(value);
            if (field == normalizedValue) return;
            field = normalizedValue;
            OnPropertyChanged(propertyName);
            OnPropertyChanged(indexPropertyName);
        }

        private bool _isTodayEatSentry = true;

        public bool IsTodayEatSentry
        {
            get => _isTodayEatSentry;
            set
            {
                if (_isTodayEatSentry == value) return;
                _isTodayEatSentry = value;
                OnPropertyChanged();
            }
        }

        private int _progressBarColorMode = 0; // ProgressBarColorMode.ClassIslandTheme

        /// <summary>
        /// 正在播放组件进度条颜色来源。
        /// 0: ClassIsland 主题色；1: 封面主题色（如有，否则回退主题色）。
        /// </summary>
        public int ProgressBarColorMode
        {
            get => _progressBarColorMode;
            set
            {
                if (_progressBarColorMode == value) return;
                _progressBarColorMode = value;
                OnPropertyChanged();
            }
        }

        private bool _mediaLinkIsEnabled;
        private string _mediaLinkListenAddress = "127.0.0.1";
        private int _mediaLinkPort = 21757;
        private string _mediaLinkToken = string.Empty;
        private string _mediaLinkAllowedOrigins = string.Empty;
        private int _mediaLinkTimelineMinIntervalMs = 200;

        public bool MediaLinkIsEnabled
        {
            get => _mediaLinkIsEnabled;
            set
            {
                if (_mediaLinkIsEnabled == value) return;
                _mediaLinkIsEnabled = value;
                OnPropertyChanged();
            }
        }

        public string MediaLinkListenAddress
        {
            get => _mediaLinkListenAddress;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim();
                if (_mediaLinkListenAddress == normalized) return;
                _mediaLinkListenAddress = normalized;
                OnPropertyChanged();
            }
        }

        public int MediaLinkPort
        {
            get => _mediaLinkPort;
            set
            {
                var normalized = value is < 1 or > 65535 ? 21757 : value;
                if (_mediaLinkPort == normalized) return;
                _mediaLinkPort = normalized;
                OnPropertyChanged();
            }
        }

        public string MediaLinkToken
        {
            get => _mediaLinkToken;
            set
            {
                var normalized = value ?? string.Empty;
                if (_mediaLinkToken == normalized) return;
                _mediaLinkToken = normalized;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 允许的 Origin 白名单，分号或逗号分隔。空表示拒绝所有带 Origin 的连接。
        /// </summary>
        public string MediaLinkAllowedOrigins
        {
            get => _mediaLinkAllowedOrigins;
            set
            {
                var normalized = value ?? string.Empty;
                if (_mediaLinkAllowedOrigins == normalized) return;
                _mediaLinkAllowedOrigins = normalized;
                OnPropertyChanged();
            }
        }
        public int MediaLinkTimelineMinIntervalMs
        {
            get => _mediaLinkTimelineMinIntervalMs;
            set
            {
                var normalized = value < 0 ? 200 : value;
                if (_mediaLinkTimelineMinIntervalMs == normalized) return;
                _mediaLinkTimelineMinIntervalMs = normalized;
                OnPropertyChanged();
            }
        }

        private bool _mediaLinkUpstreamIsEnabled;
        private string _mediaLinkUpstreamEndpoint = string.Empty;
        private string _mediaLinkUpstreamToken = string.Empty;

        /// <summary>
        /// 是否消费另一台实例的 MediaLink 推送。收到的媒体与歌词写入注入存储，
        /// 故还需把「媒体源模式」设为外部优先或仅外部注入才会实际生效。
        /// </summary>
        public bool MediaLinkUpstreamIsEnabled
        {
            get => _mediaLinkUpstreamIsEnabled;
            set
            {
                if (_mediaLinkUpstreamIsEnabled == value) return;
                _mediaLinkUpstreamIsEnabled = value;
                OnPropertyChanged();
            }
        }

        /// <summary>上游实例的 WebSocket 地址，形如 <c>ws://192.168.1.201:21757/v1/ws</c>。</summary>
        public string MediaLinkUpstreamEndpoint
        {
            get => _mediaLinkUpstreamEndpoint;
            set
            {
                var normalized = value?.Trim() ?? string.Empty;
                if (_mediaLinkUpstreamEndpoint == normalized) return;
                _mediaLinkUpstreamEndpoint = normalized;
                OnPropertyChanged();
            }
        }

        /// <summary>上游实例的 Token。与本机的 <see cref="MediaLinkToken"/> 无关。</summary>
        public string MediaLinkUpstreamToken
        {
            get => _mediaLinkUpstreamToken;
            set
            {
                var normalized = value ?? string.Empty;
                if (_mediaLinkUpstreamToken == normalized) return;
                _mediaLinkUpstreamToken = normalized;
                OnPropertyChanged();
            }
        }

        private MediaLinkMediaSourceMode _mediaLinkMediaSourceMode = MediaLinkMediaSourceMode.PlatformOnly;
        private bool _mediaLinkUiUsesEffective = true;
        private bool _mediaLinkPushUsesEffective = true;

        public MediaLinkMediaSourceMode MediaLinkMediaSourceMode
        {
            get => _mediaLinkMediaSourceMode;
            set
            {
                if (_mediaLinkMediaSourceMode == value) return;
                _mediaLinkMediaSourceMode = value;
                OnPropertyChanged();
            }
        }

        public bool MediaLinkUiUsesEffective
        {
            get => _mediaLinkUiUsesEffective;
            set
            {
                if (_mediaLinkUiUsesEffective == value) return;
                _mediaLinkUiUsesEffective = value;
                OnPropertyChanged();
            }
        }

        public bool MediaLinkPushUsesEffective
        {
            get => _mediaLinkPushUsesEffective;
            set
            {
                if (_mediaLinkPushUsesEffective == value) return;
                _mediaLinkPushUsesEffective = value;
                OnPropertyChanged();
            }
        }

        private bool _mediaLinkPlaybackIsEnabled;

        /// <summary>
        /// 是否把上游音频放出声。默认关——多数用户只看频谱，出声应是显式选择。
        ///
        /// 不需要与本机采集做互斥设置：播放有内容的前提是当前生效的媒体来自上游，
        /// 而那种情况下本机 loopback 采集本就被仲裁停掉了，回环的前提不存在。
        /// </summary>
        public bool MediaLinkPlaybackIsEnabled
        {
            get => _mediaLinkPlaybackIsEnabled;
            set
            {
                if (_mediaLinkPlaybackIsEnabled == value) return;
                _mediaLinkPlaybackIsEnabled = value;
                OnPropertyChanged();
            }
        }

        private bool _mediaLinkAlignmentIsEnabled;

        /// <summary>
        /// 是否对齐跨机播放的出声时刻。默认关——按需付费的边界：开了对齐的用户才为
        /// 48kHz 端点的重采样器付 CPU，而多数用户两台机器不在同一个房间，对不对齐
        /// 无从听出。
        ///
        /// 只表示用户要不要，不表示此刻能不能：可行性由发送端声明的预算、本机设备
        /// 延迟与对时状态在运行时判，判不过时声音照出、只是不对齐，并记下原因。
        /// </summary>
        public bool MediaLinkAlignmentIsEnabled
        {
            get => _mediaLinkAlignmentIsEnabled;
            set
            {
                if (_mediaLinkAlignmentIsEnabled == value) return;
                _mediaLinkAlignmentIsEnabled = value;
                OnPropertyChanged();
            }
        }

        /// <summary>抖动缓冲的默认目标深度。对 WiFi 的重传与漫游尖峰够用。</summary>
        public const int DefaultPlaybackBufferMs = 200;

        /// <summary>
        /// 下界不取更小：低于一个音频周期（约 20ms）加一次网络重传的量级，
        /// 缓冲会持续欠载，表现为断续。
        /// </summary>
        public const int MinPlaybackBufferMs = 50;

        /// <summary>上界即播放环形缓冲容量的一半，留出被填满也不覆盖未读数据的余量。</summary>
        public const int MaxPlaybackBufferMs = 1000;

        // 上面那对区间给设置页 NumericUpDown 用的 decimal 镜像。那两个属性是 decimal?，
        // 而 {x:Static} 不做 XAML 的字面量类型转换，int 静态量在 AXAML 编译期就被拒。
        // 由上面推导而来，不重新写数字——区间仍只有一个真值源，且 AXAML 引用错名字是编译错误。
        public const decimal PlaybackBufferMsMinimum = MinPlaybackBufferMs;
        public const decimal PlaybackBufferMsMaximum = MaxPlaybackBufferMs;

        private int _mediaLinkPlaybackBufferMs = DefaultPlaybackBufferMs;

        /// <summary>
        /// 抖动缓冲目标深度，毫秒。有线局域网抖动小，调到 80 可显著降低延迟；
        /// WiFi 遇重传或漫游可能出现 200ms 以上尖峰，调低会断续。
        ///
        /// 越界夹紧而不拒绝：设置页里的数字框允许任意输入，
        /// 拒绝会让用户面对一个不生效又不报错的输入框。夹紧后仍发通知，
        /// 否则双向绑定的界面会继续显示越界值而生效值已经变了。
        /// </summary>
        public int MediaLinkPlaybackBufferMs
        {
            get => _mediaLinkPlaybackBufferMs;
            set
            {
                var clamped = Math.Clamp(value, MinPlaybackBufferMs, MaxPlaybackBufferMs);
                if (_mediaLinkPlaybackBufferMs == clamped) return;
                _mediaLinkPlaybackBufferMs = clamped;
                OnPropertyChanged();
            }
        }

        private int _mediaLinkAudioClockBudgetMs = 300;

        /// <summary>
        /// 本机作为发送端时在 server.hello 里声明的播放延迟预算，毫秒。默认 300——
        /// 够盖住一次 TCP 重传加抖动缓冲的稳态深度。所有接收端共用这一个声明值，
        /// 这正是它放在发送端配置里的理由：各接收端自己配就是各自为真，必然失配。
        ///
        /// 不做夹紧：接收端本来就要对任意声明值防御（太小、太大、缺失各有归因），
        /// 发送端再夹一层只是把同一个判断写两遍，而两遍会各自演化。
        /// 改动对已连接的会话不追发，新会话按新值声明。
        /// </summary>
        public int MediaLinkAudioClockBudgetMs
        {
            get => _mediaLinkAudioClockBudgetMs;
            set
            {
                if (_mediaLinkAudioClockBudgetMs == value) return;
                _mediaLinkAudioClockBudgetMs = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 手动偏移的区间下界，毫秒。±500 够覆盖蓝牙链路常见的 100 到 200 毫秒并留余量；
        /// 再往外的值更可能是误操作，而一个夸张的偏移会把对齐推到明显错位。
        /// </summary>
        public const int MinManualOffsetMs = -500;

        /// <summary>手动偏移的区间上界，毫秒。取值理由见 <see cref="MinManualOffsetMs"/>。</summary>
        public const int MaxManualOffsetMs = 500;

        // 设置页 NumericUpDown 用的 decimal 镜像，理由同 PlaybackBufferMsMinimum：
        // {x:Static} 不做字面量类型转换，由 int 常量推导保证区间只有一个真值源。
        public const decimal ManualOffsetMsMinimum = MinManualOffsetMs;
        public const decimal ManualOffsetMsMaximum = MaxManualOffsetMs;

        private Dictionary<string, int> _mediaLinkManualOffsetsMs = new(StringComparer.Ordinal);

        /// <summary>
        /// 按输出设备记忆的手动出声偏移，毫秒。键是系统渲染端点 ID。
        ///
        /// 为什么按设备：偏移补的是自动估计看不见的硬件尾段（DAC、功放、蓝牙），
        /// 那是设备的属性而不是这台机器的属性——蓝牙耳机调好的值对音箱就是错的。
        /// 符号约定跟 native 的 actual 侧：正值表示这台设备真实出声比自动估计更晚。
        ///
        /// 读写走 <see cref="GetManualOffsetMs"/> / <see cref="SetManualOffsetMs"/>；
        /// 属性本身只为序列化暴露。声明 IReadOnlyDictionary 而非 Dictionary：
        /// getter 交出可变字典等于绕过写侧的夹紧与写时复制，原地改写面在类型上堵死。
        /// System.Text.Json 对该接口的收发都支持（反序列化实体化成 Dictionary），
        /// 线格式不变，往返由 ManualOffsets_SurviveJsonRoundTrip 看守。
        /// </summary>
        public IReadOnlyDictionary<string, int> MediaLinkManualOffsetsMs
        {
            get => _mediaLinkManualOffsetsMs;
            set
            {
                // 拷贝而不是收下引用：调用方（含反序列化器之外的任何人）手里若还留着
                // 原字典，事后原地改它就等于绕过只读声明改内部状态——别名一断，
                // 「不再变动的完整快照」这条读侧前提才在所有入口上成立。
                _mediaLinkManualOffsetsMs = value is null
                    ? new(StringComparer.Ordinal)
                    : new Dictionary<string, int>(value, StringComparer.Ordinal);
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 读一个设备的手动偏移。未知设备（含拿不到设备标识）取 0，不取上一个设备的值——
        /// 换设备后沿用旧偏移是一种会让人以为「校准丢了」的错。
        ///
        /// 读侧也夹紧：配置文件是整个字典一次性反序列化进来的，不走逐项 setter，
        /// 手改出界的值只有这里能拦。
        ///
        /// 本方法在探测线程上被对齐重算调用，而写在 UI 线程——先把引用取成本地快照
        /// 再查：写侧是写时复制换引用（见 <see cref="SetManualOffsetMs"/>），
        /// 快照上的读永远面对一个不再变动的完整字典。
        /// </summary>
        public int GetManualOffsetMs(string? deviceId)
        {
            var offsets = _mediaLinkManualOffsetsMs;
            return !string.IsNullOrEmpty(deviceId) && offsets.TryGetValue(deviceId, out var ms)
                ? Math.Clamp(ms, MinManualOffsetMs, MaxManualOffsetMs)
                : 0;
        }

        /// <summary>
        /// 写一个设备的手动偏移。越界夹紧而不拒绝，理由同
        /// <see cref="MediaLinkPlaybackBufferMs"/>；夹紧后同值早退，避免空发通知。
        /// 拿不到设备标识时不写——没有键可挂，写进一个编造的键等于发明平行设备概念。
        ///
        /// 写时复制而不是原地写：读侧在探测线程上无锁查字典，Dictionary 被并发原地
        /// 写时读到中途态的失败形态不止抛异常——桶链可成环，TryGetValue 死循环把
        /// 探测线程永久挂死，任何 try/catch 都兜不住。拷到新字典再换引用，引用替换
        /// 是原子的，旧引用上的读最迟下一轮重算跟上新值。写在 UI 线程上是串行的，
        /// 拷贝之间不互踩；字典最多几十个端点，逐写一拷不构成负担。
        /// </summary>
        public void SetManualOffsetMs(string? deviceId, int valueMs)
        {
            if (string.IsNullOrEmpty(deviceId)) return;
            var clamped = Math.Clamp(valueMs, MinManualOffsetMs, MaxManualOffsetMs);
            var current = _mediaLinkManualOffsetsMs;
            if (current.TryGetValue(deviceId, out var existing) && existing == clamped)
            {
                return;
            }

            _mediaLinkManualOffsetsMs = new Dictionary<string, int>(current, StringComparer.Ordinal)
            {
                [deviceId] = clamped
            };
            OnPropertyChanged(nameof(MediaLinkManualOffsetsMs));
        }

    }
    public class MediaSource : ObservableObject
    {
        private string _source = string.Empty;
        private bool _isEnabled = true;
        private bool? _isLyricsSearchEnabled;
        private string? _iconPath;
        private string _displayName = string.Empty;
        private string _iconStatus = "未解析";
        private Bitmap? _displayIcon;

        public string Source
        {
            get => _source;
            set
            {
                if (_source == value) return;
                _source = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(IsLyricsSearchEnabled));
            }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                OnPropertyChanged();
            }
        }

        public bool IsLyricsSearchEnabled
        {
            get => _isLyricsSearchEnabled ?? IsLyricsSearchEnabledByDefault(Source);
            set
            {
                if (_isLyricsSearchEnabled == value) return;
                _isLyricsSearchEnabled = value;
                OnPropertyChanged();
            }
        }

        public static bool IsLyricsSearchEnabledByDefault(string sourceApp)
        {
            // SPlayer-Next 默认关闭通用歌词搜索：优先走其外部 API 直出歌词。
            return !Helpers.SPlayerNextMediaSource.Matches(sourceApp);
        }

        public string? IconPath
        {
            get => _iconPath;
            set
            {
                if (_iconPath == value) return;
                _iconPath = value;
                OnPropertyChanged();
            }
        }

        private string? _customDisplayName;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CustomDisplayName
        {
            get => _customDisplayName;
            set
            {
                var normalizedValue = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (_customDisplayName == normalizedValue) return;
                _customDisplayName = normalizedValue;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
            }
        }

        [JsonIgnore]
        public string DisplayName
        {
            get => !string.IsNullOrWhiteSpace(CustomDisplayName)
                ? CustomDisplayName
                : string.IsNullOrWhiteSpace(_displayName)
                    ? Source
                    : _displayName;
            set
            {
                if (_displayName == value) return;
                _displayName = value;
                OnPropertyChanged();
            }
        }

        [JsonIgnore]
        public string IconStatus
        {
            get => _iconStatus;
            set
            {
                if (_iconStatus == value) return;
                _iconStatus = value;
                OnPropertyChanged();
            }
        }

        [JsonIgnore]
        public Bitmap? DisplayIcon
        {
            get => _displayIcon;
            set
            {
                if (ReferenceEquals(_displayIcon, value)) return;
                _displayIcon = value;
                OnPropertyChanged();
            }
        }
    }
}

