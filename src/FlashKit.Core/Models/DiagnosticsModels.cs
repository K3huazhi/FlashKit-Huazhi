namespace FlashKit.Core.Models;

public enum LogKind
{
    Command,
    Info,
    Warning,
    Error
}

/// <summary>
/// 全局原始日志入口。命令日志在进程结束后一次性写入，包含完整 stdout/stderr；
/// 终端和刷写页另有实时输出回调，避免长任务期间日志窗口只能看到旧内容。
/// </summary>
public sealed record LogEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public LogKind Kind { get; init; } = LogKind.Info;
    public string Category { get; init; } = "App";
    public string? CommandLine { get; init; }
    public int? ExitCode { get; init; }
    public string? StandardOutput { get; init; }
    public string? StandardError { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? Message { get; init; }
    public bool TimedOut { get; init; }
    public bool Cancelled { get; init; }

    public string Summary => Kind switch
    {
        LogKind.Command => $"{CommandLine} → {ExitCode?.ToString() ?? "无退出码"}",
        _ => Message ?? string.Empty
    };
}

public sealed class OperationProgress
{
    public double Percentage { get; init; }
    public string Status { get; init; } = string.Empty;
    public long BytesProcessed { get; init; }
    public long? TotalBytes { get; init; }
    public string? CurrentItem { get; init; }

    public static OperationProgress Indeterminate(string status, string? currentItem = null) => new()
    {
        Percentage = 0,
        Status = status,
        CurrentItem = currentItem
    };
}

public sealed class FlashProgress
{
    public int CurrentIndex { get; init; }
    public int TotalCount { get; init; }
    public string? PartitionName { get; init; }
    public string? FileName { get; init; }
    public double Percentage { get; init; }
    public string Status { get; init; } = string.Empty;
}

public sealed record ToolInfoItem(
    string Name,
    string ExecutablePath,
    bool Exists,
    string? Version,
    string? Error)
{
    public string StatusText => !Exists ? "缺失" : Error is null ? "就绪" : "失败";
    public string VersionText => string.IsNullOrWhiteSpace(Version) ? "—" : Version;
    public string ErrorText => Error ?? string.Empty;
}

public sealed class ToolchainReport
{
    public required IReadOnlyList<ToolInfoItem> Tools { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;
    public string ToolsDirectory { get; init; } = string.Empty;
    public bool AllReady => Tools.Count > 0 && Tools.All(t => t.Exists && string.IsNullOrEmpty(t.Error));
    public string Summary => AllReady
        ? "内置 platform-tools 自检通过"
        : "内置 platform-tools 未就绪";
}

public sealed record DriverProblemDevice(
    string Name,
    string? DeviceId,
    uint ErrorCode,
    string? Manufacturer,
    bool LikelyAndroid);

public sealed class DriverHealthReport
{
    public bool CheckSucceeded { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<DriverProblemDevice> ProblemDevices { get; init; } = Array.Empty<DriverProblemDevice>();
    public int TotalProblemCount => ProblemDevices.Count;
    public int LikelyAndroidCount => ProblemDevices.Count(d => d.LikelyAndroid);
    public bool HasLikelyAndroidProblem => LikelyAndroidCount > 0;
    public string Summary { get; init; } = string.Empty;
}
