using FlashKit.App.Services;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using Microsoft.UI.Dispatching;

namespace FlashKit.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IDeviceManager _deviceManager;
    private readonly IDriverDiagnosticsService _driverDiagnostics;
    private readonly AppSessionState _sessionState;
    private readonly DispatcherQueue? _dispatcher;
    private AndroidDevice? _device;
    private string _driverWarning = string.Empty;

    public MainViewModel(
        IDeviceManager deviceManager,
        IDriverDiagnosticsService driverDiagnostics,
        AppSessionState sessionState)
    {
        _deviceManager = deviceManager;
        _driverDiagnostics = driverDiagnostics;
        _sessionState = sessionState;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _deviceManager.DevicesChanged += OnDevicesChanged;
        _sessionState.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsFlashing));
            OnPropertyChanged(nameof(FlashingDescription));
            OnPropertyChanged(nameof(TitleStatusText));
        };
        _device = _deviceManager.SelectedDevice;
    }

    public bool IsFlashing => _sessionState.IsFlashing;
    public string FlashingDescription => _sessionState.FlashingDescription;

    public string AppName => AppInfo.AppName;

    public AndroidDevice? Device
    {
        get => _device;
        private set
        {
            if (SetProperty(ref _device, value))
            {
                OnPropertyChanged(
                    nameof(DeviceSerialText),
                    nameof(ModeText),
                    nameof(ConnectionText),
                    nameof(ConnectionState),
                    nameof(HasDevice),
                    nameof(TitleStatusText),
                    nameof(DeviceDetailText));
            }
        }
    }

    public string DeviceSerialText
    {
        get
        {
            if (_device is not null)
            {
                return $"设备 {_device.Serial}";
            }

            if (!string.IsNullOrWhiteSpace(_deviceManager.SelectedSerial))
            {
                return $"设备 {_deviceManager.SelectedSerial}（已断开）";
            }

            return _deviceManager.Devices.Count > 1 ? "设备 未选择（多台）" : "设备 未选择";
        }
    }

    public string ModeText => _device is null ? "模式 —" : $"模式 {_device.Mode.ToChinese()}";

    public string ConnectionText => _device?.State switch
    {
        DeviceConnectionState.Connected => _device.Mode == DeviceMode.Fastboot ? "Fastboot" : "已连接",
        DeviceConnectionState.Unauthorized => "未授权",
        DeviceConnectionState.Offline => "离线",
        DeviceConnectionState.Error => "异常",
        _ => "未连接"
    };

    public DeviceConnectionState ConnectionState => _device?.State ?? DeviceConnectionState.NoDevice;

    public bool HasDevice => _device is not null;

    public string DeviceDetailText => _device?.DisplayName ?? "未选择设备";

    public string TitleStatusText => IsFlashing
        ? string.IsNullOrWhiteSpace(FlashingDescription) ? "刷写中" : FlashingDescription
        : string.Empty;

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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var report = await _driverDiagnostics.CheckAsync(cancellationToken).ConfigureAwait(true);
            if (report.HasLikelyAndroidProblem)
            {
                var names = string.Join("、", report.ProblemDevices
                    .Where(d => d.LikelyAndroid)
                    .Select(d => d.Name)
                    .Take(3));
                DriverWarning = $"设备管理器发现疑似安卓设备驱动异常：{names}。请打开驱动安装引导。";
            }
            else
            {
                DriverWarning = string.Empty;
            }
        }
        catch (Exception ex)
        {
            DriverWarning = $"驱动状态检查失败：{ex.Message}";
        }
    }

    private void OnDevicesChanged(object? sender, DeviceSnapshot snapshot)
    {
        void Apply()
        {
            Device = snapshot.SelectedDevice;
            OnPropertyChanged(nameof(DeviceSerialText), nameof(ModeText), nameof(ConnectionText));
        }

        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            Apply();
        }
        else
        {
            _dispatcher.TryEnqueue(Apply);
        }
    }
}
