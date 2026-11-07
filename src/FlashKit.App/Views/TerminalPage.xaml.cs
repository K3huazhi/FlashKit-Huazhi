using FlashKit.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FlashKit.App.Views;

public sealed partial class TerminalPage : Page
{
    public TerminalPage()
    {
        ViewModel = App.GetService<TerminalViewModel>();
        InitializeComponent();
        Loaded += (_, _) => CommandBox.Focus(FocusState.Programmatic);
    }

    public TerminalViewModel ViewModel { get; }

    private void OutputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        OutputBox.Select(OutputBox.Text.Length, 0);
    }

    private void CommandBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !IsShiftDown())
        {
            e.Handled = true;
            if (ViewModel.RunCommand.CanExecute(null))
            {
                ViewModel.RunCommand.Execute(null);
            }
        }
        else if (e.Key == VirtualKey.Up)
        {
            ViewModel.PreviousHistory();
            CommandBox.SelectionStart = CommandBox.Text.Length;
        }
        else if (e.Key == VirtualKey.Down)
        {
            ViewModel.NextHistory();
            CommandBox.SelectionStart = CommandBox.Text.Length;
        }
    }

    private static bool IsShiftDown() =>
        (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) &
         Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
}
