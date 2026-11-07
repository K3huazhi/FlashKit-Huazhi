using FlashKit.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FlashKit.App.Views;

public sealed partial class RestorePage : Page
{
    public RestorePage()
    {
        ViewModel = App.GetService<RestoreViewModel>();
        InitializeComponent();
    }

    public RestoreViewModel ViewModel { get; }

    private async void ChooseManifest_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await ViewModel.LoadManifestAsync(file.Path);
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var selected = ViewModel.Selected;
        if (selected.Count == 0)
        {
            return;
        }

        var text = $"将写入当前设备：\n\n" +
                   $"分区：{string.Join("、", selected.Select(p => p.Name))}\n" +
                   $"镜像：{selected.Count} 个\n" +
                   $"来源：{ViewModel.ManifestPath}\n\n" +
                   "还原可能触发 AVB/dm-verity 校验失败，导致无法开机；请确认已有可恢复方案。";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认还原",
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "开始还原",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.StartAsync();
    }

    private async void OpenBackupDirectory_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.OpenBackupDirectoryAsync();
}
