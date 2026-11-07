using FlashKit.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlashKit.App.Views;

public sealed partial class DevicePage : Page
{
    public DevicePage()
    {
        ViewModel = App.GetService<DeviceViewModel>();
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Activate();
        Unloaded += (_, _) => ViewModel.Deactivate();
    }

    public DeviceViewModel ViewModel { get; }

    private void OpenDriverGuide_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(DriverGuidePage));
    }
}
