namespace FlashKit.Core.Models;

/// <summary>设备当前所处的工作模式。</summary>
public enum DeviceMode
{
    Unknown = 0,
    System,
    Recovery,
    Sideload,
    Fastboot
}

/// <summary>设备连接状态。相比“有没有序列号”，这个状态更适合直接展示给用户。</summary>
public enum DeviceConnectionState
{
    NoDevice = 0,
    Connected,
    Unauthorized,
    Offline,
    Error
}

public enum PartitionRisk
{
    Normal = 0,
    DataLoss,
    Critical
}

public enum PartitionSource
{
    AdbBlockByyName,
    FastbootGetVar,
    Merged
}

public enum FlashItemState
{
    Pending,
    Validating,
    Ready,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Cancelled
}

public enum OperationKind
{
    Info,
    Warning,
    Error
}
