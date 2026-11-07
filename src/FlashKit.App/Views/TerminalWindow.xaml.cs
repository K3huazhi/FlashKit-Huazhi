using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace FlashKit.App.Views;

public sealed partial class TerminalWindow : Window
{
    public TerminalWindow()
    {
        InitializeComponent();
        Title = $"{Localization.Strings.Get("L008")} - FlashKit";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(980, 700));
        CenterOnScreen();
        ContentFrame.Navigate(typeof(TerminalPage));
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(
            area.X + (area.Width - AppWindow.Size.Width) / 2,
            area.Y + (area.Height - AppWindow.Size.Height) / 2));
    }
}
