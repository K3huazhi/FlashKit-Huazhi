using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using FlashKit.Core.Parsing;

namespace FlashKit.Services.Devices;

public sealed class DeviceInfoProvider : IDeviceInfoProvider
{
    private readonly IAdbClient _adb;

    public DeviceInfoProvider(IAdbClient adb)
    {
        _adb = adb;
    }

    public async Task<DeviceInfo> GetAsync(AndroidDevice device, CancellationToken cancellationToken = default)
    {
        if (device.Mode != DeviceMode.System || device.State != DeviceConnectionState.Connected)
        {
            var reason = device.State switch
            {
                DeviceConnectionState.Unauthorized => "设备未授权 USB 调试，请在手机上允许调试并重试。",
                DeviceConnectionState.Offline => "设备处于离线状态，请重新插拔 USB。",
                _ => "当前模式下不可用。设备信息需要在系统模式下通过 ADB 读取。"
            };
            return DeviceInfo.Unavailable(device.Serial, device.Mode, reason);
        }

        var batteryTask = ReadCommandAsync(device.Serial, "dumpsys battery", cancellationToken);
        var memoryTask = ReadCommandAsync(device.Serial, "cat /proc/meminfo", cancellationToken);
        var dfTask = ReadCommandAsync(device.Serial, "df -k", cancellationToken);
        var propTask = ReadCommandAsync(device.Serial, "getprop", cancellationToken);

        await Task.WhenAll(batteryTask, memoryTask, dfTask, propTask).ConfigureAwait(false);

        var warnings = new List<string>();
        var battery = DeviceOutputParser.ParseBattery(batteryTask.Result);
        var memory = DeviceOutputParser.ParseMemInfo(memoryTask.Result);
        var storage = DeviceOutputParser.ParseDf(dfTask.Result);
        var props = DeviceOutputParser.ParseGetProp(propTask.Result);

        warnings.AddRange(battery.Warnings);
        warnings.AddRange(memory.Warnings);
        warnings.AddRange(storage.Warnings);

        if (props.Count == 0)
        {
            warnings.Add("未读取到设备属性（getprop），型号与系统版本可能显示未知");
        }

        var info = new DeviceInfo
        {
            Serial = device.Serial,
            Mode = DeviceMode.System,
            IsAvailable = true,
            Manufacturer = FirstProp(props, "ro.product.manufacturer", "ro.product.vendor.manufacturer", "ro.product.system.manufacturer"),
            Model = FirstProp(props, "ro.product.model", "ro.product.vendor.model", "ro.product.marketname", "ro.product.odm.model"),
            DeviceCodename = FirstProp(props, "ro.product.device", "ro.product.vendor.device", "ro.product.bootimage.device"),
            AndroidVersion = FirstProp(props, "ro.build.version.release", "ro.system.build.version.release", "ro.build.version.release_or_codename"),
            SdkVersion = FirstProp(props, "ro.build.version.sdk"),
            BuildId = FirstProp(props, "ro.build.display.id", "ro.build.id"),
            CpuAbi = FirstProp(props, "ro.product.cpu.abi", "ro.product.cpu.abilist"),
            CpuAbiList = FirstProp(props, "ro.product.cpu.abilist", "ro.product.cpu.abi"),
            BatteryLevel = battery.Level,
            BatteryStatus = battery.Status,
            BatteryTemperatureCelsius = battery.TemperatureCelsius,
            BatteryVoltageMillivolts = battery.VoltageMillivolts,
            MemoryAvailableBytes = memory.AvailableBytes,
            MemoryTotalBytes = memory.TotalBytes,
            StorageAvailableBytes = storage.AvailableBytes,
            StorageTotalBytes = storage.TotalBytes,
            StorageMountPoint = storage.MountPoint,
            CapturedAt = DateTimeOffset.Now,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray()
        };

        return info;
    }

    private async Task<string> ReadCommandAsync(string serial, string command, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _adb.ExecuteRawAsync(
                serial,
                ["shell", command],
                cancellationToken: cancellationToken,
                logToHistory: false,
                timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            return result.CombinedOutput;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string? FirstProp(IReadOnlyDictionary<string, string> props, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (props.TryGetValue(key, out var value) && IsMeaningful(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static bool IsMeaningful(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("null", StringComparison.OrdinalIgnoreCase);
}
