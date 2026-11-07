using FlashKit.App.Services;
using FlashKit.App.ViewModels;
using FlashKit.App.Views;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace FlashKit.App;

public sealed partial class MainWindow : Window
{
    private const int MinWidth = 800;
    private const int MinHeight = 620;
    private string _currentTag = "device";
    private bool _suppressSelection;
    private bool _isReady;

    public MainWindow()
    {
        ViewModel = App.GetService<MainViewModel>();
        InitializeComponent();
        Title = Localization.Strings.Get("L001");
        RootLayout.DataContext = ViewModel;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        if (!MicaController.IsSupported())
        {
            SystemBackdrop = null;
            RootLayout.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        }

        AppWindow.Resize(new SizeInt32(1240, 840));
        CenterOnScreen();
        TrySetIcon();

        AppWindow.Changed += (_, args) =>
        {
            if (!args.DidSizeChange)
            {
                return;
            }

            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored } &&
                (AppWindow.Size.Width < MinWidth || AppWindow.Size.Height < MinHeight))
            {
                AppWindow.Resize(new SizeInt32(
                    Math.Max(MinWidth, AppWindow.Size.Width),
                    Math.Max(MinHeight, AppWindow.Size.Height)));
            }

            // 800-1000px 时自动收窄为图标栏，把宽度留给表格和刷入清单。
            if (AppWindow.Size.Width < 1000 && RootNavigationView.IsPaneOpen && _isReady)
            {
                RootNavigationView.IsPaneOpen = false;
            }
        };

        RootNavigationView.Loaded += (_, _) =>
        {
            _isReady = true;
            try
            {
                if (RootFrame.Content is null)
                {
                    RootFrame.Navigate(typeof(DevicePage));
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(
                    Path.Combine(AppContext.BaseDirectory, "navigation-error.log"),
                    ex.ToString());
            }
        };

        Closed += OnClosed;
    }

    public MainViewModel ViewModel { get; }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        RootNavigationView.IsPaneOpen = !RootNavigationView.IsPaneOpen;
    }

    private void RootNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_isReady || _suppressSelection || args.SelectedItemContainer is not NavigationViewItem item ||
            item.Tag is not string tag)
        {
            return;
        }

        if (ViewModel.IsFlashing && tag != _currentTag)
        {
            SelectTag(_currentTag);
            return;
        }

        if (tag == _currentTag && RootFrame.Content is not null)
        {
            return;
        }

        NavigateToTag(tag);
    }

    private void NavigateToTag(string tag)
    {
        _currentTag = tag;
        var pageType = tag switch
        {
            "tasks" => typeof(TaskQueuePage),
            "backup" => typeof(BackupPage),
            "restore" => typeof(RestorePage),
            "flash" => typeof(FlashPage),
            "package" => typeof(PackagePage),
            "image" => typeof(ImageToolsPage),
            "files" => typeof(FileManagerPage),
            "filesystem" => typeof(FileSystemPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(DevicePage)
        };

        if (RootFrame.CurrentSourcePageType != pageType)
        {
            RootFrame.Navigate(pageType);
        }
    }

    private void SelectTag(string tag)
    {
        _suppressSelection = true;
        try
        {
            var item = RootNavigationView.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(i => i.Tag as string == tag);
            RootNavigationView.SelectedItem = item;
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsFlashing)
        {
            SelectTag(_currentTag);
            return;
        }

        _suppressSelection = true;
        try
        {
            foreach (var item in RootNavigationView.MenuItems.OfType<NavigationViewItem>())
            {
                item.IsSelected = false;
            }

            RootNavigationView.SelectedItem = null;
        }
        finally
        {
            _suppressSelection = false;
        }

        NavigateToTag("settings");
    }

    private void TerminalButton_Click(object sender, RoutedEventArgs e) =>
        App.Current.WindowManager.ShowTerminal();

    private void LogButton_Click(object sender, RoutedEventArgs e) =>
        App.Current.WindowManager.ShowLogs();

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(
            area.X + (area.Width - AppWindow.Size.Width) / 2,
            area.Y + (area.Height - AppWindow.Size.Height) / 2));
    }

    private void TrySetIcon()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
            }
        }
        catch
        {
            // 图标不是关键功能。
        }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        await App.Current.ShutdownAsync();
    }
}

