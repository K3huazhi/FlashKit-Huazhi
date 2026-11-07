using FlashKit.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FlashKit.App.Views;

public sealed partial class ImageToolsPage : Page
{
    public ImageToolsPage()
    {
        ViewModel = App.GetService<ImageToolsViewModel>();
        InitializeComponent();
    }

    public ImageToolsViewModel ViewModel { get; }

    private async void ChooseSource_Click(object sender, RoutedEventArgs e)
    {
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
            ViewModel.SourcePath = file.Path;
        }
    }

    private async void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsPayload)
        {
            var folder = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            folder.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(folder, App.MainWindowHandle);
            var picked = await folder.PickSingleFolderAsync();
            if (picked is not null)
            {
                ViewModel.OutputPath = picked.Path;
            }

            return;
        }

        if (ViewModel.IsConversion)
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                SuggestedFileName = "output.img"
            };
            picker.FileTypeChoices.Add("IMG", new List<string> { ".img" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is not null)
            {
                ViewModel.OutputPath = file.Path;
            }
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await ViewModel.StartAsync();

    private async void OpenOutput_Click(object sender, RoutedEventArgs e) => await ViewModel.OpenOutputAsync();
}
