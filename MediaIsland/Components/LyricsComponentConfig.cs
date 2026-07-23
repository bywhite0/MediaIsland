using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

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
