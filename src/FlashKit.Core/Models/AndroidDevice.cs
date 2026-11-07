namespace FlashKit.Core.Models;

/// <summary>
/// 同一序列号可能同时出现在 adb 和 fastboot 的枚举结果里（通常是切换模式的瞬间）。
/// 该类保留原始状态，避免 UI 把“未授权”误判为已连接。
/// </summary>
public sealed record AndroidDevice
{
    public required string Serial { get; init; }
    public string? Product { get; init; }
    public string? Model { get; init; }
    public DeviceMode Mode { get; init; } = DeviceMode.Unknown;
    public DeviceConnectionState State { get; init; } = DeviceConnectionState.NoDevice;
    public bool IsFastbootUserspace { get; init; }
    public string? TransportId { get; init; }
    public string RawState { get; init; } = string.Empty;
    public DateTimeOffset LastSeen { get; init; } = DateTimeOffset.Now;

    public bool IsOnline => State == DeviceConnectionState.Connected;

    public string ModeText => Mode.ToChinese();

    public string DisplayName
    {
        get
        {
            var name = !string.IsNullOrWhiteSpace(Model) ? Model : Product;
            return string.IsNullOrWhiteSpace(name) ? Serial : $"{name} ({Serial})";
        }
    }
}

public sealed record AdbDevice
{
    public required string Serial { get; init; }
    public string State { get; init; } = "unknown";
    public string? Product { get; init; }
    public string? Model { get; init; }
    public string? Device { get; init; }
    public string? TransportId { get; init; }
}

public sealed record FastbootDevice
{
    public required string Serial { get; init; }
    public string RawLine { get; init; } = string.Empty;
}
