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
    private int _renderFrameRate = 30;
    private LyricsDisplayPart _displayPart = LyricsDisplayPart.Original;

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
    /// 控制歌词组件展示原文、翻译或音译。翻译/音译缺失时回退到原文。
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
