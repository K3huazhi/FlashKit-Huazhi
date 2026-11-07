using System.Collections.ObjectModel;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;

namespace FlashKit.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly IToolchainService _toolchain;
    private readonly IDriverDiagnosticsService _driverDiagnostics;
    private readonly IProcessRunner _processRunner;
    private readonly ILoggingService _logging;
    private string _toolchainSummary = "尚未自检";
    private string _checkedAtText = "—";
    private string _driverSummary = "尚未检查";
    private string _driverDetails = string.Empty;
    private bool _isChecking;
    private bool _toolsReady;

    public SettingsViewModel(
        IToolchainService toolchain,
        IDriverDiagnosticsService driverDiagnostics,
        IProcessRunner processRunner,
        ILoggingService logging)
    {
        _toolchain = toolchain;
        _driverDiagnostics = driverDiagnostics;
        _processRunner = processRunner;
        _logging = logging;

        RecheckCommand = new AsyncRelayCommand(
            () => CheckAsync(force: true),
            () => !IsChecking,
            ex => ToolchainSummary = $"自检失败：{ex.Message}");
        OpenToolsFolderCommand = new AsyncRelayCommand(
            OpenToolsFolderAsync,
            () => Directory.Exists(_toolchain.ToolsDirectory),
            ex => ToolchainSummary = $"无法打开目录：{ex.Message}");
        OpenLogFolderCommand = new AsyncRelayCommand(OpenLogFolderAsync, onError: ex => DriverSummary = ex.Message);
        OpenGoogleDriverCommand = new AsyncRelayCommand(
            () => _processRunner.OpenUrlAsync("https://developer.android.com/studio/run/win-usb"),
            onError: ex => DriverSummary = ex.Message);
    }

    public string AppVersion => $"FlashKit {AppInfo.Version}";
    public string RuntimeText => $".NET {Environment.Version} · Windows {Environment.OSVersion.Version}";
    public string ToolsDirectory => _toolchain.ToolsDirectory;
    public string AdbPath => _toolchain.AdbPath;
    public string FastbootPath => _toolchain.FastbootPath;

    public ObservableCollection<ToolInfoItem> Tools { get; } = new();

    public string ToolchainSummary
    {
        get => _toolchainSummary;
        private set => SetProperty(ref _toolchainSummary, value);
    }

    public string CheckedAtText
    {
        get => _checkedAtText;
        private set => SetProperty(ref _checkedAtText, value);
    }

    public string DriverSummary
    {
        get => _driverSummary;
        private set => SetProperty(ref _driverSummary, value);
    }

    public string DriverDetails
    {
        get => _driverDetails;
        private set => SetProperty(ref _driverDetails, value);
    }

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (SetProperty(ref _isChecking, value))
            {
                RecheckCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ToolsReady
    {
        get => _toolsReady;
        private set => SetProperty(ref _toolsReady, value);
    }

    public AsyncRelayCommand RecheckCommand { get; }
    public AsyncRelayCommand OpenToolsFolderCommand { get; }
    public AsyncRelayCommand OpenLogFolderCommand { get; }
    public AsyncRelayCommand OpenGoogleDriverCommand { get; }

    public async Task InitializeAsync()
    {
        var report = _toolchain.LastReport;
        if (report is not null)
        {
            ApplyToolReport(report);
        }
        else
        {
            await CheckAsync(force: false).ConfigureAwait(true);
        }

        await CheckDriverAsync().ConfigureAwait(true);
    }

    private async Task CheckAsync(bool force)
    {
        IsChecking = true;
        try
        {
            var report = force || _toolchain.LastReport is null
                ? await _toolchain.CheckAsync().ConfigureAwait(true)
                : _toolchain.LastReport;
            if (report is not null)
            {
                ApplyToolReport(report);
            }

            await CheckDriverAsync().ConfigureAwait(true);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void ApplyToolReport(ToolchainReport report)
    {
        Tools.Clear();
        foreach (var tool in report.Tools)
        {
            Tools.Add(tool);
        }

        ToolsReady = report.AllReady;
        ToolchainSummary = report.Summary;
        CheckedAtText = $"最后自检：{report.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        OpenToolsFolderCommand.NotifyCanExecuteChanged();
    }

    private async Task CheckDriverAsync()
    {
        try
        {
            var report = await _driverDiagnostics.CheckAsync().ConfigureAwait(true);
            DriverSummary = report.Summary;
            DriverDetails = report.ProblemDevices.Count == 0
                ? "未发现带错误码的设备。"
                : string.Join(
                    Environment.NewLine,
                    report.ProblemDevices.Select(d =>
                        $"• {d.Name} · 错误码 {d.ErrorCode} · {(d.LikelyAndroid ? "疑似安卓" : "非安卓/未知")}"));
        }
        catch (Exception ex)
        {
            DriverSummary = $"驱动检查失败：{ex.Message}";
        }
    }

    private async Task OpenToolsFolderAsync()
    {
        if (!Directory.Exists(_toolchain.ToolsDirectory))
        {
            throw new DirectoryNotFoundException("内置 tools 目录不存在。");
        }

        await _processRunner.OpenFolderAsync(_toolchain.ToolsDirectory);
    }

    private async Task OpenLogFolderAsync()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlashKit",
            "Logs");
        Directory.CreateDirectory(folder);
        _logging.Info($"打开日志目录：{folder}", "Settings");
        await _processRunner.OpenFolderAsync(folder);
    }
}
