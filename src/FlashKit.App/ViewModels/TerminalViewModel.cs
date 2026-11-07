using System.Text;
using FlashKit.App.Services;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using Microsoft.UI.Dispatching;

namespace FlashKit.App.ViewModels;

public sealed class TerminalViewModel : ObservableObject
{
    private const int MaxChars = 400_000;
    private readonly ITerminalCommandService _terminal;
    private readonly IDeviceManager _deviceManager;
    private readonly ILoggingService _logging;
    private readonly DispatcherQueue _dispatcher;
    private readonly List<string> _history = new();
    private readonly StringBuilder _output = new();
    private CancellationTokenSource? _cts;
    private string _commandText = string.Empty;
    private string _outputText = string.Empty;
    private bool _isRunning;
    private int _historyIndex;

    public TerminalViewModel(
        ITerminalCommandService terminal,
        IDeviceManager deviceManager,
        ILoggingService logging)
    {
        _terminal = terminal;
        _deviceManager = deviceManager;
        _logging = logging;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _deviceManager.DevicesChanged += (_, _) =>
        {
            RunOnUi(() => OnPropertyChanged(nameof(DeviceHint)));
        };
        RunCommand = new AsyncRelayCommand(
            ExecuteAsync,
            () => !IsRunning && !string.IsNullOrWhiteSpace(CommandText),
            ex => AppendLine($"[错误] {ex.Message}"));
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        ClearCommand = new RelayCommand(Clear);
    }

    public string CommandText
    {
        get => _commandText;
        set
        {
            if (SetProperty(ref _commandText, value))
            {
                RunCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string OutputText
    {
        get => _outputText;
        private set => SetProperty(ref _outputText, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RunCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string DeviceHint
    {
        get
        {
            var device = _deviceManager.SelectedDevice;
            return device is null
                ? "未选择设备。可输入 adb devices / fastboot devices 自行查看。"
                : $"当前设备：{device.Serial}（{device.Mode.ToChinese()}）";
        }
    }

    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ClearCommand { get; }

    public void PreviousHistory()
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Max(0, _historyIndex - 1);
        CommandText = _history[_historyIndex];
    }

    public void NextHistory()
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
        CommandText = _historyIndex >= _history.Count ? string.Empty : _history[_historyIndex];
    }

    private async Task ExecuteAsync()
    {
        var line = CommandText.Trim();
        if (line.Length == 0)
        {
            return;
        }

        _history.Add(line);
        _historyIndex = _history.Count;
        CommandText = string.Empty;
        IsRunning = true;
        _cts = new CancellationTokenSource();

        try
        {
            await _terminal.ExecuteAsync(
                line,
                output =>
                {
                    RunOnUi(() => AppendLine(output.Text));
                },
                _cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            AppendLine("[已取消]");
        }
        catch (Exception ex)
        {
            AppendLine($"[错误] {ex.Message}");
            _logging.Error($"终端命令失败：{ex.Message}", "Terminal");
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
    }

    private void Clear()
    {
        _output.Clear();
        OutputText = string.Empty;
    }

    private void AppendLine(string text)
    {
        if (_output.Length > MaxChars)
        {
            _output.Remove(0, _output.Length - MaxChars / 2);
            _output.Insert(0, "…（较早输出已截断）" + Environment.NewLine);
        }

        _output.AppendLine(text);
        OutputText = _output.ToString();
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
