using FlashKit.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;

namespace FlashKit.App.Views;

public sealed partial class FileManagerPage : Page
{
    public FileManagerPage()
    {
        ViewModel = App.GetService<FileManagerViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.ActivateAsync();
    }

    public FileManagerViewModel ViewModel { get; }

    private async void Up_Click(object sender, RoutedEventArgs e) => await ViewModel.GoUpAsync();

    private async void Go_Click(object sender, RoutedEventArgs e) => await ViewModel.NavigateAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private async void Path_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            await ViewModel.NavigateAsync();
        }
    }

    private async void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        await ViewModel.EnterSelectedAsync();
    }

    private async void UploadFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await ViewModel.UploadAsync(file.Path);
        }
    }

    private async void UploadFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await ViewModel.UploadAsync(folder.Path);
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await ViewModel.DownloadAsync(folder.Path);
        }
    }

    private async void OpenLocal_Click(object sender, RoutedEventArgs e) => await ViewModel.OpenLocalAsync();
}
