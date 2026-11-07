using System.Runtime.InteropServices;
using FlashKit.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace FlashKit.App.Services;

/// <summary>
/// 终端 / 原始日志窗口的单例生命周期管理。
/// 已打开时只激活并置顶，不重复 new Window；关闭后引用清空，可再次打开。
/// </summary>
public sealed class WindowManager
{
    private TerminalWindow? _terminalWindow;
    private LogWindow? _logWindow;

    public WindowManager()
    {
    }

    public void ShowTerminal()
    {
        if (_terminalWindow is not null)
        {
            BringToFront(_terminalWindow);
            return;
        }

        _terminalWindow = new TerminalWindow();
        _terminalWindow.Closed += (_, _) => _terminalWindow = null;
        _terminalWindow.Activate();
    }

    public void ShowLogs()
    {
        if (_logWindow is not null)
        {
            BringToFront(_logWindow);
            return;
        }

        _logWindow = new LogWindow();
        _logWindow.Closed += (_, _) => _logWindow = null;
        _logWindow.Activate();
    }

    public void CloseAll()
    {
        try
        {
            _terminalWindow?.Close();
        }
        catch
        {
            // 窗口可能已经关闭。
        }

        try
        {
            _logWindow?.Close();
        }
        catch
        {
            // 窗口可能已经关闭。
        }

        _terminalWindow = null;
        _logWindow = null;
    }

    private static void BringToFront(Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var appWindow = AppWindow.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
            if (appWindow.Presenter is OverlappedPresenter presenter &&
                presenter.State == OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            window.Activate();
            SetForegroundWindow(hwnd);
            ShowWindow(hwnd, SwRestore);
        }
        catch
        {
            // 激活失败时退回普通 Activate。
            window.Activate();
        }
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
