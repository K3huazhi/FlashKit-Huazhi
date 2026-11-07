using FlashKit.App.ViewModels;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlashKit.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly IProcessRunner _processRunner = App.GetService<IProcessRunner>();
    private bool _initialized;

    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            await ViewModel.InitializeAsync();
        };
    }

    public SettingsViewModel ViewModel { get; }

    private void OpenDriverGuide_Click(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(DriverGuidePage));
    }

    private async void OpenDeviceManager_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _processRunner.RunAsync(new ProcessRequest
            {
                ExecutablePath = "mmc.exe",
                WorkingDirectory = Environment.SystemDirectory,
                Arguments = ["devmgmt.msc"],
                LogToHistory = true,
                Category = "Driver",
                Purpose = "打开设备管理器"
            });
        }
        catch (Exception ex)
        {
            await new ContentDialog
            {
                Title = "无法打开设备管理器",
                Content = ex.Message,
                CloseButtonText = "关闭",
                XamlRoot = XamlRoot
            }.ShowAsync();
        }
    }
}
