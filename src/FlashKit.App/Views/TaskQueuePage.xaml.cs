using FlashKit.App.ViewModels;
using FlashKit.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlashKit.App.Views;

public sealed partial class TaskQueuePage : Page
{
    public TaskQueuePage()
    {
        ViewModel = App.GetService<TaskQueueViewModel>();
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Refresh();
    }

    public TaskQueueViewModel ViewModel { get; }

    private void ClearCompleted_Click(object sender, RoutedEventArgs e) => ViewModel.ClearCompleted();

    private void OpenLog_Click(object sender, RoutedEventArgs e) => ViewModel.WindowManager.ShowLogs();

    private async void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: OperationTaskRecord task })
        {
            await ViewModel.OpenOutputAsync(task);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: OperationTaskRecord task })
        {
            ViewModel.CancelTask(task);
        }
    }
}
