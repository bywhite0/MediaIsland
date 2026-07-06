using CommunityToolkit.Mvvm.ComponentModel;

namespace MediaIsland.Components;

public class LyricsComponentConfig : ObservableRecipient
{
    private bool _isHideWhenEmpty;
    private bool _isHideWhenPaused;
    private bool _isShowStatusText = true;

    public bool IsHideWhenPaused
    {
        get => _isHideWhenPaused;
        set
        {
            if (_isHideWhenPaused == value) return;
            _isHideWhenPaused = value;
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
}
