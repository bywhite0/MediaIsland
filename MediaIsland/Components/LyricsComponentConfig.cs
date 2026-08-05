using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaIsland.Services.Lyrics.Models;

namespace MediaIsland.Components;

public class LyricsComponentConfig : ObservableRecipient
{
    private bool _isHideWhenEmpty;
    private bool _isShowStatusText = true;
    private bool _isShowNoteIcon = true;
    private bool _isFixedWidthToMaxLineEnabled;
    private bool _isLeftNegativeMargin;
    private bool _isRightNegativeMargin;
    private double _maxContentWidth;
    private bool _isScrollWhenOverflow;
    private int _renderFrameRate = 30;
    private double _lineSpacing;
    private LyricsDisplayPart _displayPart = LyricsDisplayPart.Original;
    private bool _isShowLyricsKana;

    /// <summary>
    /// 多行歌词的附加行距（像素，可为负）。部分日文字体行高偏大，
    /// 取负值可压缩多行间距；取正值则让主歌词与背景人声行分得更开。
    /// </summary>
    public double LineSpacing
    {
        get => _lineSpacing;
        set
        {
            var normalizedValue = double.IsFinite(value) ? Math.Clamp(value, -8, 8) : 0;
            if (Math.Abs(_lineSpacing - normalizedValue) < 0.001) return;
            _lineSpacing = normalizedValue;
            OnPropertyChanged();
        }
    }

    public bool IsHideWhenEmpty
    {
        get => _isHideWhenEmpty;
        set
        {
            if (_isHideWhenEmpty == value) return;
            _isHideWhenEmpty = value;
            OnPropertyChanged();
        }
    }

    public bool IsShowStatusText
    {
        get => _isShowStatusText;
        set
        {
            if (_isShowStatusText == value) return;
            _isShowStatusText = value;
            OnPropertyChanged();
        }
    }

    public bool IsShowNoteIcon
    {
        get => _isShowNoteIcon;
        set
        {
            if (_isShowNoteIcon == value) return;
            _isShowNoteIcon = value;
            OnPropertyChanged();
        }
    }


    /// <summary>
    /// 按当前歌曲最长歌词行固定歌词区宽度，避免一首歌内组件宽度随换行连续变化。
    /// </summary>
    public bool IsFixedWidthToMaxLineEnabled
    {
        get => _isFixedWidthToMaxLineEnabled;
        set
        {
            if (_isFixedWidthToMaxLineEnabled == value) return;
            _isFixedWidthToMaxLineEnabled = value;
            OnPropertyChanged();
        }
    }

    public bool IsLeftNegativeMargin
    {
        get => _isLeftNegativeMargin;
        set
        {
            if (_isLeftNegativeMargin == value) return;
            _isLeftNegativeMargin = value;
            OnPropertyChanged();
        }
    }

    public bool IsRightNegativeMargin
    {
        get => _isRightNegativeMargin;
        set
        {
            if (_isRightNegativeMargin == value) return;
            _isRightNegativeMargin = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 歌词区的最大宽度（像素）。0 表示不限制，保持组件随内容自由伸展的旧行为。
    /// 超出该宽度时按 <see cref="IsScrollWhenOverflow"/> 决定截断或滚动。
    /// </summary>
    public double MaxContentWidth
    {
        get => _maxContentWidth;
        set
        {
            var normalizedValue = double.IsFinite(value) && value > 0 ? Math.Clamp(value, 40, 1000) : 0;
            if (Math.Abs(_maxContentWidth - normalizedValue) < 0.001) return;
            _maxContentWidth = normalizedValue;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 内容超出 <see cref="MaxContentWidth"/> 时横向滚动展示，而不是截断为省略号。
    /// </summary>
    public bool IsScrollWhenOverflow
    {
        get => _isScrollWhenOverflow;
        set
        {
            if (_isScrollWhenOverflow == value) return;
            _isScrollWhenOverflow = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 控制歌词组件展示原文、翻译或音译。
    /// Translation/Romanization 缺失时显示原文；TranslationOnly/RomanizationOnly 缺失时不显示。
    /// </summary>
    public LyricsDisplayPart DisplayPart
    {
        get => _displayPart;
        set
        {
            if (!Enum.IsDefined(typeof(LyricsDisplayPart), value))
            {
                value = LyricsDisplayPart.Original;
            }

            if (_displayPart == value) return;
            _displayPart = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayPartIndex));
        }
    }

    /// <summary>
    /// 在原文上方显示歌词源自带的字级假名（QRC/KRC 的 <c>[kana:]</c>）。
    /// 仅当实际展示原文时生效；与音译 DisplayPart 无关，默认关闭。
    /// </summary>
    public bool IsShowLyricsKana
    {
        get => _isShowLyricsKana;
        set
        {
            if (_isShowLyricsKana == value) return;
            _isShowLyricsKana = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public int DisplayPartIndex
    {
        get => (int)DisplayPart;
        set => DisplayPart = Enum.IsDefined(typeof(LyricsDisplayPart), value)
            ? (LyricsDisplayPart)value
            : LyricsDisplayPart.Original;
    }

    public int RenderFrameRate
    {
        get => _renderFrameRate;
        set
        {
            var normalizedValue = value == 60 ? 60 : 30;
            if (_renderFrameRate == normalizedValue) return;
            _renderFrameRate = normalizedValue;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RenderFrameRateIndex));
        }
    }

    [JsonIgnore]
    public int RenderFrameRateIndex
    {
        get => RenderFrameRate == 60 ? 1 : 0;
        set => RenderFrameRate = value == 1 ? 60 : 30;
    }
}
