using System.Collections.Specialized;
using FlashKit.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlashKit.App.Views;

public sealed partial class LogPage : Page
{
    public LogPage()
    {
        ViewModel = App.GetService<LogViewModel>();
        InitializeComponent();
        ViewModel.ScrollToEndRequested += OnScrollToEndRequested;
        ViewModel.Entries.CollectionChanged += OnEntriesChanged;
        Unloaded += (_, _) =>
        {
            ViewModel.ScrollToEndRequested -= OnScrollToEndRequested;
            ViewModel.Entries.CollectionChanged -= OnEntriesChanged;
        };
    }

    public LogViewModel ViewModel { get; }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.AutoScroll && ViewModel.Entries.Count > 0)
        {
            LogList.ScrollIntoView(ViewModel.Entries[^1]);
        }
    }

    private void OnScrollToEndRequested(object? sender, EventArgs e)
    {
        if (ViewModel.Entries.Count > 0)
        {
            LogList.ScrollIntoView(ViewModel.Entries[^1]);
        }
    }
}
