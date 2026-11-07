using System.Collections.ObjectModel;
using System.Text;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace FlashKit.App.ViewModels;

public sealed class LogViewModel : ObservableObject
{
    private const int MaxEntries = 5_000;
    private readonly ILoggingService _logging;
    private readonly DispatcherQueue? _dispatcher;
    private LogEntryViewModel? _selectedEntry;
    private bool _autoScroll = true;

    public LogViewModel(ILoggingService logging)
    {
        _logging = logging;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        foreach (var entry in _logging.Snapshot().TakeLast(MaxEntries))
        {
            Entries.Add(new LogEntryViewModel(entry));
        }

        _logging.EntryAdded += (_, entry) => RunOnUi(() =>
        {
            Entries.Add(new LogEntryViewModel(entry));
            while (Entries.Count > MaxEntries)
            {
                Entries.RemoveAt(0);
            }

            OnPropertyChanged(nameof(CountText));
            if (_autoScroll)
            {
                ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
            }
        });
        _logging.Cleared += (_, _) => RunOnUi(() =>
        {
            Entries.Clear();
            SelectedEntry = null;
            OnPropertyChanged(nameof(CountText));
        });

        ClearCommand = new RelayCommand(() => _logging.Clear());
        CopyAllCommand = new RelayCommand(CopyAll);
        CopySelectedCommand = new RelayCommand(CopySelected, () => SelectedEntry is not null);
    }

    public ObservableCollection<LogEntryViewModel> Entries { get; } = new();

    public event EventHandler? ScrollToEndRequested;

    public LogEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value))
            {
                CopySelectedCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(HasSelection), nameof(SelectedDetailText));
            }
        }
    }

    public bool HasSelection => SelectedEntry is not null;

    public string SelectedDetailText => SelectedEntry?.ToDetailText() ?? "选择一条日志查看完整 stdout / stderr。";

    public string CountText => $"{Entries.Count} 条";

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    public RelayCommand ClearCommand { get; }
    public RelayCommand CopyAllCommand { get; }
    public RelayCommand CopySelectedCommand { get; }

    private void CopyAll()
    {
        var builder = new StringBuilder();
        foreach (var entry in Entries)
        {
            builder.AppendLine(entry.ToClipboardText());
        }

        SetClipboard(builder.ToString());
    }

    private void CopySelected()
    {
        if (SelectedEntry is not null)
        {
            SetClipboard(SelectedEntry.ToClipboardText());
        }
    }

    private static void SetClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }
}

public sealed class LogEntryViewModel
{
    private readonly LogEntry _entry;

    public LogEntryViewModel(LogEntry entry)
    {
        _entry = entry;
    }

    public LogKind Kind => _entry.Kind;
    public string TimeText => _entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
    public string Category => _entry.Category;
    public string Title => _entry.Kind switch
    {
        LogKind.Command => _entry.CommandLine ?? "(无命令行)",
        LogKind.Error => _entry.Message ?? "错误",
        LogKind.Warning => _entry.Message ?? "警告",
        _ => _entry.Message ?? _entry.Summary
    };

    public string ExitText => _entry.Kind == LogKind.Command
        ? _entry.TimedOut ? "超时" : _entry.Cancelled ? "取消" : $"退出码 {_entry.ExitCode}"
        : string.Empty;

    public string DurationText => _entry.Duration is { } duration ? $"{duration.TotalSeconds:0.0}s" : string.Empty;

    public string Preview
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_entry.StandardError))
            {
                return FirstLine(_entry.StandardError);
            }

            if (!string.IsNullOrWhiteSpace(_entry.StandardOutput))
            {
                return FirstLine(_entry.StandardOutput);
            }

            return _entry.Message ?? string.Empty;
        }
    }

    public string ToDetailText()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"时间：{_entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}");
        builder.AppendLine($"类型：{_entry.Kind}");
        builder.AppendLine($"分类：{_entry.Category}");
        if (!string.IsNullOrWhiteSpace(_entry.CommandLine))
        {
            builder.AppendLine($"命令：{_entry.CommandLine}");
            builder.AppendLine($"退出码：{_entry.ExitCode?.ToString() ?? "无"}");
        }

        if (!string.IsNullOrWhiteSpace(_entry.Message))
        {
            builder.AppendLine($"消息：{_entry.Message}");
        }

        builder.AppendLine();
        builder.AppendLine("── stdout ──");
        builder.AppendLine(string.IsNullOrWhiteSpace(_entry.StandardOutput) ? "(空)" : _entry.StandardOutput.TrimEnd());
        builder.AppendLine();
        builder.AppendLine("── stderr ──");
        builder.AppendLine(string.IsNullOrWhiteSpace(_entry.StandardError) ? "(空)" : _entry.StandardError.TrimEnd());
        return builder.ToString();
    }

    public string ToClipboardText()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"[{TimeText}] [{Category}] {Title} {(string.IsNullOrEmpty(ExitText) ? "" : $"({ExitText})")}");
        if (!string.IsNullOrWhiteSpace(_entry.StandardOutput))
        {
            builder.AppendLine(_entry.StandardOutput.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(_entry.StandardError))
        {
            builder.AppendLine(_entry.StandardError.TrimEnd());
        }

        return builder.ToString();
    }

    private static string FirstLine(string text)
    {
        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
