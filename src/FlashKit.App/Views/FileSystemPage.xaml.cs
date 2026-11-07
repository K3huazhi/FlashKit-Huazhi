using FlashKit.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FlashKit.App.Views;

public sealed partial class FileSystemPage : Page
{
    public FileSystemPage()
    {
        ViewModel = App.GetService<FileSystemViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.ActivateAsync();
    }

    public FileSystemViewModel ViewModel { get; }

    private async void ChooseSource_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsPack)
        {
            var folder = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            folder.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(folder, App.MainWindowHandle);
            var picked = await folder.PickSingleFolderAsync();
            if (picked is not null)
            {
                ViewModel.SourcePath = picked.Path;
            }

            return;
        }

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await ViewModel.SetSourceAsync(file.Path);
        }
    }

    private async void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsPack)
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                SuggestedFileName = "filesystem.img"
            };
            picker.FileTypeChoices.Add("IMG", new List<string> { ".img" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is not null)
            {
                ViewModel.OutputPath = file.Path;
            }

            return;
        }

        var folder = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        folder.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(folder, App.MainWindowHandle);
        var picked = await folder.PickSingleFolderAsync();
        if (picked is not null)
        {
            ViewModel.OutputPath = picked.Path;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await ViewModel.StartAsync();

    private async void OpenOutput_Click(object sender, RoutedEventArgs e) => await ViewModel.OpenOutputAsync();
}
