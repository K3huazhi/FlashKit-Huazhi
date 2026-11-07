using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using FlashKit.App.Services;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using FlashKit.Core.Parsing;
using Microsoft.UI.Dispatching;

namespace FlashKit.App.ViewModels;

public enum FlashInputMode
{
    Manual = 0,
    FolderScan = 1
}

public sealed class FlashPreflightReport
{
    public required FlashRunOptions Options { get; init; }
    public required IReadOnlyList<FlashTaskViewModel> Tasks { get; init; }
    public required IReadOnlyList<FlashValidationResult> Results { get; init; }
    public bool IsValid { get; init; }
    public bool HasCritical { get; init; }
    public bool HasWarnings { get; init; }
    public bool IsFastbootd { get; init; }
    public string DeviceCodename { get; init; } = string.Empty;
    public IReadOnlyList<string> BlockingMessages { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> WarningMessages { get; init; } = Array.Empty<string>();
    public long TotalBytes { get; init; }
    public string SummaryText => $"共 {Tasks.Count} 项，约 {PartitionInfo.FormatBytes(TotalBytes)}";
}

public sealed class FlashViewModel : ObservableObject
{
    private readonly IDeviceManager _deviceManager;
    private readonly IFastbootClient _fastboot;
    private readonly IPartitionCatalogService _catalogService;
    private readonly IFlashValidationService _validation;
    private readonly IFlashService _flashService;
    private readonly ITaskQueueService _taskQueue;
    private readonly ILoggingService _logging;
    private readonly AppSessionState _sessionState;
    private readonly DispatcherQueue? _dispatcher;
    private readonly Dictionary<string, CachedScan> _scanCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _liveOutput = new();
    private CancellationTokenSource? _flashCts;
    private CancellationTokenSource? _scanCts;
    private PartitionCatalog? _catalog;
    private AndroidDevice? _device;
    private FlashInputMode _inputMode = FlashInputMode.Manual;
    private string _manualPartitionName = string.Empty;
    private string _scanFolderPath = string.Empty;
    private string _catalogStatus = "尚未读取分区目录";
    private string _partitionWarnings = string.Empty;
    private string _scanStatus = "尚未扫描文件夹";
    private string _expectedCodename = string.Empty;
    private string _errorMessage = string.Empty;
    private string _statusMessage = string.Empty;
    private string _liveOutputText = string.Empty;
    private string _progressStatus = string.Empty;
    private bool _verifyDeviceCodename = true;
    private bool _continueOnFailure;
    private bool _isLoadingPartitions;
    private bool _isScanning;
    private bool _isFlashing;
    private bool _isActive;
    private bool _isFastbootd;
    private double _progressPercent;
    private int _creationCounter;
    private string _lastDeviceKey = string.Empty;
    private string _fastbootdStatus = "未知";

    public FlashViewModel(
        IDeviceManager deviceManager,
        IFastbootClient fastboot,
        IPartitionCatalogService catalogService,
        IFlashValidationService validation,
        IFlashService flashService,
        ITaskQueueService taskQueue,
        ILoggingService logging,
        AppSessionState sessionState)
    {
        _deviceManager = deviceManager;
        _fastboot = fastboot;
        _catalogService = catalogService;
        _validation = validation;
        _flashService = flashService;
        _taskQueue = taskQueue;
        _logging = logging;
        _sessionState = sessionState;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        Tasks.CollectionChanged += OnTasksCollectionChanged;
        _deviceManager.DevicesChanged += OnDevicesChanged;

        RefreshPartitionsCommand = new AsyncRelayCommand(
            () => RefreshPartitionsAsync(force: true),
            () => !IsBusy && HasDevice,
            ex => SetError(ex));
        RebootToFastbootCommand = new AsyncRelayCommand(
            RebootToFastbootAsync,
            () => !IsBusy && HasDevice,
            ex => SetError(ex));
        RebootToFastbootdCommand = new AsyncRelayCommand(
            RebootToFastbootdAsync,
            () => !IsBusy && HasDevice,
            ex => SetError(ex));
        RebootToSystemCommand = new AsyncRelayCommand(
            RebootToSystemAsync,
            () => !IsBusy && HasDevice,
            ex => SetError(ex));
        CancelFlashCommand = new RelayCommand(CancelFlash, () => IsFlashing);
        ClearLiveOutputCommand = new RelayCommand(ClearLiveOutput);
        ClearTasksCommand = new RelayCommand(ClearTasks, () => !IsFlashing && Tasks.Count > 0);
        SelectAllCandidatesCommand = new RelayCommand(SelectAllCandidates, () => MatchedCandidates.Count + UnmatchedCandidates.Count > 0);
        InvertCandidatesCommand = new RelayCommand(InvertCandidates, () => MatchedCandidates.Count + UnmatchedCandidates.Count > 0);
        SelectRecognizedCandidatesCommand = new RelayCommand(SelectRecognizedCandidates, () => MatchedCandidates.Count > 0);
        AddSelectedCandidatesCommand = new AsyncRelayCommand(
            AddSelectedCandidatesAsync,
            () => !IsFlashing && SelectedCandidateCount > 0,
            ex => SetError(ex));
        RescanFolderCommand = new AsyncRelayCommand(
            () => ScanFolderAsync(ScanFolderPath, force: true),
            () => !IsFlashing && !string.IsNullOrWhiteSpace(ScanFolderPath) && !IsScanning,
            ex => SetError(ex));
        CancelScanCommand = new RelayCommand(CancelScan, () => IsScanning);

        UpdateFromDeviceManager();
    }

    public ObservableCollection<FlashTaskViewModel> Tasks { get; } = new();
    public ObservableCollection<PartitionInfo> Partitions { get; } = new();
    public ObservableCollection<ScanCandidateViewModel> MatchedCandidates { get; } = new();
    public ObservableCollection<ScanCandidateViewModel> UnmatchedCandidates { get; } = new();

    public AndroidDevice? Device => _device;
    public bool HasDevice => _device is not null;
    public string DeviceSerialText => _device?.Serial ?? _deviceManager.SelectedSerial ?? "未选择设备";
    public string DeviceModeText => _device?.Mode.ToChinese() ?? "—";
    public string DeviceSummary => _device is null
        ? "请先在“设备信息”页选择设备。"
        : $"{_device.DisplayName} · {_device.Mode.ToChinese()}";
    public bool IsFastbootMode => _device?.Mode == DeviceMode.Fastboot;
    public bool IsSystemMode => _device?.Mode == DeviceMode.System;

    public FlashInputMode InputMode
    {
        get => _inputMode;
        set
        {
            if (SetProperty(ref _inputMode, value))
            {
                OnPropertyChanged(nameof(InputModeIndex), nameof(IsManualMode), nameof(IsScanMode));
            }
        }
    }

    public int InputModeIndex
    {
        get => (int)InputMode;
        set => InputMode = (FlashInputMode)Math.Clamp(value, 0, 1);
    }

    public bool IsManualMode => InputMode == FlashInputMode.Manual;
    public bool IsScanMode => InputMode == FlashInputMode.FolderScan;

    /// <summary>手动模式的目标分区名。留空时由随后选择的文件名推导。</summary>
    public string ManualPartitionName
    {
        get => _manualPartitionName;
        set
        {
            if (SetProperty(ref _manualPartitionName, value))
            {
                OnPropertyChanged(nameof(ManualPartitionHint));
            }
        }
    }

    public string ManualPartitionHint =>
        string.IsNullOrWhiteSpace(ManualPartitionName)
            ? "留空则从文件名推导（boot.img → boot）"
            : "加入后仍可在任务清单中逐条修改";

    public PartitionCatalog? Catalog => _catalog;

    public string CatalogStatus
    {
        get => _catalogStatus;
        private set => SetProperty(ref _catalogStatus, value);
    }

    public string PartitionWarnings
    {
        get => _partitionWarnings;
        private set
        {
            if (SetProperty(ref _partitionWarnings, value))
            {
                OnPropertyChanged(nameof(HasPartitionWarnings));
            }
        }
    }

    public bool HasPartitionWarnings => !string.IsNullOrWhiteSpace(PartitionWarnings);

    public string FastbootdStatus
    {
        get => _fastbootdStatus;
        private set => SetProperty(ref _fastbootdStatus, value);
    }

    public bool IsFastbootd
    {
        get => _isFastbootd;
        private set
        {
            if (SetProperty(ref _isFastbootd, value))
            {
                OnPropertyChanged(nameof(DynamicHint));
            }
        }
    }

    public string ScanFolderPath
    {
        get => _scanFolderPath;
        private set
        {
            if (SetProperty(ref _scanFolderPath, value))
            {
                RescanFolderCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ScanStatus
    {
        get => _scanStatus;
        private set => SetProperty(ref _scanStatus, value);
    }

    public string ExpectedCodename
    {
        get => _expectedCodename;
        set => SetProperty(ref _expectedCodename, value);
    }

    public bool VerifyDeviceCodename
    {
        get => _verifyDeviceCodename;
        set => SetProperty(ref _verifyDeviceCodename, value);
    }

    public bool ContinueOnFailure
    {
        get => _continueOnFailure;
        set => SetProperty(ref _continueOnFailure, value);
    }

    public bool IsLoadingPartitions
    {
        get => _isLoadingPartitions;
        private set
        {
            if (SetProperty(ref _isLoadingPartitions, value))
            {
                OnPropertyChanged(nameof(IsBusy), nameof(CanStartFlash), nameof(IsNotFlashing));
                RaiseCommandStates();
            }
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(IsBusy), nameof(CanStartFlash), nameof(IsNotFlashing));
                CancelScanCommand.NotifyCanExecuteChanged();
                RaiseCommandStates();
            }
        }
    }

    public bool IsFlashing
    {
        get => _isFlashing;
        private set
        {
            if (SetProperty(ref _isFlashing, value))
            {
                OnPropertyChanged(nameof(IsBusy), nameof(CanStartFlash), nameof(IsNotFlashing));
                CancelFlashCommand.NotifyCanExecuteChanged();
                RaiseCommandStates();
            }
        }
    }

    public bool IsNotFlashing => !IsFlashing;
    public bool IsBusy => IsLoadingPartitions || IsScanning || IsFlashing;

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

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public string LiveOutputText
    {
        get => _liveOutputText;
        private set => SetProperty(ref _liveOutputText, value);
    }

    public string ProgressStatus
    {
        get => _progressStatus;
        private set => SetProperty(ref _progressStatus, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public int SelectedTaskCount => Tasks.Count(t => t.IsSelected);
    public long SelectedBytes => Tasks.Where(t => t.IsSelected).Sum(t => t.EffectiveSizeBytes);
    public bool HasSelectedTasks => SelectedTaskCount > 0;
    public bool HasTasks => Tasks.Count > 0;

    public bool HasCriticalSelected => Tasks.Any(t => t.IsSelected && t.IsCritical);

    public string SummaryText => $"共 {SelectedTaskCount} 项，约 {PartitionInfo.FormatBytes(SelectedBytes)}";

    public string HighRiskNotice => Tasks.Any(t => t.IsSelected && t.Risk == PartitionRisk.Critical)
        ? "清单中包含高危分区，开始前需要二次确认。"
        : string.Empty;

    public string DynamicHint
    {
        get
        {
            var hasDynamic = Tasks.Any(t => t.IsSelected && t.IsDynamic);
            if (!hasDynamic)
            {
                return string.Empty;
            }

            return IsFastbootd
                ? "清单包含动态分区，当前已在 Fastbootd，可直接刷入。"
                : "清单包含动态分区（system/vendor 等），建议进入 Fastbootd；仅提示，不阻断按名称刷入。";
        }
    }

    public bool HasDynamicSelected => Tasks.Any(t => t.IsSelected && t.IsDynamic);

    public bool CanStartFlash => HasSelectedTasks && IsFastbootMode && !IsBusy;

    public int SelectedCandidateCount =>
        MatchedCandidates.Concat(UnmatchedCandidates).Count(c => c.IsSelected);

    public AsyncRelayCommand RefreshPartitionsCommand { get; }
    public AsyncRelayCommand RebootToFastbootCommand { get; }
    public AsyncRelayCommand RebootToFastbootdCommand { get; }
    public AsyncRelayCommand RebootToSystemCommand { get; }
    public RelayCommand CancelFlashCommand { get; }
    public RelayCommand ClearLiveOutputCommand { get; }
    public RelayCommand ClearTasksCommand { get; }
    public RelayCommand SelectAllCandidatesCommand { get; }
    public RelayCommand InvertCandidatesCommand { get; }
    public RelayCommand SelectRecognizedCandidatesCommand { get; }
    public AsyncRelayCommand AddSelectedCandidatesCommand { get; }
    public AsyncRelayCommand RescanFolderCommand { get; }
    public RelayCommand CancelScanCommand { get; }

    public async Task ActivateAsync()
    {
        _isActive = true;
        UpdateFromDeviceManager();
        if (HasDevice)
        {
            await RefreshPartitionsAsync(force: false).ConfigureAwait(true);
        }
    }

    public void Deactivate()
    {
        _isActive = false;
    }

    public async Task RefreshPartitionsAsync(bool force)
    {
        var device = _deviceManager.SelectedDevice;
        if (device is null)
        {
            CatalogStatus = "未选择设备";
            SetError(new InvalidOperationException("请先选择设备。"));
            return;
        }

        if (device.Mode is not (DeviceMode.System or DeviceMode.Fastboot))
        {
            Partitions.Clear();
            CatalogStatus = "当前模式下无法读取分区目录，请进入系统或 Fastboot 模式。";
            return;
        }

        IsLoadingPartitions = true;
        ErrorMessage = string.Empty;
        try
        {
            var catalog = await _catalogService.GetAsync(device.Serial, device.Mode, force).ConfigureAwait(true);
            _catalog = catalog;
            Partitions.Clear();
            foreach (var partition in catalog.Partitions)
            {
                Partitions.Add(partition);
            }

            CatalogStatus = catalog.Partitions.Count == 0
                ? "未读取到分区。请确认权限或切换到 Fastboot 模式。"
                : $"{catalog.Partitions.Count} 个分区 · {(catalog.HasSizes ? "容量已读取" : "容量未知")} · 来源 {SourceText(catalog.Source)}";
            PartitionWarnings = string.Join(Environment.NewLine, catalog.Warnings);

            if (string.IsNullOrWhiteSpace(ExpectedCodename) && !string.IsNullOrWhiteSpace(catalog.DeviceCodename))
            {
                ExpectedCodename = catalog.DeviceCodename;
            }

            IsFastbootd = catalog.IsUserspace ?? false;
            FastbootdStatus = catalog.IsUserspace switch
            {
                true => "Fastbootd（is-userspace=yes）",
                false => "Bootloader（is-userspace=no）",
                _ => "未知"
            };

            UpdateTasksFromCatalog();
            _scanCache.Clear();
            await RefreshFastbootdAsync(device, catalog).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SetError(ex);
            CatalogStatus = "分区读取失败";
        }
        finally
        {
            IsLoadingPartitions = false;
            UpdateSummary();
        }
    }

    private async Task RefreshFastbootdAsync(AndroidDevice device, PartitionCatalog catalog)
    {
        if (device.Mode != DeviceMode.Fastboot || catalog.IsUserspace is not null)
        {
            return;
        }

        try
        {
            var userspace = await _fastboot.IsUserspaceAsync(device.Serial).ConfigureAwait(true);
            if (userspace is not null)
            {
                IsFastbootd = userspace.Value;
                FastbootdStatus = userspace.Value ? "Fastbootd（is-userspace=yes）" : "Bootloader（is-userspace=no）";
            }
        }
        catch
        {
            FastbootdStatus = "未知";
        }
    }

    public void AddManualTask(string filePath)
    {
        var target = string.IsNullOrWhiteSpace(ManualPartitionName)
            ? Path.GetFileNameWithoutExtension(filePath)
            : ManualPartitionName.Trim();
        if (AddTaskFromName(target, filePath))
        {
            ManualPartitionName = string.Empty;
        }
    }

    /// <summary>
    /// 按目标分区名直接加入待刷清单。分区目录只用于可选的大小提示，不是加入条件。
    /// </summary>
    public bool AddTaskFromName(string partitionName, string filePath)
    {
        var target = partitionName?.Trim() ?? string.Empty;
        if (target.Length == 0)
        {
            SetError(new InvalidOperationException("目标分区名不能为空。"));
            return false;
        }

        if (!File.Exists(filePath))
        {
            SetError(new InvalidOperationException("镜像文件不存在。"));
            return false;
        }

        if (Tasks.Any(t => t.PartitionName.Equals(target, StringComparison.OrdinalIgnoreCase)))
        {
            SetError(new InvalidOperationException($"目标分区 {target} 已在清单中；可先移除旧条目再添加。"));
            return false;
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length == 0)
        {
            SetError(new InvalidOperationException("镜像文件为空。"));
            return false;
        }

        var known = _catalog?.Partitions.FirstOrDefault(p =>
            p.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
        var sparse = ImageFormats.Inspect(filePath);
        var task = new FlashTaskViewModel(
            _creationCounter++,
            target,
            filePath,
            fileInfo.Length,
            sparse,
            known?.SizeBytes,
            PartitionRules.IsDynamic(target));
        Tasks.Add(task);
        SortTasks();
        ErrorMessage = string.Empty;
        StatusMessage = $"已加入 {target} ← {Path.GetFileName(filePath)}";
        UpdateSummary();
        return true;
    }

    public void RemoveTask(FlashTaskViewModel task)
    {
        if (IsFlashing)
        {
            return;
        }

        task.PropertyChanged -= OnTaskPropertyChanged;
        Tasks.Remove(task);
        UpdateSummary();
    }

    public async Task ScanFolderAsync(string folderPath, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            SetError(new InvalidOperationException("文件夹不存在。"));
            return;
        }

        ScanFolderPath = folderPath;
        var signature = CatalogSignature();
        if (!force && _scanCache.TryGetValue(folderPath, out var cached) && cached.Signature == signature)
        {
            ApplyScan(cached);
            ScanStatus = $"{MatchedCandidates.Count} 个已匹配，{UnmatchedCandidates.Count} 个未匹配（会话缓存）";
            return;
        }

        IsScanning = true;
        ErrorMessage = string.Empty;
        ScanStatus = "正在后台扫描…";
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var scanToken = _scanCts.Token;
        try
        {
            MatchedCandidates.Clear();
            UnmatchedCandidates.Clear();
            var available = _catalog?.Partitions.Select(p => p.Name).ToArray() ?? Array.Empty<string>();
            var scanProgress = new Progress<int>(count =>
                RunOnUi(() =>
                {
                    if (!scanToken.IsCancellationRequested)
                    {
                        ScanStatus = $"已扫描 {count} 个镜像…";
                    }
                }));

            // 目录枚举、文件大小与 sparse 头解析都放到线程池，避免大目录卡住 UI。
            var scanResult = await Task.Run(() =>
            {
                var files = Directory.EnumerateFiles(folderPath, "*.img", SearchOption.AllDirectories)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var matched = new List<ScanCandidateViewModel>();
                var unmatched = new List<ScanCandidateViewModel>();
                foreach (var file in files)
                {
                    scanToken.ThrowIfCancellationRequested();
                    var suggested = PartitionMatcher.InferPartitionName(file, available)
                                    ?? Path.GetFileNameWithoutExtension(file);
                    var candidate = new ScanCandidateViewModel(file, suggested, available);
                    if (candidate.IsMatched)
                    {
                        matched.Add(candidate);
                    }
                    else
                    {
                        unmatched.Add(candidate);
                    }

                    ((IProgress<int>)scanProgress).Report(matched.Count + unmatched.Count);
                }

                return new ScanBuildResult(
                    files.Length,
                    matched.OrderBy(c => PartitionRules.GetRecommendedOrder(c.PartitionName))
                        .ThenBy(c => c.PartitionName, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    unmatched.OrderBy(c => c.FileName, StringComparer.OrdinalIgnoreCase).ToList());
            }, scanToken).ConfigureAwait(true);

            if (scanResult.FileCount == 0)
            {
                ScanStatus = "文件夹中没有 .img 文件。";
                _scanCache[folderPath] = new CachedScan(signature, [], []);
                return;
            }

            foreach (var candidate in scanResult.Matched.Concat(scanResult.Unmatched))
            {
                candidate.PropertyChanged += OnCandidatePropertyChanged;
            }

            var cache = new CachedScan(signature, scanResult.Matched, scanResult.Unmatched);
            _scanCache[folderPath] = cache;
            ApplyScan(cache);
            ScanStatus = $"扫描完成：{scanResult.Matched.Count} 个已匹配，{scanResult.Unmatched.Count} 个未匹配。请勾选后加入清单。";
            _logging.Info($"扫描文件夹 {folderPath}：{scanResult.FileCount} 个镜像", "Flash");
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "扫描已取消。";
        }
        catch (Exception ex)
        {
            SetError(ex);
        }
        finally
        {
            IsScanning = false;
            _scanCts?.Dispose();
            _scanCts = null;
            OnPropertyChanged(nameof(SelectedCandidateCount));
            AddSelectedCandidatesCommand.NotifyCanExecuteChanged();
            SelectAllCandidatesCommand.NotifyCanExecuteChanged();
            InvertCandidatesCommand.NotifyCanExecuteChanged();
            SelectRecognizedCandidatesCommand.NotifyCanExecuteChanged();
        }
    }

    private void CancelScan() => _scanCts?.Cancel();

    public Task AddSelectedCandidatesAsync()
    {
        var selected = MatchedCandidates.Concat(UnmatchedCandidates).Where(c => c.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            return Task.CompletedTask;
        }

        var added = 0;
        foreach (var candidate in selected)
        {
            if (AddTaskFromName(candidate.PartitionName, candidate.FilePath))
            {
                candidate.IsSelected = false;
                added++;
            }
        }

        StatusMessage = $"已加入 {added} 个扫描结果。";
        OnPropertyChanged(nameof(SelectedCandidateCount));
        AddSelectedCandidatesCommand.NotifyCanExecuteChanged();
        return Task.CompletedTask;
    }

    public async Task<FlashPreflightReport> BuildPreflightAsync()
    {
        var blocking = new List<string>();
        var warnings = new List<string>();
        var selectedTasks = Tasks.Where(t => t.IsSelected)
            .OrderBy(t => t.Sequence)
            .ToArray();
        var options = new FlashRunOptions
        {
            Serial = _deviceManager.SelectedDevice?.Serial ?? _deviceManager.SelectedSerial ?? string.Empty,
            ContinueOnFailure = ContinueOnFailure,
            ExpectedCodename = ExpectedCodename?.Trim(),
            VerifyDeviceCodename = VerifyDeviceCodename,
            AllowCriticalPartitions = true,
            AllowUnknownPartitionSizes = true
        };

        var device = _deviceManager.SelectedDevice;
        if (device is null)
        {
            blocking.Add("请先选择设备。");
        }
        else if (device.State != DeviceConnectionState.Connected)
        {
            blocking.Add($"设备状态异常：{device.State}。请重新连接 USB。");
        }
        else if (device.Mode != DeviceMode.Fastboot)
        {
            blocking.Add("刷入需要设备处于 Fastboot 模式。请先重启到 Fastboot。");
        }

        if (selectedTasks.Length == 0)
        {
            blocking.Add("待刷任务清单为空。");
        }

        var duplicateTarget = selectedTasks
            .GroupBy(t => PartitionRules.StripSlotSuffix(t.PartitionName), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateTarget is not null)
        {
            blocking.Add($"目标分区 {duplicateTarget.Key} 在清单中重复；请合并或修改后再刷入。");
        }

        var catalog = _catalog ?? new PartitionCatalog
        {
            Partitions = Array.Empty<PartitionInfo>(),
            Source = PartitionSource.FastbootGetVar
        };

        var userspace = catalog.IsUserspace;
        if (userspace is null && device is not null && device.Mode == DeviceMode.Fastboot)
        {
            try
            {
                userspace = await _fastboot.IsUserspaceAsync(device.Serial).ConfigureAwait(true);
                IsFastbootd = userspace == true;
            }
            catch
            {
                // 无法确认时由校验器按未进入 Fastbootd 处理。
            }
        }

        var results = new List<FlashValidationResult>();
        foreach (var task in selectedTasks)
        {
            var result = await _validation.ValidateAsync(
                task.ToFlashItem(),
                catalog,
                options,
                userspace == true).ConfigureAwait(true);
            task.ApplyValidation(result);
            results.Add(result);
            foreach (var issue in result.Issues)
            {
                if (issue.IsBlocking)
                {
                    blocking.Add($"{task.PartitionName}：{issue.Message}");
                }
                else if (issue.Kind == OperationKind.Warning)
                {
                    warnings.Add($"{task.PartitionName}：{issue.Message}");
                }
            }
        }

        if (selectedTasks.Any(t => t.IsCritical))
        {
            warnings.Add("清单包含 persist / modem / xbl 等高危分区，刷错可能导致设备无法开机。");
        }

        if (VerifyDeviceCodename && string.IsNullOrWhiteSpace(ExpectedCodename))
        {
            warnings.Add("未填写期望设备代号，已跳过可选的设备代号核对。");
        }

        if (!VerifyDeviceCodename)
        {
            warnings.Add("设备代号核对已关闭；仅影响提示，不影响按分区名刷入。");
        }

        if (catalog.Partitions.Count == 0)
        {
            warnings.Add("当前没有可用的分区目录提示；仍会按清单中的目标分区名直接执行 fastboot flash。");
        }

        warnings.AddRange(catalog.Warnings);
        blocking = blocking.Distinct(StringComparer.Ordinal).ToList();
        warnings = warnings.Distinct(StringComparer.Ordinal).ToList();

        return new FlashPreflightReport
        {
            Options = options,
            Tasks = selectedTasks,
            Results = results,
            IsValid = blocking.Count == 0,
            HasCritical = selectedTasks.Any(t => t.IsCritical),
            HasWarnings = warnings.Count > 0,
            IsFastbootd = userspace == true,
            DeviceCodename = catalog.DeviceCodename ?? string.Empty,
            BlockingMessages = blocking,
            WarningMessages = warnings,
            TotalBytes = selectedTasks.Sum(t => t.EffectiveSizeBytes)
        };
    }

    public async Task<FlashRunSummary?> StartFlashAsync(FlashPreflightReport report)
    {
        if (!report.IsValid)
        {
            SetError(new InvalidOperationException("预检未通过，已取消刷入。"));
            return null;
        }

        if (IsFlashing)
        {
            return null;
        }

        _flashCts = new CancellationTokenSource();
        IsFlashing = true;
        _sessionState.IsFlashing = true;
        _sessionState.FlashingDescription = $"正在刷入 {report.Tasks.Count} 项到 {report.Options.Serial}";
        ProgressPercent = 0;
        ProgressStatus = "开始刷入…";
        StatusMessage = "刷写中，导航已锁定。可在此页取消或查看原始日志。";
        LiveOutputText = string.Empty;
        _liveOutput.Clear();
        ErrorMessage = string.Empty;

        try
        {
            TaskExecutionContext? queueContext = null;
            var progress = new Progress<FlashProgress>(p =>
            {
                ProgressPercent = p.Percentage;
                ProgressStatus = string.IsNullOrWhiteSpace(p.PartitionName)
                    ? p.Status
                    : $"{p.Status} · {p.PartitionName}（{p.CurrentIndex}/{p.TotalCount}）";
                queueContext?.Report(p.Percentage, ProgressStatus);
            });
            var flashItems = report.Tasks.Select(t => t.ToFlashItem()).ToArray();
            var summary = await _taskQueue.RunAsync(
                new OperationTaskRequest
                {
                    Type = OperationTaskType.Flash,
                    Title = $"刷入 {flashItems.Length} 项",
                    Serial = report.Options.Serial,
                    InputSummary = string.Join(", ", report.Tasks.Select(t => $"{t.PartitionName} ← {t.FileName}"))
                },
                async context =>
                {
                    queueContext = context;
                    return await _flashService.ExecuteAsync(
                        report.Options,
                        flashItems,
                        progress,
                        line => RunOnUi(() => AppendLive(line)),
                        context.CancellationToken).ConfigureAwait(false);
                },
                _flashCts.Token).ConfigureAwait(true);

            var byId = report.Tasks.ToDictionary(t => t.Id);
            foreach (var result in summary.Results)
            {
                if (byId.TryGetValue(result.Item.Id, out var task))
                {
                    task.ApplyResult(result);
                }
            }

            ProgressPercent = 100;
            ProgressStatus = summary.Cancelled ? "已取消" : "刷入结束";
            StatusMessage = summary.Cancelled
                ? $"已取消：成功 {summary.SuccessCount}，失败 {summary.FailureCount}，未执行 {summary.SkippedCount}。"
                : $"刷入结束：成功 {summary.SuccessCount}，失败 {summary.FailureCount}，跳过 {summary.SkippedCount}。";
            if (summary.FailureCount > 0)
            {
                ErrorMessage = "存在刷入失败项，请查看实时输出与原始日志。";
            }

            return summary;
        }
        catch (OperationCanceledException)
        {
            ProgressStatus = "已取消";
            StatusMessage = "刷入已取消。";
            return null;
        }
        catch (Exception ex)
        {
            SetError(ex);
            ProgressStatus = "刷入失败";
            return null;
        }
        finally
        {
            IsFlashing = false;
            _sessionState.IsFlashing = false;
            _sessionState.FlashingDescription = string.Empty;
            _flashCts.Dispose();
            _flashCts = null;
            RaiseCommandStates();
        }
    }

    public void CancelFlash()
    {
        _flashCts?.Cancel();
        StatusMessage = "正在取消…";
    }

    public void ClearTasks()
    {
        foreach (var task in Tasks)
        {
            task.PropertyChanged -= OnTaskPropertyChanged;
        }

        Tasks.Clear();
        UpdateSummary();
    }

    private void SelectAllCandidates()
    {
        foreach (var candidate in MatchedCandidates.Concat(UnmatchedCandidates))
        {
            candidate.IsSelected = true;
        }

        NotifyCandidateSelectionChanged();
    }

    private void InvertCandidates()
    {
        foreach (var candidate in MatchedCandidates.Concat(UnmatchedCandidates))
        {
            candidate.IsSelected = !candidate.IsSelected;
        }

        NotifyCandidateSelectionChanged();
    }

    private void SelectRecognizedCandidates()
    {
        foreach (var candidate in MatchedCandidates)
        {
            candidate.IsSelected = true;
        }

        foreach (var candidate in UnmatchedCandidates)
        {
            candidate.IsSelected = false;
        }

        NotifyCandidateSelectionChanged();
    }

    private void NotifyCandidateSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCandidateCount));
        AddSelectedCandidatesCommand.NotifyCanExecuteChanged();
    }

    private async Task RebootToFastbootAsync()
    {
        await _deviceManager.RebootSelectedToFastbootAsync().ConfigureAwait(true);
        await WaitForModeAsync(DeviceMode.Fastboot).ConfigureAwait(true);
    }

    private async Task RebootToFastbootdAsync()
    {
        await _deviceManager.RebootSelectedToFastbootdAsync().ConfigureAwait(true);
        await WaitForModeAsync(DeviceMode.Fastboot).ConfigureAwait(true);
        if (_deviceManager.SelectedDevice is { Mode: DeviceMode.Fastboot } device)
        {
            var userspace = await _fastboot.IsUserspaceAsync(device.Serial).ConfigureAwait(true);
            IsFastbootd = userspace == true;
            FastbootdStatus = userspace switch
            {
                true => "Fastbootd（is-userspace=yes）",
                false => "Bootloader（is-userspace=no）",
                _ => "未知（设备可能不支持 Fastbootd）"
            };
        }
    }

    private async Task RebootToSystemAsync()
    {
        await _deviceManager.RebootSelectedToSystemAsync().ConfigureAwait(true);
        StatusMessage = "已请求重启到系统。";
    }

    private async Task WaitForModeAsync(DeviceMode mode)
    {
        ProgressStatus = "等待设备重新连接…";
        var device = await _deviceManager.WaitForModeAsync(mode, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        if (device is null)
        {
            SetError(new InvalidOperationException($"等待设备进入 {mode.ToChinese()} 超时，请检查 USB 连接与驱动。"));
            return;
        }

        await RefreshPartitionsAsync(force: true).ConfigureAwait(true);
        StatusMessage = $"设备已进入 {mode.ToChinese()}。";
    }

    private void UpdateFromDeviceManager()
    {
        _device = _deviceManager.SelectedDevice;
        var key = $"{_deviceManager.SelectedSerial}|{_device?.Mode}|{_device?.State}";
        var changed = key != _lastDeviceKey;
        _lastDeviceKey = key;
        if (changed)
        {
            _catalog = null;
            Partitions.Clear();
            ExpectedCodename = string.Empty;
            IsFastbootd = false;
            FastbootdStatus = "未知";
        }

        OnPropertyChanged(
            nameof(Device), nameof(HasDevice), nameof(DeviceSerialText), nameof(DeviceModeText),
            nameof(DeviceSummary), nameof(IsFastbootMode), nameof(IsSystemMode),
            nameof(CanStartFlash), nameof(IsNotFlashing));
        RefreshPartitionsCommand.NotifyCanExecuteChanged();
        RebootToFastbootCommand.NotifyCanExecuteChanged();
        RebootToFastbootdCommand.NotifyCanExecuteChanged();
        RebootToSystemCommand.NotifyCanExecuteChanged();
        if (changed && _isActive && _device is not null && !IsFlashing)
        {
            _ = RefreshPartitionsAsync(force: true);
        }
    }

    private void OnDevicesChanged(object? sender, DeviceSnapshot snapshot) =>
        RunOnUi(UpdateFromDeviceManager);

    private void UpdateTasksFromCatalog()
    {
        if (_catalog is null)
        {
            return;
        }

        foreach (var task in Tasks)
        {
            var partition = _catalog.Partitions.FirstOrDefault(p =>
                p.Name.Equals(task.PartitionName, StringComparison.OrdinalIgnoreCase));
            task.PartitionSizeBytes = partition?.SizeBytes;
        }

        OnPropertyChanged(nameof(CanStartFlash), nameof(DynamicHint), nameof(HasDynamicSelected));
    }

    private void SortTasks()
    {
        var ordered = Tasks
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.CreationIndex)
            .ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            ordered[i].Sequence = i + 1;
        }

        if (!Tasks.SequenceEqual(ordered))
        {
            Tasks.Clear();
            foreach (var task in ordered)
            {
                Tasks.Add(task);
            }
        }
    }

    private void ApplyScan(CachedScan scan)
    {
        MatchedCandidates.Clear();
        UnmatchedCandidates.Clear();
        foreach (var candidate in scan.Matched)
        {
            MatchedCandidates.Add(candidate);
        }

        foreach (var candidate in scan.Unmatched)
        {
            UnmatchedCandidates.Add(candidate);
        }

        OnPropertyChanged(nameof(SelectedCandidateCount));
        AddSelectedCandidatesCommand.NotifyCanExecuteChanged();
        SelectAllCandidatesCommand.NotifyCanExecuteChanged();
        InvertCandidatesCommand.NotifyCanExecuteChanged();
        SelectRecognizedCandidatesCommand.NotifyCanExecuteChanged();
    }

    private string CatalogSignature() =>
        _catalog is null ? string.Empty : string.Join('|', _catalog.Partitions.Select(p => p.Name));

    private void OnTasksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (FlashTaskViewModel task in e.OldItems)
            {
                task.PropertyChanged -= OnTaskPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (FlashTaskViewModel task in e.NewItems)
            {
                task.PropertyChanged += OnTaskPropertyChanged;
            }
        }

        UpdateSummary();
    }

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FlashTaskViewModel.IsSelected) or nameof(FlashTaskViewModel.Risk))
        {
            UpdateSummary();
        }
    }

    private void OnCandidatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanCandidateViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(SelectedCandidateCount));
            AddSelectedCandidatesCommand.NotifyCanExecuteChanged();
        }
    }

    private void UpdateSummary()
    {
        OnPropertyChanged(
            nameof(SelectedTaskCount), nameof(SelectedBytes), nameof(HasSelectedTasks),
            nameof(HasTasks), nameof(HasCriticalSelected), nameof(SummaryText),
            nameof(HighRiskNotice), nameof(CanStartFlash), nameof(HasDynamicSelected),
            nameof(DynamicHint));
        ClearTasksCommand.NotifyCanExecuteChanged();
    }

    private void RaiseCommandStates()
    {
        RefreshPartitionsCommand.NotifyCanExecuteChanged();
        RebootToFastbootCommand.NotifyCanExecuteChanged();
        RebootToFastbootdCommand.NotifyCanExecuteChanged();
        RebootToSystemCommand.NotifyCanExecuteChanged();
        RescanFolderCommand.NotifyCanExecuteChanged();
        AddSelectedCandidatesCommand.NotifyCanExecuteChanged();
        SelectAllCandidatesCommand.NotifyCanExecuteChanged();
        InvertCandidatesCommand.NotifyCanExecuteChanged();
        SelectRecognizedCandidatesCommand.NotifyCanExecuteChanged();
    }

    private void SetError(Exception ex)
    {
        ErrorMessage = ex.Message;
        _logging.Error(ex.Message, "Flash");
    }

    private void AppendLive(ProcessOutputLine line)
    {
        if (_liveOutput.Length > 120_000)
        {
            _liveOutput.Remove(0, _liveOutput.Length - 60_000);
            _liveOutput.Insert(0, "…（较早输出已截断）" + Environment.NewLine);
        }

        _liveOutput.Append('[').Append(line.Source == ProcessOutputSource.StandardError ? "stderr" : "stdout").Append("] ");
        _liveOutput.AppendLine(line.Text);
        LiveOutputText = _liveOutput.ToString();
    }

    private void ClearLiveOutput()
    {
        _liveOutput.Clear();
        LiveOutputText = string.Empty;
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

    private static string SourceText(PartitionSource source) => source switch
    {
        PartitionSource.FastbootGetVar => "fastboot getvar all",
        PartitionSource.Merged => "ADB + Fastboot 交叉校验",
        _ => "adb ls /dev/block/by-name"
    };

    private sealed record CachedScan(
        string Signature,
        IReadOnlyList<ScanCandidateViewModel> Matched,
        IReadOnlyList<ScanCandidateViewModel> Unmatched);

    private sealed record ScanBuildResult(
        int FileCount,
        IReadOnlyList<ScanCandidateViewModel> Matched,
        IReadOnlyList<ScanCandidateViewModel> Unmatched);
}

