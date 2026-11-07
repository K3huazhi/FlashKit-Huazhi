using System.Collections.ObjectModel;
using System.ComponentModel;
using FlashKit.App.Services;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using Microsoft.UI.Dispatching;

namespace FlashKit.App.ViewModels;

public sealed class BackupViewModel : ObservableObject
{
    private readonly IDeviceManager _deviceManager;
    private readonly IAdbClient _adb;
    private readonly IFastbootClient _fastboot;
    private readonly IBackupEnvironmentService _environmentService;
    private readonly IBackupService _backupService;
    private readonly ITaskQueueService _taskQueue;
    private readonly IProcessRunner _processRunner;
    private readonly ILoggingService _logging;
    private readonly DispatcherQueue? _dispatcher;
    private readonly List<BackupPartitionViewModel> _all = new();
    private PartitionCatalog? _catalog;
    private BackupEnvironmentInfo? _environment;
    private AndroidDevice? _device;
    private CancellationTokenSource? _backupCts;
    private string _lastDeviceKey = string.Empty;
    private string _destinationRoot;
    private string _catalogStatus = "尚未读取分区提示";
    private string _errorMessage = string.Empty;
    private string _warningMessage = string.Empty;
    private string _progressStatus = string.Empty;
    private string _backupResultText = string.Empty;
    private string _lastOutputDirectory = string.Empty;
    private string _spaceStatus = string.Empty;
    private bool _spaceError;
    private bool _isLoading;
    private bool _isBusy;
    private bool _isRootAvailable;
    private bool _updatingCoverage;
    private double _progressPercent;

    public BackupViewModel(
        IDeviceManager deviceManager,
        IAdbClient adb,
        IFastbootClient fastboot,
        IBackupEnvironmentService environmentService,
        IBackupService backupService,
        ITaskQueueService taskQueue,
        IProcessRunner processRunner,
        ILoggingService logging)
    {
        _deviceManager = deviceManager;
        _adb = adb;
        _fastboot = fastboot;
        _environmentService = environmentService;
        _backupService = backupService;
        _taskQueue = taskQueue;
        _processRunner = processRunner;
        _logging = logging;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _destinationRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "FlashKit",
            "Backups");

        _deviceManager.DevicesChanged += OnDevicesChanged;

        RefreshPartitionsCommand = new AsyncRelayCommand(
            () => RefreshPartitionsAsync(force: true),
            () => CanReadBackupEnvironment,
            ex => SetError(ex));
        RebootToRecoveryCommand = new AsyncRelayCommand(
            RebootToRecoveryAsync,
            () => !IsBusy && HasDevice,
            ex => SetError(ex));
        RebootToSystemCommand = new AsyncRelayCommand(
            RebootToSystemAsync,
            () => !IsBusy && HasDevice,
            ex => SetError(ex));
        SelectAllCommand = new RelayCommand(SelectAll, () => HasSelectablePartitions && !IsBusy);
        InvertSelectionCommand = new RelayCommand(InvertSelection, () => HasSelectablePartitions && !IsBusy);
        SelectRegularCommand = new RelayCommand(SelectRegular, () => RegularPartitions.Count > 0 && !IsBusy);
        CancelBackupCommand = new RelayCommand(CancelBackup, () => IsBusy);
        OpenOutputCommand = new AsyncRelayCommand(
            OpenOutputAsync,
            () => CanOpenOutput,
            ex => SetError(ex));

        _device = _deviceManager.SelectedDevice;
        UpdateDeviceState();
    }

    public ObservableCollection<BackupPartitionViewModel> RegularPartitions { get; } = new();
    public ObservableCollection<BackupPartitionViewModel> LogicalPartitions { get; } = new();
    public ObservableCollection<BackupPartitionViewModel> HighRiskPartitions { get; } = new();

    public AndroidDevice? Device => _device;
    public bool HasDevice => _device is not null;
    public bool IsRecoveryMode => _device?.Mode == DeviceMode.Recovery &&
                                 _device.State == DeviceConnectionState.Connected;

    public bool IsEnvironmentSupported => _environment?.IsSupported == true && IsRootAvailable;

    public bool CanReadBackupEnvironment => !IsBusy && _device is
    {
        State: DeviceConnectionState.Connected,
        Mode: DeviceMode.System or DeviceMode.Recovery
    };
    public string EnvironmentText => _environment?.Message ?? "尚未探测备份环境";
    public string TransportText => _environment?.ModeText ?? "—";
    public bool IsRootAvailable
    {
        get => _isRootAvailable;
        private set
        {
            if (SetProperty(ref _isRootAvailable, value))
            {
                OnPropertyChanged(nameof(RootStatusText), nameof(CanStartBackup));
            }
        }
    }

    public string RootStatusText => IsRootAvailable ? "Root 可用" : "Root 未确认";
    public string DeviceSummary => _device is null
        ? "请先在“设备信息”页选择设备"
        : $"{_device.DisplayName} · {_device.Mode.ToChinese()}";
    public string ModeText => _device?.Mode.ToChinese() ?? "—";
    public string CatalogStatus
    {
        get => _catalogStatus;
        private set => SetProperty(ref _catalogStatus, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string WarningMessage
    {
        get => _warningMessage;
        private set
        {
            if (SetProperty(ref _warningMessage, value))
            {
                OnPropertyChanged(nameof(HasWarning));
            }
        }
    }

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningMessage);

    public string DestinationRoot
    {
        get => _destinationRoot;
        set
        {
            if (SetProperty(ref _destinationRoot, value))
            {
                OnPropertyChanged(nameof(DestinationHint));
                RecomputeSelection();
            }
        }
    }

    public string DestinationHint => $"输出会在该目录下新建 FlashKit_Backup_<设备序列号>_<时间> 文件夹";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy), nameof(CanStartBackup), nameof(CanCancelBackup));
                RaiseCommandStates();
            }
        }
    }

    public bool IsNotBusy => !IsBusy;
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public string ProgressStatus
    {
        get => _progressStatus;
        private set => SetProperty(ref _progressStatus, value);
    }

    public string BackupResultText
    {
        get => _backupResultText;
        private set
        {
            if (SetProperty(ref _backupResultText, value))
            {
                OnPropertyChanged(nameof(HasBackupResult));
            }
        }
    }

    public bool HasBackupResult => !string.IsNullOrWhiteSpace(BackupResultText);
    public string LastOutputDirectory
    {
        get => _lastOutputDirectory;
        private set
        {
            if (SetProperty(ref _lastOutputDirectory, value))
            {
                OnPropertyChanged(nameof(CanOpenOutput));
                OpenOutputCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanOpenOutput => !string.IsNullOrWhiteSpace(LastOutputDirectory) &&
                                 Directory.Exists(LastOutputDirectory);

    public int SelectedCount => _all.Count(p => p.IsSelected && p.IsEnabled);
    public long SelectedBytes => _all.Where(p => p.IsSelected && p.IsEnabled)
        .Sum(p => p.SizeBytes ?? 0);
    public bool HasUnknownSelectedSizes => _all.Any(p => p.IsSelected && p.IsEnabled && p.SizeBytes is null or <= 0);
    public bool HasSelection => SelectedCount > 0;
    public bool HasHighRiskSelection => _all.Any(p => p.IsSelected && p.IsEnabled && p.Risk != PartitionRisk.Normal);
    public bool HasSelectablePartitions => _all.Any(p => p.IsEnabled);
    public bool HasHighRiskPartitions => HighRiskPartitions.Count > 0;

    public string SelectedSummary => SelectedCount == 0
        ? "尚未勾选分区"
        : $"已选 {SelectedCount} 个分区，约 {PartitionInfo.FormatBytes(SelectedBytes)}" +
          (HasUnknownSelectedSizes ? "（部分分区大小未知）" : string.Empty);

    public string SelectionHint => "默认不勾选；仅实际勾选的分区会写入输出目录与 manifest。";

    public string SpaceStatus
    {
        get => _spaceStatus;
        private set => SetProperty(ref _spaceStatus, value);
    }

    public bool HasSpaceError
    {
        get => _spaceError;
        private set
        {
            if (SetProperty(ref _spaceError, value))
            {
                OnPropertyChanged(nameof(CanStartBackup));
            }
        }
    }

    public bool OrphanLogicalSelected => _all.Any(p => p.IsSuperContainer) &&
                                         !_all.Any(p => p.IsSuperContainer && p.IsSelected) &&
                                         _all.Any(p => p.IsSelected && p.IsEnabled &&
                                                       p.Group == BackupPartitionGroup.SuperLogical &&
                                                       !p.IsSuperContainer);

    public string OrphanLogicalMessage =>
        "独立备份逻辑分区可能不完整，建议改选 super 整体备份。此提示不阻断备份。";

    public bool CanCancelBackup => IsBusy;

    public bool CanStartBackup => !IsBusy && IsEnvironmentSupported && HasSelection && !HasSpaceError;

    public RelayCommand SelectAllCommand { get; }
    public RelayCommand InvertSelectionCommand { get; }
    public RelayCommand SelectRegularCommand { get; }
    public RelayCommand CancelBackupCommand { get; }
    public AsyncRelayCommand OpenOutputCommand { get; }
    public AsyncRelayCommand RefreshPartitionsCommand { get; }
    public AsyncRelayCommand RebootToRecoveryCommand { get; }
    public AsyncRelayCommand RebootToSystemCommand { get; }

    public async Task ActivateAsync()
    {
        _device = _deviceManager.SelectedDevice;
        UpdateDeviceState();
        if (CanReadBackupEnvironment && (_environment is null || _all.Count == 0))
        {
            await RefreshPartitionsAsync(force: false).ConfigureAwait(true);
        }
    }

    public async Task RefreshPartitionsAsync(bool force)
    {
        var device = _deviceManager.SelectedDevice;
        if (device is null)
        {
            SetError(new InvalidOperationException("请先在设备信息页选择设备。"));
            return;
        }

        if (device.Mode is not (DeviceMode.System or DeviceMode.Recovery))
        {
            ClearPartitions("当前模式不能读取分区。请进入系统（KSU root）或 TWRP；Fastboot/官方 Recovery 不支持。");
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var environment = await _environmentService
                .DetectAsync(device, force, CancellationToken.None)
                .ConfigureAwait(true);
            _environment = environment;
            IsRootAvailable = environment.IsRoot;
            _catalog = environment.Catalog;
            if (environment.Catalog is not null)
            {
                BuildGroups(environment.Catalog);
            }
            else
            {
                ClearGroups();
            }

            CatalogStatus = environment.Catalog is { Partitions.Count: > 0 } catalog
                ? $"{catalog.Partitions.Count} 个分区 · {(catalog.HasSizes ? "容量已读取" : "容量未知")} · {RootStatusText}"
                : environment.Message;
            WarningMessage = environment.IsSupported
                ? string.Join(Environment.NewLine, environment.Catalog?.Warnings ?? Array.Empty<string>())
                : environment.Message;
            RecomputeSelection();
            OnPropertyChanged(nameof(IsEnvironmentSupported), nameof(EnvironmentText), nameof(TransportText));
            RaiseCommandStates();
        }
        catch (Exception ex)
        {
            SetError(ex);
            CatalogStatus = "备份环境探测失败；请检查 ADB 授权与 Root。";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ClearGroups()
    {
        foreach (var item in _all)
        {
            item.PropertyChanged -= OnPartitionPropertyChanged;
        }

        _all.Clear();
        RegularPartitions.Clear();
        LogicalPartitions.Clear();
        HighRiskPartitions.Clear();
    }

    private async Task<bool> EnsureRootAsync(string serial)
    {
        try
        {
            var id = await _adb.ShellAsync(serial, "id", logToHistory: false).ConfigureAwait(true);
            if (id.Contains("uid=0", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var root = await _adb.ExecuteRawAsync(
                serial,
                ["root"],
                cancellationToken: default,
                logToHistory: true,
                timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            await Task.Delay(1200).ConfigureAwait(true);
            id = await _adb.ShellAsync(serial, "id", logToHistory: false).ConfigureAwait(true);
            var ok = id.Contains("uid=0", StringComparison.OrdinalIgnoreCase);
            if (!ok)
            {
                WarningMessage = "Recovery ADB 无法切换到 root：" +
                                 (string.IsNullOrWhiteSpace(root.ToReadableError()) ? "未知原因" : root.ToReadableError());
            }

            return ok;
        }
        catch (Exception ex)
        {
            _logging.Warn($"Recovery root 检查失败：{ex.Message}", "Backup");
            return false;
        }
    }

    public void ChangeDestination(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        DestinationRoot = path;
    }

    public async Task<BackupResult?> StartBackupAsync()
    {
        if (!CanStartBackup)
        {
            SetError(new InvalidOperationException(
                !IsEnvironmentSupported
                    ? "当前备份环境不可用：请进入系统 KSU Root 或 TWRP，并确认 Root 可用。"
                    : "请至少勾选一个分区并确认空间。"));
            return null;
        }

        var device = _deviceManager.SelectedDevice!;
        var selected = _all.Where(p => p.IsSelected && p.IsEnabled).ToArray();
        var options = new BackupRunOptions
        {
            Serial = device.Serial,
            DestinationRoot = DestinationRoot,
            AllPartitions = _all.Select(p => p.ToSelection()).ToArray(),
            SelectedPartitions = selected.Select(p => p.ToSelection()).ToArray(),
            DeviceCodename = _catalog?.DeviceCodename,
            DeviceModel = _environment?.Model,
            AndroidRelease = _environment?.AndroidRelease,
            TwrpVersion = _environment?.TwrpVersion,
            Transport = _environment?.Transport ?? BackupTransport.BootRoot,
            IsUserspace = false
        };

        IsBusy = true;
        ErrorMessage = string.Empty;
        BackupResultText = string.Empty;
        ProgressPercent = 0;
        ProgressStatus = "准备备份…";
        _backupCts?.Dispose();
        _backupCts = new CancellationTokenSource();

        try
        {
            TaskExecutionContext? queueContext = null;
            var progress = new Progress<BackupProgress>(p =>
            {
                ProgressPercent = p.Percentage;
                ProgressStatus = p.Status;
                queueContext?.Report(p.Percentage, p.Status);
            });
            var taskRequest = new OperationTaskRequest
            {
                Type = OperationTaskType.Backup,
                Title = $"备份 {selected.Length} 个分区",
                Serial = device.Serial,
                InputSummary = string.Join(", ", selected.Select(p => p.Name)),
                OutputPath = DestinationRoot
            };
            var result = await _taskQueue.RunAsync(taskRequest, async context =>
            {
                queueContext = context;
                var backupResult = await _backupService.BackupAsync(
                    options,
                    progress,
                    null,
                    context.CancellationToken).ConfigureAwait(false);
                context.SetOutputPath(backupResult.OutputDirectory);
                return backupResult;
            }, _backupCts.Token).ConfigureAwait(true);

            LastOutputDirectory = result.OutputDirectory;
            BackupResultText = result.Summary;
            if (result.Manifest.FailedPartitions.Count > 0)
            {
                ErrorMessage = "部分分区备份失败：" + string.Join("、", result.Manifest.FailedPartitions);
            }
            else if (result.Manifest.Cancelled)
            {
                ProgressStatus = "备份已取消";
            }
            else
            {
                ProgressStatus = "备份完成";
                ProgressPercent = 100;
            }

            WarningMessage = string.Join(Environment.NewLine, result.Manifest.Warnings);
            return result;
        }
        catch (OperationCanceledException)
        {
            ProgressStatus = "备份已取消";
            return null;
        }
        catch (Exception ex)
        {
            SetError(ex);
            ProgressStatus = "备份失败";
            return null;
        }
        finally
        {
            IsBusy = false;
            _backupCts?.Dispose();
            _backupCts = null;
            RaiseCommandStates();
        }
    }

    public void CancelBackup()
    {
        _backupCts?.Cancel();
    }

    public string BuildConfirmationText()
    {
        var selected = _all.Where(p => p.IsSelected && p.IsEnabled).ToArray();
        var names = string.Join("、", selected.Take(24).Select(p => p.DisplayName));
        if (selected.Length > 24)
        {
            names += $" 等 {selected.Length} 个分区";
        }

        var lines = new List<string>
        {
            $"目标设备：{DeviceSummary}",
            "读取方式：Recovery ADB root（只读块设备）",
            $"备份目录：{DestinationRoot}",
            $"已选分区：{names}",
            $"预计大小：{PartitionInfo.FormatBytes(SelectedBytes)}" + (HasUnknownSelectedSizes ? "（部分未知）" : string.Empty)
        };

        if (selected.Any(p => p.Risk != PartitionRisk.Normal))
        {
            lines.Add("包含高风险分区：读取也可能耗时较长，请保持 USB 连接稳定。");
        }

        if (OrphanLogicalSelected)
        {
            lines.Add(OrphanLogicalMessage);
        }

        lines.Add("未勾选的分区不会生成文件；manifest 会记录未备份分区名称。");
        return string.Join(Environment.NewLine, lines);
    }

    private void BuildGroups(PartitionCatalog catalog)
    {
        foreach (var item in _all)
        {
            item.PropertyChanged -= OnPartitionPropertyChanged;
        }

        _all.Clear();
        RegularPartitions.Clear();
        LogicalPartitions.Clear();
        HighRiskPartitions.Clear();

        var ordered = catalog.Partitions
            .OrderBy(p => p.Name.Equals("super", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var partition in ordered)
        {
            var vm = new BackupPartitionViewModel(partition);
            vm.PropertyChanged += OnPartitionPropertyChanged;
            _all.Add(vm);
            switch (vm.Group)
            {
                case BackupPartitionGroup.SuperLogical:
                    LogicalPartitions.Add(vm);
                    break;
                case BackupPartitionGroup.HighRisk:
                    HighRiskPartitions.Add(vm);
                    break;
                default:
                    RegularPartitions.Add(vm);
                    break;
            }
        }

        OnPropertyChanged(
            nameof(HasHighRiskPartitions),
            nameof(HasSelectablePartitions),
            nameof(SelectedSummary),
            nameof(CanStartBackup));
        RecomputeSelection();
    }

    private void ClearPartitions(string status)
    {
        foreach (var item in _all)
        {
            item.PropertyChanged -= OnPartitionPropertyChanged;
        }

        _all.Clear();
        RegularPartitions.Clear();
        LogicalPartitions.Clear();
        HighRiskPartitions.Clear();
        _catalog = null;
        _environment = null;
        CatalogStatus = status;
        IsRootAvailable = false;
        WarningMessage = string.Empty;
        OnPropertyChanged(nameof(IsEnvironmentSupported), nameof(EnvironmentText), nameof(TransportText));
        RecomputeSelection();
    }

    private void OnPartitionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_updatingCoverage || e.PropertyName != nameof(BackupPartitionViewModel.IsSelected))
        {
            return;
        }

        _updatingCoverage = true;
        try
        {
            var super = _all.FirstOrDefault(p => p.IsSuperContainer);
            var covered = super is not null && super.IsSelected;
            foreach (var logical in _all.Where(p => p.Group == BackupPartitionGroup.SuperLogical && !p.IsSuperContainer))
            {
                logical.SetCoveredBySuper(covered);
            }
        }
        finally
        {
            _updatingCoverage = false;
        }

        RecomputeSelection();
    }

    private void RecomputeSelection()
    {
        var required = SelectedBytes;
        if (SelectedCount == 0)
        {
            SpaceStatus = "未勾选分区，不计算备份空间。";
            HasSpaceError = false;
        }
        else if (string.IsNullOrWhiteSpace(DestinationRoot))
        {
            SpaceStatus = "尚未选择备份目录。";
            HasSpaceError = true;
        }
        else if (HasUnknownSelectedSizes)
        {
            SpaceStatus = $"预计至少 {PartitionInfo.FormatBytes(required)}；部分分区大小未知，无法精确校验。";
            HasSpaceError = false;
        }
        else
        {
            try
            {
                Directory.CreateDirectory(DestinationRoot);
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(DestinationRoot))!);
                const long reserve = 512L * 1024 * 1024;
                if (drive.AvailableFreeSpace < required + reserve)
                {
                    SpaceStatus = $"空间不足：预计 {PartitionInfo.FormatBytes(required)}，可用 " +
                                  $"{PartitionInfo.FormatBytes(drive.AvailableFreeSpace)}。";
                    HasSpaceError = true;
                }
                else
                {
                    SpaceStatus = $"预计 {PartitionInfo.FormatBytes(required)} · 可用 " +
                                  $"{PartitionInfo.FormatBytes(drive.AvailableFreeSpace)}";
                    HasSpaceError = false;
                }
            }
            catch (Exception ex)
            {
                SpaceStatus = $"无法检查空间：{ex.Message}";
                HasSpaceError = false;
            }
        }

        OnPropertyChanged(
            nameof(SelectedCount),
            nameof(SelectedBytes),
            nameof(SelectedSummary),
            nameof(HasSelection),
            nameof(HasUnknownSelectedSizes),
            nameof(HasHighRiskSelection),
            nameof(OrphanLogicalSelected),
            nameof(HasSelectablePartitions),
            nameof(HasHighRiskPartitions),
            nameof(CanStartBackup),
            nameof(CanOpenOutput));
        SelectAllCommand.NotifyCanExecuteChanged();
        InvertSelectionCommand.NotifyCanExecuteChanged();
        SelectRegularCommand.NotifyCanExecuteChanged();
    }

    private void SelectAll()
    {
        // 先快照，避免勾选 super 时动态禁用逻辑分区，导致枚举集合在循环中变化。
        foreach (var item in _all.Where(p => p.IsEnabled).ToArray())
        {
            item.IsSelected = true;
        }
    }

    private void InvertSelection()
    {
        foreach (var item in _all.Where(p => p.IsEnabled).ToArray())
        {
            item.IsSelected = !item.IsSelected;
        }
    }

    private void SelectRegular()
    {
        foreach (var item in _all.Where(p => p.IsEnabled).ToArray())
        {
            item.IsSelected = item.Group == BackupPartitionGroup.Regular;
        }
    }

    private async Task OpenOutputAsync()
    {
        if (CanOpenOutput)
        {
            await _processRunner.OpenFolderAsync(LastOutputDirectory).ConfigureAwait(true);
        }
    }

    private async Task RebootToRecoveryAsync()
    {
        var device = _deviceManager.SelectedDevice;
        if (device is null)
        {
            return;
        }

        if (device.Mode == DeviceMode.Fastboot)
        {
            await _fastboot.RebootAsync(device.Serial, "recovery").ConfigureAwait(true);
        }
        else
        {
            await _adb.RebootAsync(device.Serial, "recovery").ConfigureAwait(true);
        }
    }

    private async Task RebootToSystemAsync()
    {
        var device = _deviceManager.SelectedDevice;
        if (device is null)
        {
            return;
        }

        if (device.Mode == DeviceMode.Fastboot)
        {
            await _fastboot.RebootAsync(device.Serial).ConfigureAwait(true);
        }
        else
        {
            await _adb.RebootAsync(device.Serial).ConfigureAwait(true);
        }
    }

    public void Deactivate()
    {
    }

    private void OnDevicesChanged(object? sender, DeviceSnapshot snapshot)
    {
        void Apply()
        {
            _device = snapshot.SelectedDevice;
            UpdateDeviceState();
            var key = $"{snapshot.SelectedDevice?.Serial}|{snapshot.SelectedDevice?.Mode}|{snapshot.SelectedDevice?.State}";
            if (key == _lastDeviceKey)
            {
                return;
            }

            _lastDeviceKey = key;
            if (_device is not null && _device.Mode is DeviceMode.System or DeviceMode.Recovery)
            {
                _ = RefreshPartitionsAsync(force: true);
            }
            else
            {
                ClearPartitions("当前模式不能读取分区。请进入系统（KSU root）或 TWRP；Fastboot/官方 Recovery 不支持。");
            }
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

    private void UpdateDeviceState()
    {
        OnPropertyChanged(
            nameof(Device),
            nameof(HasDevice),
            nameof(DeviceSummary),
            nameof(ModeText),
            nameof(IsRecoveryMode),
            nameof(IsEnvironmentSupported),
            nameof(CanReadBackupEnvironment),
            nameof(EnvironmentText),
            nameof(TransportText),
            nameof(RootStatusText),
            nameof(CanStartBackup));
        RaiseCommandStates();
    }

    private void SetError(Exception ex)
    {
        ErrorMessage = ex.Message;
        _logging.Error(ex.Message, "Backup");
    }

    private void RaiseCommandStates()
    {
        RefreshPartitionsCommand.NotifyCanExecuteChanged();
        RebootToRecoveryCommand.NotifyCanExecuteChanged();
        RebootToSystemCommand.NotifyCanExecuteChanged();
        CancelBackupCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        InvertSelectionCommand.NotifyCanExecuteChanged();
        SelectRegularCommand.NotifyCanExecuteChanged();
        OpenOutputCommand.NotifyCanExecuteChanged();
    }
}
