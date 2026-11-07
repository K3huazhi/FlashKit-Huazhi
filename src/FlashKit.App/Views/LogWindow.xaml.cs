using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace FlashKit.App.Views;

public sealed partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        Title = $"{Localization.Strings.Get("L009")} - FlashKit";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1160, 760));
        CenterOnScreen();
        ContentFrame.Navigate(typeof(LogPage));
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(
            area.X + (area.Width - AppWindow.Size.Width) / 2,
            area.Y + (area.Height - AppWindow.Size.Height) / 2));
    }
}
