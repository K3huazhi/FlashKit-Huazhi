using FlashKit.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FlashKit.App.Views;

public sealed partial class PackagePage : Page
{
    public PackagePage()
    {
        ViewModel = App.GetService<PackageViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.ActivateAsync();
    }

    public PackageViewModel ViewModel { get; }

    private async void SelectPackage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add(".zip");
        picker.FileTypeFilter.Add(".bin");
        picker.FileTypeFilter.Add(".br");
        picker.FileTypeFilter.Add(".dat");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ViewModel.SetPackagePath(file.Path);
        }
    }

    private async void StartExtract_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.PackagePath))
        {
            await ShowMessageAsync("请选择卡刷包", "请先选择 payload.bin、OTA ZIP 或纯 img ZIP。");
            return;
        }

        if (Directory.Exists(ViewModel.ExtractOutputPreview) &&
            Directory.EnumerateFileSystemEntries(ViewModel.ExtractOutputPreview).Any())
        {
            var dialog = new ContentDialog
            {
                Title = "输出目录已存在",
                Content = new TextBlock
                {
                    Text = $"目录已存在且非空：\n{ViewModel.ExtractOutputPreview}\n\n继续会先删除该目录再解包。",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "删除并重新解包",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await ViewModel.StartExtractAsync(overwriteExisting: true);
            return;
        }

        await ViewModel.StartExtractAsync();
    }

    private async void AddImages_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add(".img");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 })
        {
            ViewModel.AddImages(files.Select(f => f.Path));
        }
    }

    private void RemoveImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PackImageViewModel image })
        {
            ViewModel.RemoveImage(image);
        }
    }

    private async void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = "flashkit-images"
        };
        picker.FileTypeChoices.Add("ZIP 卡刷包", new List<string> { ".zip" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            ViewModel.PackOutputPath = file.Path;
        }
    }

    private async void StartPack_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.PackOutputPath))
        {
            await ShowMessageAsync("请选择输出路径", "请先点击“输出到…”选择 ZIP 保存位置。");
            return;
        }

        var outputInfo = new FileInfo(ViewModel.PackOutputPath);
        if (outputInfo.Exists && outputInfo.Length > 0)
        {
            var dialog = new ContentDialog
            {
                Title = "覆盖已有 ZIP？",
                Content = $"目标文件已存在：\n{ViewModel.PackOutputPath}\n\n继续将覆盖该文件。",
                PrimaryButtonText = "覆盖",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        await ViewModel.StartPackAsync();
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        await new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        }.ShowAsync();
    }
}
