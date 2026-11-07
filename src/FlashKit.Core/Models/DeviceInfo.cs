namespace FlashKit.Core.Models;

/// <summary>
/// 设备信息快照。所有字段都可能为 null：厂商 ROM 的 dumpsys/df/getprop 输出并不统一，
/// 解析器只负责把“能确认的值”填进来，展示层统一把 null 映射成“未知”。
/// </summary>
public sealed class DeviceInfo
{
    public required string Serial { get; init; }
    public DeviceMode Mode { get; init; }
    public bool IsAvailable { get; init; }
    public string? UnavailableReason { get; init; }

    public string? Manufacturer { get; init; }
    public string? Model { get; init; }
    public string? DeviceCodename { get; init; }
    public string? AndroidVersion { get; init; }
    public string? SdkVersion { get; init; }
    public string? BuildId { get; init; }
    public string? CpuAbi { get; init; }
    public string? CpuAbiList { get; init; }

    public int? BatteryLevel { get; init; }
    public string? BatteryStatus { get; init; }
    public int? BatteryTemperatureCelsius { get; init; }
    public int? BatteryVoltageMillivolts { get; init; }

    public long? MemoryAvailableBytes { get; init; }
    public long? MemoryTotalBytes { get; init; }

    public long? StorageAvailableBytes { get; init; }
    public long? StorageTotalBytes { get; init; }
    public string? StorageMountPoint { get; init; }

    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static DeviceInfo Unavailable(string serial, DeviceMode mode, string reason) => new()
    {
        Serial = serial,
        Mode = mode,
        IsAvailable = false,
        UnavailableReason = reason,
        CapturedAt = DateTimeOffset.Now
    };
}

public static class DeviceModeText
{
    public static string ToChinese(this DeviceMode mode) => mode switch
    {
        DeviceMode.System => "系统",
        DeviceMode.Recovery => "Recovery",
        DeviceMode.Sideload => "Sideload",
        DeviceMode.Fastboot => "Fastboot",
        _ => "未知"
    };
}
