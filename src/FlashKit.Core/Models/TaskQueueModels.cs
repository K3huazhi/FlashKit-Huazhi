using System.Text.Json.Serialization;

namespace FlashKit.Core.Models;

public enum OperationTaskType
{
    Flash = 0,
    Backup,
    Restore,
    PackageExtract,
    PackagePack,
    ImageConvert,
    PayloadExtract,
    FileTransfer,
    FileSystemUnpack,
    FileSystemPack
}

public enum OperationTaskStatus
{
    Queued = 0,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public sealed class OperationTaskRequest
{
    public required OperationTaskType Type { get; init; }
    public required string Title { get; init; }
    public string Serial { get; init; } = string.Empty;
    public string InputSummary { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed class OperationTaskRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public OperationTaskType Type { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Serial { get; init; } = string.Empty;
    public string InputSummary { get; init; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string LogReference { get; init; } = string.Empty;
    public OperationTaskStatus Status { get; set; } = OperationTaskStatus.Queued;
    public double ProgressPercent { get; set; }
    public string ProgressStatus { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public string Error { get; set; } = string.Empty;
    [JsonIgnore]
    public bool IsCompleted => Status is OperationTaskStatus.Succeeded or OperationTaskStatus.Failed or OperationTaskStatus.Cancelled;

    [JsonIgnore]
    public bool CanCancel => !IsCompleted;

    [JsonIgnore]
    public string DurationText => Duration.TotalSeconds < 1
        ? $"{Duration.TotalMilliseconds:0} ms"
        : $"{Duration:hh\\:mm\\:ss}";

    [JsonIgnore]
    public string TypeText => Type switch
    {
        OperationTaskType.Flash => "刷入",
        OperationTaskType.Backup => "备份",
        OperationTaskType.Restore => "还原",
        OperationTaskType.PackageExtract => "卡刷包解包",
        OperationTaskType.PackagePack => "卡刷包打包",
        OperationTaskType.ImageConvert => "镜像转换",
        OperationTaskType.PayloadExtract => "payload 解包",
        OperationTaskType.FileTransfer => "文件传输",
        OperationTaskType.FileSystemUnpack => "文件系统解包",
        OperationTaskType.FileSystemPack => "文件系统打包",
        _ => Type.ToString()
    };

    [JsonIgnore]
    public string StatusText => Status switch
    {
        OperationTaskStatus.Queued => "排队",
        OperationTaskStatus.Running => "运行中",
        OperationTaskStatus.Succeeded => "成功",
        OperationTaskStatus.Failed => "失败",
        OperationTaskStatus.Cancelled => "已取消",
        _ => Status.ToString()
    };

    [JsonIgnore]
    public string CreatedText => CreatedAt.LocalDateTime.ToString("MM-dd HH:mm:ss");

    [JsonIgnore]
    public string ProgressText => string.IsNullOrWhiteSpace(ProgressStatus)
        ? $"{ProgressPercent:0}%"
        : $"{ProgressPercent:0}% · {ProgressStatus}";

    [JsonIgnore]
    public string OutputText => string.IsNullOrWhiteSpace(OutputPath) ? "—" : OutputPath;
}

public sealed class TaskProgress
{
    public double Percentage { get; init; }
    public string Status { get; init; } = string.Empty;
}

public sealed class TaskExecutionContext
{
    private readonly Action<TaskProgress> _report;
    private readonly Action<string> _setOutput;

    public TaskExecutionContext(OperationTaskRecord record, CancellationToken cancellationToken, Action<TaskProgress> report, Action<string> setOutput)
    {
        Record = record;
        CancellationToken = cancellationToken;
        _report = report;
        _setOutput = setOutput;
    }

    public OperationTaskRecord Record { get; }
    public CancellationToken CancellationToken { get; }
    public void Report(double percentage, string status) =>
        _report(new TaskProgress { Percentage = percentage, Status = status });
    public void SetOutputPath(string path) => _setOutput(path);
}
