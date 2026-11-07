using FlashKit.Core.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlashKit.App.Views;

public sealed partial class DriverGuidePage : Page
{
    private readonly IProcessRunner _processRunner = App.GetService<IProcessRunner>();

    public DriverGuidePage()
    {
        InitializeComponent();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
        {
            Frame.GoBack();
        }
    }

    private async void OpenDeviceManager_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _processRunner.RunAsync(new Core.Models.ProcessRequest
            {
                ExecutablePath = "mmc.exe",
                WorkingDirectory = Environment.SystemDirectory,
                Arguments = ["devmgmt.msc"],
                LogToHistory = true,
                Category = "Driver",
                Purpose = "打开设备管理器"
            });
        }
        catch
        {
            // 打不开时不影响页面其他操作。
        }
    }

    private async void OpenGoogleDriver_Click(object sender, RoutedEventArgs e) =>
        await OpenAsync("https://developer.android.com/studio/run/win-usb");

    private async void OpenSamsung_Click(object sender, RoutedEventArgs e) =>
        await OpenAsync("https://developer.samsung.com/android-usb-driver");

    private async void OpenXiaomi_Click(object sender, RoutedEventArgs e) =>
        await OpenAsync("https://www.mi.com/global/service/support/");

    private async void OpenOppo_Click(object sender, RoutedEventArgs e) =>
        await OpenAsync("https://www.oppo.com/en/support/");

    private async void OpenVivo_Click(object sender, RoutedEventArgs e) =>
        await OpenAsync("https://www.vivo.com/en/support/");

    private async void OpenHuawei_Click(object sender, RoutedEventArgs e) =>
        await OpenAsync("https://consumer.huawei.com/en/support/");

    private async Task OpenAsync(string url)
    {
        try
        {
            await _processRunner.OpenUrlAsync(url);
        }
        catch (Exception ex)
        {
            await new ContentDialog
            {
                Title = "无法打开链接",
                Content = ex.Message,
                CloseButtonText = "关闭",
                XamlRoot = XamlRoot
            }.ShowAsync();
        }
    }
}
