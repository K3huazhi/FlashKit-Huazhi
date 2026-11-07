using FlashKit.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FlashKit.App.Views;

public sealed partial class BackupPage : Page
{
    private bool _starting;

    public BackupPage()
    {
        ViewModel = App.GetService<BackupViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.ActivateAsync();
        Unloaded += (_, _) => ViewModel.Deactivate();
    }

    public BackupViewModel ViewModel { get; }

    private async void ChangeDestination_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ViewModel.ChangeDestination(folder.Path);
        }
    }

    private async void StartBackup_Click(object sender, RoutedEventArgs e)
    {
        if (_starting || ViewModel.IsBusy || !ViewModel.CanStartBackup)
        {
            return;
        }

        _starting = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = Localization.Strings.Get("BackupConfirmTitle"),
                Content = new TextBlock
                {
                    Text = ViewModel.BuildConfirmationText(),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 560
                },
                PrimaryButtonText = Localization.Strings.Get("BackupConfirmPrimary"),
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await ViewModel.StartBackupAsync();
        }
        finally
        {
            _starting = false;
        }
    }
}
