using System.Collections.ObjectModel;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using Microsoft.UI.Dispatching;

namespace FlashKit.App.ViewModels;

public sealed class RestorePartitionViewModel : ObservableObject
{
    private bool _isSelected;

    public RestorePartitionViewModel(BackupImageEntry entry)
    {
        Name = entry.PartitionName;
        SizeBytes = entry.SizeBytes;
        Risk = entry.Risk;
        FileName = entry.FileName;
    }

    public string Name { get; }
    public long SizeBytes { get; }
    public PartitionRisk Risk { get; }
    public string FileName { get; }
    public string SizeText => PartitionInfo.FormatBytes(SizeBytes);
    public string RiskText => Risk switch
    {
        PartitionRisk.Critical => "高危",
        PartitionRisk.DataLoss => "清数据",
        _ => string.Empty
    };

    public bool IsHighRisk => Risk != PartitionRisk.Normal;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class RestoreViewModel : ObservableObject
{
    private readonly IDeviceManager _deviceManager;
    private readonly IRestoreService _restoreService;
    private readonly ITaskQueueService _taskQueue;
    private readonly IProcessRunner _processRunner;
    private readonly ILoggingService _logging;
    private readonly DispatcherQueue? _dispatcher;
    private BackupManifest? _manifest;
    private string _manifestPath = string.Empty;
    private string _status = "选择备份 manifest.json 后加载";
    private string _resultText = string.Empty;
    private string _warningText = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _isBusy;
    private bool _allowModelMismatch;
    private bool _continueOnError;
    private double _progress;

    public RestoreViewModel(
        IDeviceManager deviceManager,
        IRestoreService restoreService,
        ITaskQueueService taskQueue,
        IProcessRunner processRunner,
        ILoggingService logging)
    {
        _deviceManager = deviceManager;
        _restoreService = restoreService;
        _taskQueue = taskQueue;
        _processRunner = processRunner;
        _logging = logging;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    public ObservableCollection<RestorePartitionViewModel> Partitions { get; } = new();
    public string ManifestPath => _manifestPath;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ResultText { get => _resultText; private set { if (SetProperty(ref _resultText, value)) OnPropertyChanged(nameof(HasResult)); } }
    public bool HasResult => !string.IsNullOrWhiteSpace(ResultText);
    public string WarningText { get => _warningText; private set => SetProperty(ref _warningText, value); }
    public string ErrorMessage { get => _errorMessage; private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(CanStart), nameof(IsNotBusy)); } }
    public bool IsNotBusy => !IsBusy;
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public bool AllowModelMismatch { get => _allowModelMismatch; set => SetProperty(ref _allowModelMismatch, value); }
    public bool ContinueOnError { get => _continueOnError; set => SetProperty(ref _continueOnError, value); }
    public string DeviceText => _manifest is null
        ? "未加载 manifest"
        : $"{_manifest.DeviceModel} / {_manifest.DeviceCodename} · {_manifest.Mode} · Android {_manifest.AndroidRelease}";
    public bool HasHighRiskSelection => Partitions.Any(p => p.IsSelected && p.IsHighRisk);
    public bool CanStart => !IsBusy && _manifest is not null && Partitions.Any(p => p.IsSelected);

    public async Task LoadManifestAsync(string path)
    {
        ErrorMessage = string.Empty;
        ResultText = string.Empty;
        Partitions.Clear();
        _manifest = null;
        _manifestPath = path;
        try
        {
            _manifest = await _restoreService.ReadManifestAsync(path).ConfigureAwait(true);
            foreach (var image in _manifest.Images)
            {
                var partition = new RestorePartitionViewModel(image);
                partition.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(RestorePartitionViewModel.IsSelected))
                    {
                        OnPropertyChanged(nameof(CanStart), nameof(HasHighRiskSelection));
                    }
                };
                Partitions.Add(partition);
            }

            WarningText = _manifest.DeviceModel is null
                ? "manifest 未记录设备型号，无法做来源校验。"
                : string.Empty;
            Status = $"已加载 {Partitions.Count} 个镜像";
            OnPropertyChanged(nameof(ManifestPath), nameof(DeviceText), nameof(CanStart));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Status = "manifest 加载失败";
        }
    }

    public IReadOnlyList<RestorePartitionViewModel> Selected => Partitions.Where(p => p.IsSelected).ToArray();

    public async Task StartAsync()
    {
        if (!CanStart || _manifest is null)
        {
            return;
        }

        var device = _deviceManager.SelectedDevice;
        if (device is null)
        {
            ErrorMessage = "未选择设备。";
            return;
        }

        IsBusy = true;
        Progress = 0;
        ResultText = string.Empty;
        ErrorMessage = string.Empty;
        Status = "准备还原…";
        try
        {
            var selected = Selected.Select(p => p.Name).ToArray();
            var request = new RestoreRequest
            {
                ManifestPath = _manifestPath,
                Serial = device.Serial,
                DeviceMode = device.Mode,
                SelectedPartitions = selected,
                ContinueOnError = ContinueOnError,
                AllowModelMismatch = AllowModelMismatch,
                FastbootUserspace = device.IsFastbootUserspace
            };
            var taskRequest = new OperationTaskRequest
            {
                Type = OperationTaskType.Restore,
                Title = $"还原 {selected.Length} 个分区",
                Serial = device.Serial,
                InputSummary = _manifestPath,
                OutputPath = _manifest.OutputDirectory
            };

            TaskExecutionContext? queueContext = null;
            var progress = new Progress<RestoreProgress>(p =>
            {
                Progress = p.Percentage;
                Status = p.Status;
                queueContext?.Report(p.Percentage, p.Status);
            });
            var result = await _taskQueue.RunAsync(taskRequest, async context =>
            {
                queueContext = context;
                return await _restoreService.RestoreAsync(
                    request,
                    progress,
                    null,
                    context.CancellationToken).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(true);

            Progress = 100;
            Status = result.Summary;
            ResultText = result.Summary + Environment.NewLine +
                         string.Join(Environment.NewLine, result.Results.Select(r =>
                             $"{r.PartitionName}：{(r.Success ? "成功" : "失败")} {r.Message}"));
            if (result.Warnings.Count > 0)
            {
                WarningText = string.Join(Environment.NewLine, result.Warnings);
            }

            if (result.FailureCount > 0)
            {
                ErrorMessage = "存在还原失败项，请查看任务队列与原始日志。";
            }
        }
        catch (OperationCanceledException)
        {
            Status = "已取消";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Status = "还原失败";
            _logging.Error("还原失败：" + ex.Message, "Restore");
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanStart), nameof(HasHighRiskSelection));
        }
    }

    public Task OpenBackupDirectoryAsync()
    {
        var path = _manifest?.OutputDirectory;
        return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
            ? _processRunner.OpenFolderAsync(path)
            : Task.CompletedTask;
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
