using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using FlashKit.Core.Parsing;
using Microsoft.UI.Dispatching;

namespace FlashKit.App.ViewModels;

public sealed class PackImageViewModel : ObservableObject
{
    private IReadOnlyList<string> _knownPartitions;
    private string _partitionName;

    public PackImageViewModel(string filePath, IReadOnlyList<string> knownPartitions)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        FileSize = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
        _knownPartitions = knownPartitions;
        var inferred = PartitionMatcher.InferPartitionName(filePath, knownPartitions);
        _partitionName = inferred ?? Path.GetFileNameWithoutExtension(filePath);
        AutoInferred = inferred is not null;
    }

    public string FilePath { get; }
    public string FileName { get; }
    public long FileSize { get; }
    public bool AutoInferred { get; }
    public string SizeText => PartitionInfo.FormatBytes(FileSize);

    public string PartitionName
    {
        get => _partitionName;
        set
        {
            if (SetProperty(ref _partitionName, value))
            {
                OnPropertyChanged(nameof(IsVerified), nameof(StatusText), nameof(StatusDetail));
            }
        }
    }

    public bool IsVerified =>
        _knownPartitions.Any(p => p.Equals(PartitionName?.Trim(), StringComparison.OrdinalIgnoreCase));

    public string StatusText => _knownPartitions.Count == 0
        ? "名称未核验"
        : IsVerified ? "已匹配" : "未知分区";

    public string StatusDetail => _knownPartitions.Count == 0
        ? "连接设备后会自动核验分区名；当前仍可手动打包。"
        : IsVerified ? "将从 ZIP 根级写入该分区。" : "请手动修改为设备真实分区名，或确认这是自定义分区。";

    public void UpdateKnownPartitions(IReadOnlyList<string> knownPartitions)
    {
        _knownPartitions = knownPartitions;
        OnPropertyChanged(nameof(IsVerified), nameof(StatusText), nameof(StatusDetail));
    }

    public PackImageInput ToInput() => new()
    {
        FilePath = FilePath,
        PartitionName = PartitionName?.Trim() ?? string.Empty
    };
}

public sealed class PackageViewModel : ObservableObject
{
    private readonly IPackageExtractor _extractor;
    private readonly IPackagePacker _packer;
    private readonly ITaskQueueService _taskQueue;
    private readonly IProcessRunner _processRunner;
    private readonly ILoggingService _logging;
    private readonly IDeviceManager _deviceManager;
    private readonly IPartitionCatalogService _catalogService;
    private readonly DispatcherQueue? _dispatcher;
    private readonly List<string> _knownPartitions = new();
    private CancellationTokenSource? _extractCts;
    private CancellationTokenSource? _packCts;
    private string _packagePath = string.Empty;
    private string _extractOutputDirectory = string.Empty;
    private string _extractStatus = "尚未解包";
    private string _extractProgressText = string.Empty;
    private string _extractWarnings = string.Empty;
    private string _incrementalWarning = string.Empty;
    private string _errorMessage = string.Empty;
    private string _knownPartitionsHint = "未读取设备分区；镜像名称会标黄，可手动修改。";
    private string _packOutputPath = string.Empty;
    private string _packStatus = "尚未打包";
    private string _packValidationText = string.Empty;
    private string _packWarningsText = string.Empty;
    private string _packResultText = string.Empty;
    private double _extractProgress;
    private double _packProgress;
    private bool _isExtracting;
    private bool _isPacking;
    private bool _packValidationValid;

    public PackageViewModel(
        IPackageExtractor extractor,
        IPackagePacker packer,
        ITaskQueueService taskQueue,
        IProcessRunner processRunner,
        ILoggingService logging,
        IDeviceManager deviceManager,
        IPartitionCatalogService catalogService)
    {
        _extractor = extractor;
        _packer = packer;
        _taskQueue = taskQueue;
        _processRunner = processRunner;
        _logging = logging;
        _deviceManager = deviceManager;
        _catalogService = catalogService;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        Images.CollectionChanged += OnImagesChanged;
        _deviceManager.DevicesChanged += (_, _) => RunOnUi(() => { _ = RefreshKnownPartitionsAsync(false); });

        CancelExtractCommand = new RelayCommand(CancelExtract, () => IsExtracting);
        CancelPackCommand = new RelayCommand(CancelPack, () => IsPacking);
        OpenExtractOutputCommand = new AsyncRelayCommand(
            OpenExtractOutputAsync,
            () => !string.IsNullOrWhiteSpace(ExtractOutputDirectory) && Directory.Exists(ExtractOutputDirectory),
            ex => SetError(ex));
        OpenPackOutputCommand = new AsyncRelayCommand(
            OpenPackOutputAsync,
            () => !string.IsNullOrWhiteSpace(PackOutputPath),
            ex => SetError(ex));
        ClearImagesCommand = new RelayCommand(ClearImages, () => !IsPacking && Images.Count > 0);
        RefreshKnownPartitionsCommand = new AsyncRelayCommand(
            () => RefreshKnownPartitionsAsync(true),
            () => !IsBusy,
            ex => SetError(ex));
    }

    public ObservableCollection<PackImageViewModel> Images { get; } = new();

    public string PackagePath
    {
        get => _packagePath;
        private set
        {
            if (SetProperty(ref _packagePath, value))
            {
                OnPropertyChanged(nameof(ExtractOutputPreview), nameof(CanExtract));
            }
        }
    }

    public string ExtractOutputPreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PackagePath))
            {
                return "选择 ZIP 后自动创建同名目录（images/、META-INF/、manifest.json）";
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(PackagePath)) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(PackagePath);
            return Path.Combine(directory, name);
        }
    }

    public string ExtractOutputDirectory
    {
        get => _extractOutputDirectory;
        private set
        {
            if (SetProperty(ref _extractOutputDirectory, value))
            {
                OpenExtractOutputCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ExtractStatus
    {
        get => _extractStatus;
        private set => SetProperty(ref _extractStatus, value);
    }

    public string ExtractProgressText
    {
        get => _extractProgressText;
        private set => SetProperty(ref _extractProgressText, value);
    }

    public string ExtractWarnings
    {
        get => _extractWarnings;
        private set
        {
            if (SetProperty(ref _extractWarnings, value))
            {
                OnPropertyChanged(nameof(HasExtractWarnings));
            }
        }
    }

    public bool HasExtractWarnings => !string.IsNullOrWhiteSpace(ExtractWarnings);

    public string IncrementalWarning
    {
        get => _incrementalWarning;
        private set
        {
            if (SetProperty(ref _incrementalWarning, value))
            {
                OnPropertyChanged(nameof(HasIncrementalWarning));
            }
        }
    }

    public bool HasIncrementalWarning => !string.IsNullOrWhiteSpace(IncrementalWarning);

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

    public string KnownPartitionsHint
    {
        get => _knownPartitionsHint;
        private set => SetProperty(ref _knownPartitionsHint, value);
    }

    public string PackOutputPath
    {
        get => _packOutputPath;
        set
        {
            if (SetProperty(ref _packOutputPath, value))
            {
                OpenPackOutputCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanPack));
            }
        }
    }

    public string PackStatus
    {
        get => _packStatus;
        private set => SetProperty(ref _packStatus, value);
    }

    public string PackValidationText
    {
        get => _packValidationText;
        private set
        {
            if (SetProperty(ref _packValidationText, value))
            {
                OnPropertyChanged(nameof(HasPackValidationError));
            }
        }
    }

    public bool HasPackValidationError => !string.IsNullOrWhiteSpace(PackValidationText);

    public string PackWarningsText
    {
        get => _packWarningsText;
        private set
        {
            if (SetProperty(ref _packWarningsText, value))
            {
                OnPropertyChanged(nameof(HasPackWarnings));
            }
        }
    }

    public bool HasPackWarnings => !string.IsNullOrWhiteSpace(PackWarningsText);

    public string PackResultText
    {
        get => _packResultText;
        private set => SetProperty(ref _packResultText, value);
    }

    public double ExtractProgress
    {
        get => _extractProgress;
        private set => SetProperty(ref _extractProgress, value);
    }

    public double PackProgress
    {
        get => _packProgress;
        private set => SetProperty(ref _packProgress, value);
    }

    public bool IsExtracting
    {
        get => _isExtracting;
        private set
        {
            if (SetProperty(ref _isExtracting, value))
            {
                OnPropertyChanged(nameof(IsBusy), nameof(IsNotBusy), nameof(CanPack), nameof(CanExtract));
                CancelExtractCommand.NotifyCanExecuteChanged();
                RefreshKnownPartitionsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsPacking
    {
        get => _isPacking;
        private set
        {
            if (SetProperty(ref _isPacking, value))
            {
                OnPropertyChanged(nameof(IsBusy), nameof(IsNotBusy), nameof(CanPack), nameof(CanExtract));
                CancelPackCommand.NotifyCanExecuteChanged();
                ClearImagesCommand.NotifyCanExecuteChanged();
                RefreshKnownPartitionsCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsBusy => IsExtracting || IsPacking;
    public bool IsNotBusy => !IsBusy;

    public string PackSummary => $"{Images.Count} 张镜像，约 {PartitionInfo.FormatBytes(Images.Sum(i => i.FileSize))}";

    public bool CanPack => !IsBusy && _packValidationValid && Images.Count > 0 && !string.IsNullOrWhiteSpace(PackOutputPath);
    public bool CanExtract => !IsBusy && !string.IsNullOrWhiteSpace(PackagePath) && File.Exists(PackagePath);

    public RelayCommand CancelExtractCommand { get; }
    public RelayCommand CancelPackCommand { get; }
    public AsyncRelayCommand OpenExtractOutputCommand { get; }
    public AsyncRelayCommand OpenPackOutputCommand { get; }
    public RelayCommand ClearImagesCommand { get; }
    public AsyncRelayCommand RefreshKnownPartitionsCommand { get; }

    public async Task ActivateAsync()
    {
        if (_knownPartitions.Count == 0)
        {
            await RefreshKnownPartitionsAsync(force: false).ConfigureAwait(true);
        }
    }

    public void SetPackagePath(string path)
    {
        PackagePath = path;
        ExtractOutputDirectory = string.Empty;
        ExtractWarnings = string.Empty;
        IncrementalWarning = string.Empty;
        ExtractProgress = 0;
        ExtractStatus = "待解包";
        ExtractProgressText = string.Empty;
        ErrorMessage = string.Empty;
    }

    public void AddImages(IEnumerable<string> paths)
    {
        var known = _knownPartitions.ToArray();
        var existing = Images.Select(i => i.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !Path.GetExtension(path).Equals(".img", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!existing.Add(path))
            {
                continue;
            }

            var image = new PackImageViewModel(path, known);
            Images.Add(image);
            added++;
        }

        PackStatus = added > 0 ? $"已加入 {added} 张镜像。" : "没有新增 .img 文件。";
        RefreshPackValidation();
    }

    public void RemoveImage(PackImageViewModel image)
    {
        if (IsPacking)
        {
            return;
        }

        Images.Remove(image);
        RefreshPackValidation();
    }

    public async Task StartExtractAsync(bool overwriteExisting = false)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(PackagePath))
        {
            return;
        }

        if (!File.Exists(PackagePath))
        {
            SetError(new FileNotFoundException("卡刷包文件不存在。", PackagePath));
            return;
        }

        if (Directory.Exists(ExtractOutputPreview) &&
            Directory.EnumerateFileSystemEntries(ExtractOutputPreview).Any())
        {
            if (!overwriteExisting)
            {
                SetError(new IOException("输出目录已存在且非空，请选择覆盖或先移动旧目录。"));
                return;
            }

            try
            {
                Directory.Delete(ExtractOutputPreview, recursive: true);
            }
            catch (Exception ex)
            {
                SetError(new IOException($"无法清理旧输出目录：{ex.Message}", ex));
                return;
            }
        }

        IsExtracting = true;
        _extractCts = new CancellationTokenSource();
        ErrorMessage = string.Empty;
        IncrementalWarning = string.Empty;
        ExtractWarnings = string.Empty;
        ExtractOutputDirectory = string.Empty;
        ExtractProgress = 0;
        ExtractStatus = "正在解包…";
        ExtractProgressText = string.Empty;

        try
        {
            TaskExecutionContext? queueContext = null;
            var progress = new Progress<OperationProgress>(p =>
            {
                ExtractProgress = p.Percentage;
                ExtractProgressText = string.IsNullOrWhiteSpace(p.Status) ? string.Empty : p.Status;
                queueContext?.Report(p.Percentage, p.Status);
            });
            var extractPath = PackagePath;
            var result = await _taskQueue.RunAsync(
                new OperationTaskRequest
                {
                    Type = OperationTaskType.PackageExtract,
                    Title = $"解包 {Path.GetFileName(extractPath)}",
                    InputSummary = extractPath,
                    OutputPath = ExtractOutputPreview
                },
                async context =>
                {
                    queueContext = context;
                    return await _extractor.ExtractAsync(
                        extractPath,
                        progress,
                        line => RunOnUi(() => ExtractProgressText = line),
                        context.CancellationToken).ConfigureAwait(false);
                },
                _extractCts.Token).ConfigureAwait(true);

            ExtractOutputDirectory = result.OutputDirectory;
            ExtractWarnings = string.Join(Environment.NewLine, result.Warnings);
            ExtractStatus = $"完成：{result.ImageCount} 个镜像";
            ExtractProgress = 100;
            _logging.Info($"解包完成：{result.OutputDirectory}（{result.ImageCount} 个镜像）", "Package");
        }
        catch (IncrementalOtaException ex)
        {
            IncrementalWarning = ex.Message;
            ExtractStatus = "检测到增量 OTA，已停止";
            _logging.Warn(ex.Message, "Package");
        }
        catch (OperationCanceledException)
        {
            ExtractStatus = "已取消";
        }
        catch (Exception ex)
        {
            SetError(ex);
            ExtractStatus = "解包失败";
        }
        finally
        {
            IsExtracting = false;
            _extractCts.Dispose();
            _extractCts = null;
        }
    }

    public async Task StartPackAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var validation = _packer.Validate(Images.Select(i => i.ToInput()).ToArray());
        if (!validation.IsValid)
        {
            PackValidationText = string.Join(Environment.NewLine, validation.Errors);
            return;
        }

        if (string.IsNullOrWhiteSpace(PackOutputPath))
        {
            SetError(new InvalidOperationException("请选择 ZIP 输出路径。"));
            return;
        }

        IsPacking = true;
        _packCts = new CancellationTokenSource();
        ErrorMessage = string.Empty;
        PackProgress = 0;
        PackStatus = "正在打包…";
        PackResultText = string.Empty;

        try
        {
            TaskExecutionContext? queueContext = null;
            var progress = new Progress<OperationProgress>(p =>
            {
                PackProgress = p.Percentage;
                PackStatus = string.IsNullOrWhiteSpace(p.Status) ? "正在打包…" : p.Status;
                queueContext?.Report(p.Percentage, p.Status);
            });
            var inputs = Images.Select(i => i.ToInput()).ToArray();
            var outputPath = PackOutputPath;
            var result = await _taskQueue.RunAsync(
                new OperationTaskRequest
                {
                    Type = OperationTaskType.PackagePack,
                    Title = $"打包 {Path.GetFileName(outputPath)}",
                    InputSummary = string.Join(", ", inputs.Select(i => i.PartitionName)),
                    OutputPath = outputPath
                },
                async context =>
                {
                    queueContext = context;
                    return await _packer.PackAsync(
                        inputs,
                        outputPath,
                        progress,
                        line => RunOnUi(() => PackStatus = line),
                        context.CancellationToken).ConfigureAwait(false);
                },
                _packCts.Token).ConfigureAwait(true);

            PackProgress = 100;
            PackStatus = "打包完成";
            PackResultText = $"输出：{result.OutputPath}\n大小：{PartitionInfo.FormatBytes(result.SizeBytes)}，镜像 {result.ImageCount} 个";
            _logging.Info($"打包完成：{result.OutputPath}", "Package");
        }
        catch (OperationCanceledException)
        {
            PackStatus = "已取消";
        }
        catch (Exception ex)
        {
            SetError(ex);
            PackStatus = "打包失败";
        }
        finally
        {
            IsPacking = false;
            _packCts.Dispose();
            _packCts = null;
        }
    }

    private async Task RefreshKnownPartitionsAsync(bool force)
    {
        var device = _deviceManager.SelectedDevice;
        if (device is null)
        {
            _knownPartitions.Clear();
            KnownPartitionsHint = "未选择设备：镜像名称会标黄，仍可手动修改后打包。";
            UpdateImageKnownPartitions();
            return;
        }

        if (device.Mode is not (DeviceMode.System or DeviceMode.Fastboot))
        {
            _knownPartitions.Clear();
            KnownPartitionsHint = "当前模式无法读取分区表：镜像名称会标黄，可手动修改。";
            UpdateImageKnownPartitions();
            return;
        }

        try
        {
            var catalog = await _catalogService.GetAsync(device.Serial, device.Mode, force).ConfigureAwait(true);
            _knownPartitions.Clear();
            _knownPartitions.AddRange(catalog.Partitions.Select(p => p.Name));
            KnownPartitionsHint = _knownPartitions.Count == 0
                ? "未能读取设备分区，名称核验不可用。"
                : $"已从 {device.Mode.ToChinese()} 模式读取 {_knownPartitions.Count} 个分区，用于核验镜像名。";
            UpdateImageKnownPartitions();
        }
        catch (Exception ex)
        {
            KnownPartitionsHint = $"分区读取失败：{ex.Message}";
        }
    }

    private void UpdateImageKnownPartitions()
    {
        var known = _knownPartitions.ToArray();
        foreach (var image in Images)
        {
            image.UpdateKnownPartitions(known);
        }
    }

    private void RefreshPackValidation()
    {
        if (Images.Count == 0)
        {
            PackValidationText = string.Empty;
            PackWarningsText = string.Empty;
            _packValidationValid = false;
        }
        else
        {
            var result = _packer.Validate(Images.Select(i => i.ToInput()).ToArray());
            PackValidationText = string.Join(Environment.NewLine, result.Errors);
            PackWarningsText = string.Join(Environment.NewLine, result.Warnings);
            _packValidationValid = result.IsValid;
        }

        OnPropertyChanged(nameof(PackSummary), nameof(CanPack));
        ClearImagesCommand.NotifyCanExecuteChanged();
    }

    private void OnImagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (PackImageViewModel image in e.NewItems)
            {
                image.PropertyChanged -= OnImagePropertyChanged;
                image.PropertyChanged += OnImagePropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (PackImageViewModel image in e.OldItems)
            {
                image.PropertyChanged -= OnImagePropertyChanged;
            }
        }

        RefreshPackValidation();
    }

    private void OnImagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackImageViewModel.PartitionName))
        {
            RefreshPackValidation();
        }
    }

    private void ClearImages()
    {
        Images.Clear();
        PackResultText = string.Empty;
        RefreshPackValidation();
    }

    private void CancelExtract() => _extractCts?.Cancel();
    private void CancelPack() => _packCts?.Cancel();

    private async Task OpenExtractOutputAsync()
    {
        if (Directory.Exists(ExtractOutputDirectory))
        {
            await _processRunner.OpenFolderAsync(ExtractOutputDirectory);
        }
    }

    private async Task OpenPackOutputAsync()
    {
        var directory = Path.GetDirectoryName(PackOutputPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            await _processRunner.OpenFolderAsync(directory);
        }
    }

    private void SetError(Exception ex)
    {
        ErrorMessage = ex.Message;
        _logging.Error(ex.Message, "Package");
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
