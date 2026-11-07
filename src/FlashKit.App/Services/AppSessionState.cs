using FlashKit.App.ViewModels;

namespace FlashKit.App.Services;

/// <summary>
/// 跨页面共享的运行状态。刷写期间由 FlashViewModel 置位，主窗口据此锁定导航。
/// 单独拆出来是为了避免 MainViewModel 与 FlashViewModel 相互依赖。
/// </summary>
public sealed class AppSessionState : ObservableObject
{
    private bool _isFlashing;
    private string _flashingDescription = string.Empty;

    public bool IsFlashing
    {
        get => _isFlashing;
        set
        {
            if (SetProperty(ref _isFlashing, value))
            {
                OnPropertyChanged(nameof(IsNavigationLocked));
            }
        }
    }

    public string FlashingDescription
    {
        get => _flashingDescription;
        set => SetProperty(ref _flashingDescription, value);
    }

    public bool IsNavigationLocked => IsFlashing;
}
