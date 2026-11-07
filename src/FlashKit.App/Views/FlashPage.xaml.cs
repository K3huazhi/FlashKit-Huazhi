using FlashKit.App.ViewModels;
using FlashKit.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace FlashKit.App.Views;

public sealed partial class FlashPage : Page
{
    private bool _starting;

    public FlashPage()
    {
        ViewModel = App.GetService<FlashViewModel>();
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.ActivateAsync();
        Unloaded += (_, _) => ViewModel.Deactivate();
    }

    public FlashViewModel ViewModel { get; }

    private async void AddManualTask_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickImageAsync();
        if (path is not null)
        {
            ViewModel.AddManualTask(path);
        }
    }

    private async Task<string?> PickImageAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add(".img");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async void ScanFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            await ViewModel.ScanFolderAsync(folder.Path);
        }
    }

    private void RemoveTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FlashTaskViewModel task })
        {
            ViewModel.RemoveTask(task);
        }
    }

    private async void StartFlash_Click(object sender, RoutedEventArgs e)
    {
        if (_starting || ViewModel.IsFlashing)
        {
            return;
        }

        _starting = true;
        try
        {
            var report = await ViewModel.BuildPreflightAsync();
            var dialog = BuildConfirmationDialog(report);
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            var summary = await ViewModel.StartFlashAsync(report);
            if (summary is not null)
            {
                await ShowResultAsync(summary);
            }
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("刷入失败", ex.Message);
        }
        finally
        {
            _starting = false;
        }
    }

    private ContentDialog BuildConfirmationDialog(FlashPreflightReport report)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = $"设备：{report.Options.Serial}",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        panel.Children.Add(new TextBlock
        {
            Text = report.SummaryText + (report.IsFastbootd ? " · Fastbootd" : " · Bootloader"),
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        if (report.HasCritical)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "⚠ 清单包含高危分区（persist / modem / efs / xbl 等）。刷错可能导致设备无法开机、基带丢失或设备身份信息损坏。",
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
        }

        if (report.BlockingMessages.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "阻断项",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
            });
            panel.Children.Add(BuildIssueList(report.BlockingMessages, "SystemFillColorCriticalBrush"));
        }

        if (report.WarningMessages.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "警告",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"]
            });
            panel.Children.Add(BuildIssueList(report.WarningMessages, "SystemFillColorCautionBrush"));
        }

        CheckBox? confirmCheckBox = null;
        if (report.HasCritical)
        {
            confirmCheckBox = new CheckBox
            {
                Content = "我已知晓高危分区风险，并确认继续刷入。",
                IsChecked = false
            };
            panel.Children.Add(confirmCheckBox);
        }

        var dialog = new ContentDialog
        {
            Title = report.IsValid ? "确认开始刷入" : "预检未通过",
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 480,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            XamlRoot = XamlRoot,
            CloseButtonText = report.IsValid ? "取消" : "关闭",
            PrimaryButtonText = report.IsValid ? "开始刷入" : string.Empty,
            DefaultButton = ContentDialogButton.Close
        };

        if (!report.IsValid)
        {
            dialog.PrimaryButtonText = string.Empty;
            return dialog;
        }

        if (confirmCheckBox is not null)
        {
            dialog.IsPrimaryButtonEnabled = false;
            confirmCheckBox.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = true;
            confirmCheckBox.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = false;
        }

        return dialog;
    }

    private static ScrollViewer BuildIssueList(IReadOnlyList<string> issues, string brushKey) =>
        new()
        {
            MaxHeight = 150,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock
            {
                Text = string.Join(Environment.NewLine, issues.Select(i => "• " + i)),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources[brushKey]
            }
        };

    private async Task ShowResultAsync(FlashRunSummary summary)
    {
        var details = string.Join(
            Environment.NewLine,
            summary.Results.Select(r =>
                $"{r.Item.PartitionName}：{StateText(r.State)}" +
                (r.ExitCode is { } code ? $"（退出码 {code}）" : string.Empty) +
                (string.IsNullOrWhiteSpace(r.Message) ? string.Empty : $" · {r.Message}")));
        var dialog = new ContentDialog
        {
            Title = summary.FailureCount > 0 ? "刷入结束，存在失败项" : "刷入结束",
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap }
            },
            XamlRoot = XamlRoot,
            CloseButtonText = "关闭"
        };
        await dialog.ShowAsync();
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

    private static string StateText(FlashItemState state) => state switch
    {
        FlashItemState.Succeeded => "成功",
        FlashItemState.Failed => "失败",
        FlashItemState.Skipped => "跳过",
        FlashItemState.Cancelled => "取消",
        _ => state.ToString()
    };
}
