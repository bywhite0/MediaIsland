using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MediaIsland.Components;

public class LyricsComponentConfig : ObservableRecipient
{
    private bool _isHideWhenEmpty;
    private bool _isShowStatusText = true;
    private bool _isShowNoteIcon = true;
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
