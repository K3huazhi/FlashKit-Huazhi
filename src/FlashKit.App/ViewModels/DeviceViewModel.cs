using System.Collections.ObjectModel;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using Microsoft.UI.Dispatching;

namespace FlashKit.App.ViewModels;

public sealed class DeviceViewModel : ObservableObject
{
    private readonly IDeviceManager _deviceManager;
    private readonly IDeviceInfoProvider _deviceInfoProvider;
    private readonly IDriverDiagnosticsService _driverDiagnostics;
    private readonly DispatcherQueue? _dispatcher;
    private readonly DispatcherQueueTimer? _timer;

    private AndroidDevice? _selectedDevice;
    private DeviceInfo? _info;
    private bool _isLoading;
    private bool _isActive;
    private bool _autoRefresh;
    private int _refreshIntervalSeconds = 5;
    private string _errorMessage = string.Empty;
    private string _driverWarning = string.Empty;
    private string _driverDetails = string.Empty;
    private string _lastUpdatedText = "尚未读取";
    private bool _syncingSelection;
    private CancellationTokenSource? _loadCts;

    public DeviceViewModel(
        IDeviceManager deviceManager,
        IDeviceInfoProvider deviceInfoProvider,
        IDriverDiagnosticsService driverDiagnostics)
    {
        _deviceManager = deviceManager;
        _deviceInfoProvider = deviceInfoProvider;
        _driverDiagnostics = driverDiagnostics;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _timer = _dispatcher?.CreateTimer();
        if (_timer is not null)
        {
            _timer.Interval = TimeSpan.FromSeconds(_refreshIntervalSeconds);
            _timer.Tick += async (_, _) =>
            {
                if (_isActive && AutoRefresh && !IsLoading &&
                    SelectedDevice is { Mode: DeviceMode.System, State: DeviceConnectionState.Connected })
                {
                    await LoadAsync().ConfigureAwait(true);
                }
            };
            _timer.Stop();
        }

        RefreshCommand = new AsyncRelayCommand(
            () => LoadAsync(showLoading: true),
            () => !IsLoading && SelectedDevice is not null,
            ex => ErrorMessage = ex.Message);
        RefreshDevicesCommand = new AsyncRelayCommand(
            async () =>
            {
                await _deviceManager.RefreshNowAsync(logToHistory: true).ConfigureAwait(true);
                await LoadAsync(showLoading: true).ConfigureAwait(true);
            },
            () => !IsLoading,
            ex => ErrorMessage = ex.Message);

        foreach (var device in _deviceManager.Devices)
        {
            Devices.Add(device);
        }

        _selectedDevice = _deviceManager.SelectedDevice;
        _deviceManager.DevicesChanged += OnDevicesChanged;
    }

    public ObservableCollection<AndroidDevice> Devices { get; } = new();

    public AndroidDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (ReferenceEquals(_selectedDevice, value))
            {
                return;
            }

            _selectedDevice = value;
            OnPropertyChanged(
                nameof(SelectedDevice),
                nameof(HasSelection),
                nameof(SelectionHint),
                nameof(SelectedDeviceName),
                nameof(ModeText),
                nameof(SerialText),
                nameof(IsInfoAvailable),
                nameof(UnavailableMessage),
                nameof(UnavailableCardText));
            RefreshCommand.NotifyCanExecuteChanged();

            if (!_syncingSelection)
            {
                _deviceManager.SelectDevice(value?.Serial);
                Info = value is null
                    ? null
                    : DeviceInfo.Unavailable(
                        value.Serial,
                        value.Mode,
                        value.Mode == DeviceMode.System
                            ? "正在读取设备信息…"
                            : "当前模式下不可用。设备信息需要在系统模式下通过 ADB 读取。");
                _ = LoadAsync(showLoading: true);
            }
        }
    }

    public DeviceInfo? Info
    {
        get => _info;
        private set
        {
            if (SetProperty(ref _info, value))
            {
                RaiseInfoProperties();
            }
        }
    }

    public bool HasSelection => SelectedDevice is not null;
    public string SelectedDeviceName => SelectedDevice?.DisplayName ?? "未选择设备";
    public string ModeText => SelectedDevice is null ? "—" : SelectedDevice.Mode.ToChinese();
    public string SerialText => SelectedDevice?.Serial ?? "—";

    public string SelectionHint
    {
        get
        {
            if (Devices.Count == 0)
            {
                return "未检测到设备。请连接手机并开启 USB 调试，或进入 Fastboot 模式。";
            }

            if (SelectedDevice is null)
            {
                return $"检测到 {Devices.Count} 台设备。请先在顶部选择要操作的设备，不会默认取第一台。";
            }

            return string.Empty;
        }
    }

    public bool IsInfoAvailable => Info?.IsAvailable == true;

    public string UnavailableCardText
    {
        get
        {
            if (SelectedDevice is null)
            {
                return "请选择设备";
            }

            return SelectedDevice.Mode == DeviceMode.System ? "暂无数据" : "当前模式下不可用";
        }
    }

    public string UnavailableMessage
    {
        get
        {
            if (Info is { IsAvailable: false, UnavailableReason: { } reason })
            {
                return reason;
            }

            if (SelectedDevice is null)
            {
                return "请选择设备后查看信息。";
            }

            return SelectedDevice.Mode is DeviceMode.Fastboot or DeviceMode.Recovery or DeviceMode.Sideload
                ? "当前模式下不可用。设备信息需要在系统模式下通过 ADB 读取。"
                : "暂无设备信息数据。";
        }
    }

    public string ModelText => Info?.Model ?? "未知";
    public string ManufacturerText => Info?.Manufacturer ?? "未知";
    public string AndroidVersionText => Info?.AndroidVersion ?? "未知";
    public string SdkText => Info?.SdkVersion ?? "未知";
    public string CpuAbiText => Info?.CpuAbi ?? "未知";
    public string BuildIdText => Info?.BuildId ?? "未知";
    public string CodenameText => Info?.DeviceCodename ?? "未知";

    public double BatteryPercent => Info?.BatteryLevel ?? 0;
    public string BatteryText => Info?.BatteryLevel is { } level ? $"{level}%" : "未知";
    public string BatterySecondary =>
        Info is null
            ? "—"
            : string.Join(" · ", new[]
            {
                Info.BatteryStatus,
                Info.BatteryTemperatureCelsius is { } temp ? $"{temp} °C" : null,
                Info.BatteryVoltageMillivolts is { } mv ? $"{mv / 1000.0:0.00} V" : null
            }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public double MemoryPercent
    {
        get
        {
            if (Info?.MemoryTotalBytes is > 0 && Info.MemoryAvailableBytes is { } available)
            {
                return Math.Clamp((1.0 - (double)available / Info.MemoryTotalBytes.Value) * 100.0, 0, 100);
            }

            return 0;
        }
    }

    public string MemoryText
    {
        get
        {
            if (Info?.MemoryAvailableBytes is not { } available)
            {
                return "未知";
            }

            return Info.MemoryTotalBytes is { } total
                ? $"{PartitionInfo.FormatBytes(available)} 可用 / {PartitionInfo.FormatBytes(total)}"
                : $"{PartitionInfo.FormatBytes(available)} 可用";
        }
    }

    public double StoragePercent
    {
        get
        {
            if (Info?.StorageTotalBytes is > 0 && Info.StorageAvailableBytes is { } available)
            {
                return Math.Clamp((1.0 - (double)available / Info.StorageTotalBytes.Value) * 100.0, 0, 100);
            }

            return 0;
        }
    }

    public string StorageText
    {
        get
        {
            if (Info?.StorageAvailableBytes is not { } available)
            {
                return "未知";
            }

            return Info.StorageTotalBytes is { } total
                ? $"{PartitionInfo.FormatBytes(available)} 可用 / {PartitionInfo.FormatBytes(total)}"
                : $"{PartitionInfo.FormatBytes(available)} 可用";
        }
    }

    public string StorageMountText => Info?.StorageMountPoint is { } mount ? $"挂载点 {mount}" : "—";

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
                RefreshDevicesCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string DriverWarning
    {
        get => _driverWarning;
        private set
        {
            if (SetProperty(ref _driverWarning, value))
            {
                OnPropertyChanged(nameof(HasDriverWarning));
            }
        }
    }

    public bool HasDriverWarning => !string.IsNullOrWhiteSpace(DriverWarning);
    public string DriverDetails
    {
        get => _driverDetails;
        private set => SetProperty(ref _driverDetails, value);
    }

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set => SetProperty(ref _lastUpdatedText, value);
    }

    public string WarningsText => Info?.Warnings.Count > 0
        ? string.Join(Environment.NewLine, Info.Warnings.Select(w => "• " + w))
        : string.Empty;

    public bool HasWarnings => Info?.Warnings.Count > 0;

    public bool AutoRefresh
    {
        get => _autoRefresh;
        set
        {
            if (!SetProperty(ref _autoRefresh, value))
            {
                return;
            }

            if (value && _isActive)
            {
                _timer?.Start();
            }
            else
            {
                _timer?.Stop();
            }
        }
    }

    public int RefreshIntervalSeconds
    {
        get => _refreshIntervalSeconds;
        set
        {
            if (!SetProperty(ref _refreshIntervalSeconds, value))
            {
                return;
            }

            if (_timer is not null)
            {
                _timer.Interval = TimeSpan.FromSeconds(value);
            }
        }
    }

    public IReadOnlyList<int> RefreshIntervals { get; } = [5, 10];

    public int RefreshIntervalIndex
    {
        get => RefreshIntervalSeconds == 10 ? 1 : 0;
        set
        {
            RefreshIntervalSeconds = value == 1 ? 10 : 5;
            OnPropertyChanged();
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand RefreshDevicesCommand { get; }

    public void Activate()
    {
        _isActive = true;
        if (AutoRefresh)
        {
            _timer?.Start();
        }

        if (Info is null || Info.Serial != SelectedDevice?.Serial || Info.Mode != SelectedDevice?.Mode)
        {
            _ = LoadAsync(showLoading: true);
        }
    }

    public void Deactivate()
    {
        _isActive = false;
        _timer?.Stop();
        _loadCts?.Cancel();
    }

    public async Task LoadAsync(bool showLoading = false)
    {
        var device = SelectedDevice;
        if (device is null)
        {
            Info = null;
            ErrorMessage = string.Empty;
            return;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        if (showLoading)
        {
            IsLoading = true;
        }

        ErrorMessage = string.Empty;
        try
        {
            var info = await _deviceInfoProvider.GetAsync(device, token).ConfigureAwait(true);
            if (token.IsCancellationRequested || SelectedDevice?.Serial != device.Serial)
            {
                return;
            }

            Info = info;
            LastUpdatedText = $"最后更新：{info.CapturedAt.ToLocalTime():HH:mm:ss}";
            await CheckDriverAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = $"读取设备信息失败：{ex.Message}";
            LastUpdatedText = $"读取失败：{DateTimeOffset.Now:HH:mm:ss}";
        }
        finally
        {
            if (showLoading)
            {
                IsLoading = false;
            }
        }
    }

    private async Task CheckDriverAsync(CancellationToken cancellationToken)
    {
        try
        {
            var report = await _driverDiagnostics.CheckAsync(cancellationToken).ConfigureAwait(true);
            if (report.HasLikelyAndroidProblem)
            {
                DriverWarning = $"检测到 {report.LikelyAndroidCount} 个疑似安卓驱动异常设备。";
            }
            else
            {
                DriverWarning = string.Empty;
            }

            DriverDetails = report.CheckSucceeded
                ? string.Join(
                    Environment.NewLine,
                    report.ProblemDevices
                        .Take(8)
                        .Select(d => $"• {d.Name}（错误码 {d.ErrorCode}{(d.LikelyAndroid ? "，疑似安卓" : string.Empty)}）"))
                : report.Error ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            DriverWarning = string.Empty;
        }
    }

    private void OnDevicesChanged(object? sender, DeviceSnapshot snapshot)
    {
        RunOnUi(() =>
        {
            var previous = SelectedDevice;
            _syncingSelection = true;
            try
            {
                Devices.Clear();
                foreach (var device in snapshot.Devices)
                {
                    Devices.Add(device);
                }

                _selectedDevice = snapshot.SelectedDevice;
                OnPropertyChanged(
                    nameof(SelectedDevice),
                    nameof(HasSelection),
                    nameof(SelectionHint),
                    nameof(SelectedDeviceName),
                    nameof(ModeText),
                    nameof(SerialText));
            }
            finally
            {
                _syncingSelection = false;
            }

            var changed = previous is null ||
                          snapshot.SelectedDevice is null ||
                          previous.Serial != snapshot.SelectedDevice.Serial ||
                          previous.Mode != snapshot.SelectedDevice.Mode ||
                          previous.State != snapshot.SelectedDevice.State;

            if (changed)
            {
                if (snapshot.SelectedDevice is null)
                {
                    Info = null;
                    LastUpdatedText = "尚未读取";
                }
                else if (snapshot.SelectedDevice.Mode != DeviceMode.System)
                {
                    Info = DeviceInfo.Unavailable(
                        snapshot.SelectedDevice.Serial,
                        snapshot.SelectedDevice.Mode,
                        "当前模式下不可用。设备信息需要在系统模式下通过 ADB 读取。");
                }

                if (_isActive)
                {
                    _ = LoadAsync(showLoading: true);
                }
            }
        });
    }

    private void RaiseInfoProperties()
    {
        OnPropertyChanged(
            nameof(IsInfoAvailable), nameof(UnavailableMessage), nameof(UnavailableCardText),
            nameof(ModelText), nameof(ManufacturerText), nameof(AndroidVersionText),
            nameof(SdkText), nameof(CpuAbiText), nameof(BuildIdText), nameof(CodenameText),
            nameof(BatteryPercent), nameof(BatteryText), nameof(BatterySecondary),
            nameof(MemoryPercent), nameof(MemoryText), nameof(StoragePercent),
            nameof(StorageText), nameof(StorageMountText), nameof(WarningsText), nameof(HasWarnings));
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

