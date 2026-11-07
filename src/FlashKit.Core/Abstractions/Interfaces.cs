using FlashKit.Core.Models;

namespace FlashKit.Core.Abstractions;

public interface ILoggingService
{
    IReadOnlyList<LogEntry> Snapshot();
    event EventHandler<LogEntry>? EntryAdded;
    event EventHandler? Cleared;

    void Add(LogEntry entry);
    void Info(string message, string category = "App");
    void Warn(string message, string category = "App");
    void Error(string message, string category = "App");
    void Clear();
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        ProcessRequest request,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 将进程 stdout 原样写入文件，用于 adb exec-out 传输二进制镜像。
    /// stdout 不做文本解码，stderr 仍作为文本采集。
    /// </summary>
    Task<ProcessResult> RunToFileAsync(
        ProcessRequest request,
        string outputPath,
        Action<long>? onBytesWritten = null,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default);

    /// <summary>仅用于“打开输出目录/打开驱动下载页”，同样集中走本类，避免 UI 散落 Process.Start。</summary>
    Task OpenFolderAsync(string folderPath, CancellationToken cancellationToken = default);
    Task OpenUrlAsync(string url, CancellationToken cancellationToken = default);
}

public interface IAdbClient
{
    Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken cancellationToken = default, bool logToHistory = false);
    Task<string> GetStateAsync(string serial, bool logToHistory = false, CancellationToken cancellationToken = default);
    Task<string> ShellAsync(string serial, string command, CancellationToken cancellationToken = default, bool logToHistory = true);
    Task<string?> GetPropAsync(string serial, string propertyName, CancellationToken cancellationToken = default);
    Task<ProcessResult> PushAsync(string serial, string localPath, string remotePath, Action<ProcessOutputLine>? onOutput = null, CancellationToken cancellationToken = default);
    Task<ProcessResult> PullAsync(string serial, string remotePath, string localPath, Action<ProcessOutputLine>? onOutput = null, CancellationToken cancellationToken = default);
    Task<ProcessResult> ExecOutToFileAsync(
        string serial,
        string command,
        string localPath,
        Action<long>? onBytesWritten = null,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default);
    Task<ProcessResult> RebootAsync(string serial, string? target = null, CancellationToken cancellationToken = default);
    Task<ProcessResult> ExecuteRawAsync(
        string? serial,
        IReadOnlyList<string> arguments,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default,
        bool logToHistory = true,
        TimeSpan? timeout = null);
    Task<ProcessResult> StartServerAsync(CancellationToken cancellationToken = default);
}

public interface IFastbootClient
{
    Task<IReadOnlyList<FastbootDevice>> ListDevicesAsync(CancellationToken cancellationToken = default, bool logToHistory = false);
    Task<ProcessResult> ExecuteRawAsync(
        string? serial,
        IReadOnlyList<string> arguments,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default,
        bool logToHistory = true,
        TimeSpan? timeout = null);
    Task<string?> GetVarAsync(string serial, string variableName, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, string>> GetVarAllAsync(string serial, CancellationToken cancellationToken = default);
    Task<bool?> IsUserspaceAsync(string serial, CancellationToken cancellationToken = default);
    Task<ProcessResult> FlashAsync(
        string serial,
        string partitionName,
        string imagePath,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default);
    Task<ProcessResult> RebootAsync(string serial, string? target = null, CancellationToken cancellationToken = default);
    Task<ProcessResult> FlashLogicalPartitionAsync(string serial, string partition, string imagePath, Action<ProcessOutputLine>? onOutput = null, CancellationToken cancellationToken = default);
    Task<ProcessResult> FetchAsync(
        string serial,
        string partitionName,
        string outputPath,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default);
}

public interface IBackupService
{
    Task<BackupResult> BackupAsync(
        BackupRunOptions options,
        IProgress<BackupProgress>? progress = null,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default);
}

public interface IBackupEnvironmentService
{
    Task<BackupEnvironmentInfo> DetectAsync(
        AndroidDevice device,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
    void Invalidate(string serial);
}

public interface ITaskQueueService
{
    IReadOnlyList<OperationTaskRecord> Snapshot();
    event EventHandler? Changed;

    Task<T> RunAsync<T>(
        OperationTaskRequest request,
        Func<TaskExecutionContext, Task<T>> operation,
        CancellationToken cancellationToken = default);

    void Cancel(Guid id);
    void ClearCompleted();
}

public interface IDeviceManager
{
    IReadOnlyList<AndroidDevice> Devices { get; }
    string? SelectedSerial { get; }
    AndroidDevice? SelectedDevice { get; }
    DeviceSnapshot Snapshot { get; }
    event EventHandler<DeviceSnapshot>? DevicesChanged;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
    Task RefreshNowAsync(bool logToHistory = false, CancellationToken cancellationToken = default);
    void SelectDevice(string? serial);
    Task RebootSelectedToFastbootAsync(CancellationToken cancellationToken = default);
    Task RebootSelectedToFastbootdAsync(CancellationToken cancellationToken = default);
    Task RebootSelectedToSystemAsync(CancellationToken cancellationToken = default);
    Task<AndroidDevice?> WaitForModeAsync(DeviceMode mode, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class DeviceSnapshot
{
    public required IReadOnlyList<AndroidDevice> Devices { get; init; }
    public AndroidDevice? SelectedDevice { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    public string? Error { get; init; }
}

public interface IDeviceInfoProvider
{
    Task<DeviceInfo> GetAsync(AndroidDevice device, CancellationToken cancellationToken = default);
}

public interface IPartitionCatalogService
{
    Task<PartitionCatalog> GetAsync(string serial, DeviceMode mode, bool forceRefresh = false, CancellationToken cancellationToken = default);
    void Invalidate(string serial);
}

public interface IFlashValidationService
{
    Task<FlashValidationResult> ValidateAsync(
        FlashItem item,
        PartitionCatalog catalog,
        FlashRunOptions options,
        bool fastbootUserspace,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FlashValidationResult>> ValidateAllAsync(
        IReadOnlyList<FlashItem> items,
        PartitionCatalog catalog,
        FlashRunOptions options,
        bool fastbootUserspace,
        CancellationToken cancellationToken = default);
}

public interface IFlashService
{
    Task<FlashRunSummary> ExecuteAsync(
        FlashRunOptions options,
        IReadOnlyList<FlashItem> items,
        IProgress<FlashProgress>? progress = null,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default);
}

public interface IPackageExtractor
{
    Task<ExtractResult> ExtractAsync(
        string packagePath,
        IProgress<OperationProgress>? progress = null,
        Action<string>? logLine = null,
        CancellationToken cancellationToken = default);
}

public interface IPackagePacker
{
    PackValidationResult Validate(IReadOnlyList<PackImageInput> images);
    Task<PackResult> PackAsync(
        IReadOnlyList<PackImageInput> images,
        string outputZipPath,
        IProgress<OperationProgress>? progress = null,
        Action<string>? logLine = null,
        CancellationToken cancellationToken = default);
}

public interface IToolchainService
{
    string ToolsDirectory { get; }
    string AdbPath { get; }
    string FastbootPath { get; }
    ToolchainReport? LastReport { get; }
    Task<ToolchainReport> CheckAsync(CancellationToken cancellationToken = default);
}

public interface IDriverDiagnosticsService
{
    Task<DriverHealthReport> CheckAsync(CancellationToken cancellationToken = default);
}

public interface ITerminalCommandService
{
    Task ExecuteAsync(
        string commandLine,
        Action<ProcessOutputLine> onOutput,
        CancellationToken cancellationToken = default);
}
