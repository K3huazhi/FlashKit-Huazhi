using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;

namespace FlashKit.Services.Devices;

public sealed class DeviceManager : IDeviceManager, IAsyncDisposable
{
    private readonly IAdbClient _adb;
    private readonly IFastbootClient _fastboot;
    private readonly IToolchainService _toolchain;
    private readonly ILoggingService _logging;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateGate = new();

    private AndroidDevice[] _devices = Array.Empty<AndroidDevice>();
    private string? _selectedSerial;
    private DeviceSnapshot _snapshot = new() { Devices = Array.Empty<AndroidDevice>() };
    private string? _lastSignature;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private string? _lastToolError;

    public DeviceManager(
        IAdbClient adb,
        IFastbootClient fastboot,
        IToolchainService toolchain,
        ILoggingService logging)
    {
        _adb = adb;
        _fastboot = fastboot;
        _toolchain = toolchain;
        _logging = logging;
    }

    public IReadOnlyList<AndroidDevice> Devices => _devices;
    public string? SelectedSerial => _selectedSerial;
    public AndroidDevice? SelectedDevice => _devices.FirstOrDefault(d => d.Serial == _selectedSerial);
    public DeviceSnapshot Snapshot => _snapshot;

    public event EventHandler<DeviceSnapshot>? DevicesChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loopTask is not null && !_loopTask.IsCompleted)
        {
            return Task.CompletedTask;
        }

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = PollLoopAsync(_loopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_loopCts is null)
        {
            return;
        }

        try
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
            if (_loopTask is not null)
            {
                await _loopTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        finally
        {
            _loopCts.Dispose();
            _loopCts = null;
            _loopTask = null;
        }
    }

    public async Task RefreshNowAsync(bool logToHistory = false, CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshCoreAsync(logToHistory, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logging.Error($"设备枚举失败：{ex.Message}", "DeviceManager");
            PublishSnapshot(new DeviceSnapshot
            {
                Devices = _devices,
                SelectedDevice = SelectedDevice,
                Error = ex.Message
            }, force: true);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void SelectDevice(string? serial)
    {
        lock (_stateGate)
        {
            _selectedSerial = string.IsNullOrWhiteSpace(serial) ? null : serial;
            _snapshot = new DeviceSnapshot
            {
                Devices = _devices,
                SelectedDevice = _devices.FirstOrDefault(d => d.Serial == _selectedSerial)
            };
        }

        DevicesChanged?.Invoke(this, _snapshot);
    }

    public async Task RebootSelectedToFastbootAsync(CancellationToken cancellationToken = default)
    {
        var device = RequireSelected();
        ProcessResult result;
        if (device.Mode == DeviceMode.Fastboot)
        {
            // 已经在 fastboot/bootloader，无需重复重启。
            return;
        }

        if (device.Mode is DeviceMode.System or DeviceMode.Recovery or DeviceMode.Sideload)
        {
            result = await _adb.RebootAsync(device.Serial, "bootloader", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException("当前设备状态不支持切换到 Fastboot，请检查 USB 连接或驱动。");
        }

        EnsureSuccess(result, "切换到 Fastboot 失败");
        _logging.Info($"已请求 {device.Serial} 重启到 bootloader", "DeviceManager");
        await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
        await RefreshNowAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task RebootSelectedToFastbootdAsync(CancellationToken cancellationToken = default)
    {
        var device = RequireSelected();
        ProcessResult result;
        if (device.Mode == DeviceMode.Fastboot)
        {
            result = await _fastboot.RebootAsync(device.Serial, "fastboot", cancellationToken).ConfigureAwait(false);
        }
        else if (device.Mode is DeviceMode.System or DeviceMode.Recovery or DeviceMode.Sideload)
        {
            result = await _adb.RebootAsync(device.Serial, "fastboot", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException("当前设备状态不支持切换到 Fastbootd。");
        }

        EnsureSuccess(result, "切换到 Fastbootd 失败");
        _logging.Info($"已请求 {device.Serial} 重启到 Fastbootd", "DeviceManager");
        await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
        await RefreshNowAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task RebootSelectedToSystemAsync(CancellationToken cancellationToken = default)
    {
        var device = RequireSelected();
        var result = device.Mode == DeviceMode.Fastboot
            ? await _fastboot.RebootAsync(device.Serial, null, cancellationToken).ConfigureAwait(false)
            : await _adb.RebootAsync(device.Serial, null, cancellationToken).ConfigureAwait(false);

        EnsureSuccess(result, "重启到系统失败");
        _logging.Info($"已请求 {device.Serial} 重启到系统", "DeviceManager");
        await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
        await RefreshNowAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<AndroidDevice?> WaitForModeAsync(
        DeviceMode mode,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RefreshNowAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var device = SelectedDevice;
            if (device is not null && device.Mode == mode && device.State == DeviceConnectionState.Connected)
            {
                return device;
            }

            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshNowAsync(logToHistory: false, cancellationToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RefreshNowAsync(logToHistory: false, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 应用关闭。
        }
        catch (Exception ex)
        {
            _logging.Error($"设备监听循环异常：{ex.Message}", "DeviceManager");
        }
    }

    private async Task RefreshCoreAsync(bool logToHistory, CancellationToken cancellationToken)
    {
        if (!File.Exists(_toolchain.AdbPath) && !File.Exists(_toolchain.FastbootPath))
        {
            var message = $"内置 platform-tools 缺失：{_toolchain.ToolsDirectory}";
            if (!string.Equals(_lastToolError, message, StringComparison.Ordinal))
            {
                _lastToolError = message;
                _logging.Error(message, "DeviceManager");
            }

            PublishSnapshot(new DeviceSnapshot
            {
                Devices = Array.Empty<AndroidDevice>(),
                SelectedDevice = null,
                Error = message
            }, force: true);
            return;
        }

        var adbDevices = File.Exists(_toolchain.AdbPath)
            ? await _adb.ListDevicesAsync(cancellationToken, logToHistory).ConfigureAwait(false)
            : Array.Empty<AdbDevice>();
        var fastbootDevices = File.Exists(_toolchain.FastbootPath)
            ? await _fastboot.ListDevicesAsync(cancellationToken, logToHistory).ConfigureAwait(false)
            : Array.Empty<FastbootDevice>();

        var merged = new Dictionary<string, AndroidDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var adbDevice in adbDevices)
        {
            var state = adbDevice.State.ToLowerInvariant() switch
            {
                "device" => DeviceConnectionState.Connected,
                "recovery" => DeviceConnectionState.Connected,
                "sideload" => DeviceConnectionState.Connected,
                "unauthorized" => DeviceConnectionState.Unauthorized,
                "offline" => DeviceConnectionState.Offline,
                _ => DeviceConnectionState.Error
            };

            var mode = adbDevice.State.ToLowerInvariant() switch
            {
                "device" => DeviceMode.System,
                "recovery" => DeviceMode.Recovery,
                "sideload" => DeviceMode.Sideload,
                _ => DeviceMode.Unknown
            };

            merged[adbDevice.Serial] = new AndroidDevice
            {
                Serial = adbDevice.Serial,
                Product = adbDevice.Product,
                Model = adbDevice.Model,
                Mode = state == DeviceConnectionState.Connected ? mode : DeviceMode.Unknown,
                State = state,
                RawState = adbDevice.State,
                TransportId = adbDevice.TransportId,
                LastSeen = DateTimeOffset.Now
            };
        }

        foreach (var fastbootDevice in fastbootDevices)
        {
            merged.TryGetValue(fastbootDevice.Serial, out var existing);
            merged[fastbootDevice.Serial] = new AndroidDevice
            {
                Serial = fastbootDevice.Serial,
                Product = existing?.Product,
                Model = existing?.Model,
                Mode = DeviceMode.Fastboot,
                State = DeviceConnectionState.Connected,
                RawState = "fastboot",
                LastSeen = DateTimeOffset.Now
            };
        }

        var devices = merged.Values
            .OrderByDescending(d => d.Mode == DeviceMode.Fastboot)
            .ThenBy(d => d.Serial, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (_stateGate)
        {
            _devices = devices;

            // 多设备时不自动选择第一台，避免误刷。单设备时自动选中以方便普通用户。
            if (_selectedSerial is null && devices.Length == 1)
            {
                _selectedSerial = devices[0].Serial;
            }
        }

        var selected = devices.FirstOrDefault(d => d.Serial == _selectedSerial);
        var snapshot = new DeviceSnapshot
        {
            Devices = devices,
            SelectedDevice = selected,
            Error = null
        };
        _lastToolError = null;
        PublishSnapshot(snapshot, force: false);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void PublishSnapshot(DeviceSnapshot snapshot, bool force)
    {
        var signature = string.Join('|', snapshot.Devices.Select(d =>
            $"{d.Serial}:{d.Mode}:{d.State}:{d.Model}:{d.Product}")) +
            $"#{snapshot.SelectedDevice?.Serial}#{snapshot.Error}";

        _snapshot = snapshot;
        if (!force && signature == _lastSignature)
        {
            return;
        }

        _lastSignature = signature;
        DevicesChanged?.Invoke(this, snapshot);
    }

    private AndroidDevice RequireSelected() =>
        SelectedDevice ?? throw new InvalidOperationException("请先选择一台设备。");

    private static void EnsureSuccess(ProcessResult result, string prefix)
    {
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"{prefix}：{result.ToReadableError()}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _refreshGate.Dispose();
    }
}
