using FlashKit.Core.Models;

namespace FlashKit.App.ViewModels;

public sealed class FlashTaskViewModel : ObservableObject
{
    private bool _isSelected = true;
    private FlashItemState _state = FlashItemState.Pending;
    private string _statusText = "待校验";
    private string _validationMessage = string.Empty;
    private int? _exitCode;
    private int _sequence;
    private long? _partitionSizeBytes;
    private PartitionRisk _risk;
    private string _partitionName;
    private bool _isDynamic;

    public FlashTaskViewModel(
        int creationIndex,
        string partitionName,
        string filePath,
        long fileSize,
        SparseImageInfo sparse,
        long? partitionSizeBytes,
        bool isDynamic)
    {
        CreationIndex = creationIndex;
        _partitionName = partitionName;
        FilePath = filePath;
        FileSize = fileSize;
        Sparse = sparse;
        _partitionSizeBytes = partitionSizeBytes;
        _isDynamic = isDynamic;
        _risk = PartitionRules.GetRisk(partitionName);
        SortOrder = PartitionRules.GetRecommendedOrder(partitionName);
        FileName = Path.GetFileName(filePath);
        EffectiveSizeBytes = sparse.ExpandedSizeBytes ?? fileSize;
    }

    public int CreationIndex { get; }
    public string FilePath { get; }
    public string FileName { get; }
    public long FileSize { get; }
    public SparseImageInfo Sparse { get; }
    public long EffectiveSizeBytes { get; }
    public int SortOrder { get; }

    /// <summary>目标分区名可逐条编辑，编辑后的值就是 fastboot flash 的实际目标。</summary>
    public string PartitionName
    {
        get => _partitionName;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (!SetProperty(ref _partitionName, normalized))
            {
                return;
            }

            Risk = PartitionRules.GetRisk(normalized);
            IsDynamic = !string.IsNullOrWhiteSpace(normalized) && PartitionRules.IsDynamic(normalized);
            // 分区名改了以后旧容量提示不再可信，交给下一次可选目录刷新重新填入。
            PartitionSizeBytes = null;
            ValidationMessage = string.Empty;
            State = FlashItemState.Pending;
            StatusText = "待校验";
        }
    }

    public bool IsDynamic
    {
        get => _isDynamic;
        private set
        {
            if (SetProperty(ref _isDynamic, value))
            {
                OnPropertyChanged(nameof(DynamicText));
            }
        }
    }

    public int Sequence
    {
        get => _sequence;
        set => SetProperty(ref _sequence, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public FlashItemState State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationMessage));
            }
        }
    }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public int? ExitCode
    {
        get => _exitCode;
        set
        {
            if (SetProperty(ref _exitCode, value))
            {
                OnPropertyChanged(nameof(ExitCodeText));
            }
        }
    }

    public string ExitCodeText => ExitCode is { } code ? $"退出码 {code}" : string.Empty;

    public PartitionRisk Risk
    {
        get => _risk;
        private set
        {
            if (SetProperty(ref _risk, value))
            {
                OnPropertyChanged(nameof(IsCritical), nameof(RiskText), nameof(CanUse));
            }
        }
    }

    public bool IsCritical => Risk == PartitionRisk.Critical;
    public bool IsDataLoss => Risk == PartitionRisk.DataLoss;
    public bool CanUse => Risk != PartitionRisk.Critical;
    public string RiskText => Risk switch
    {
        PartitionRisk.Critical => "高危",
        PartitionRisk.DataLoss => "清数据",
        _ => string.Empty
    };

    public long? PartitionSizeBytes
    {
        get => _partitionSizeBytes;
        set
        {
            if (SetProperty(ref _partitionSizeBytes, value))
            {
                OnPropertyChanged(nameof(SizeComparisonText));
            }
        }
    }

    public string SizeText => Sparse.IsSparse
        ? $"{PartitionInfo.FormatBytes(FileSize)}（sparse → {PartitionInfo.FormatBytes(EffectiveSizeBytes)}）"
        : PartitionInfo.FormatBytes(FileSize);

    public string SparseText => Sparse.IsSparse ? "sparse" : "raw";

    public string SizeComparisonText => PartitionSizeBytes is > 0
        ? (EffectiveSizeBytes > PartitionSizeBytes.Value
            ? $"超过提示容量 {PartitionInfo.FormatBytes(PartitionSizeBytes.Value)}（仅提示）"
            : $"分区 {PartitionInfo.FormatBytes(PartitionSizeBytes.Value)}")
        : "分区大小未知（仅提示）";

    public string DynamicText => IsDynamic ? "动态分区" : string.Empty;

    public FlashItem ToFlashItem() => new()
    {
        Id = Id,
        PartitionName = PartitionName,
        FilePath = FilePath,
        FileSize = FileSize,
        IsSparse = Sparse.IsSparse,
        ExpandedSizeBytes = Sparse.ExpandedSizeBytes,
        PartitionSizeBytes = PartitionSizeBytes,
        Risk = Risk,
        SortOrder = SortOrder,
        IsSelected = IsSelected
    };

    public Guid Id { get; } = Guid.NewGuid();

    public void ApplyValidation(FlashValidationResult result)
    {
        var blocking = result.Issues.Where(i => i.IsBlocking).ToArray();
        var warnings = result.Issues.Where(i => i.Kind == OperationKind.Warning).ToArray();
        if (blocking.Length > 0)
        {
            State = FlashItemState.Failed;
            StatusText = "校验失败";
            ValidationMessage = string.Join(Environment.NewLine, blocking.Select(i => "✕ " + i.Message)
                .Concat(warnings.Select(i => "! " + i.Message)));
        }
        else
        {
            State = FlashItemState.Ready;
            StatusText = warnings.Length > 0 ? "可刷入（有警告）" : "校验通过";
            ValidationMessage = string.Join(
                Environment.NewLine,
                result.Issues.Where(i => i.Kind != OperationKind.Info).Select(i => "! " + i.Message));
        }
    }

    public void ApplyResult(FlashItemResult result)
    {
        State = result.State;
        ExitCode = result.ExitCode;
        StatusText = result.State switch
        {
            FlashItemState.Succeeded => "成功",
            FlashItemState.Failed => "失败",
            FlashItemState.Skipped => "已跳过",
            FlashItemState.Cancelled => "已取消",
            FlashItemState.Running => "进行中",
            _ => "待刷入"
        };
        if (!string.IsNullOrWhiteSpace(result.Message) && result.State != FlashItemState.Succeeded)
        {
            ValidationMessage = result.Message;
        }
    }
}

public sealed class ScanCandidateViewModel : ObservableObject
{
    private bool _isSelected;
    private string _partitionName;

    public ScanCandidateViewModel(
        string filePath,
        string? matchedPartition,
        IReadOnlyList<string> availablePartitions)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        FileSize = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
        Sparse = File.Exists(filePath)
            ? ImageFormats.Inspect(filePath)
            : new SparseImageInfo(false, 0, 0, 0, 0, 0);
        AvailablePartitions = availablePartitions;
        IsMatched = !string.IsNullOrWhiteSpace(matchedPartition);
        _partitionName = matchedPartition ?? Path.GetFileNameWithoutExtension(filePath);
        Risk = IsMatched ? PartitionRules.GetRisk(_partitionName) : PartitionRisk.Normal;
    }

    public string FilePath { get; }
    public string FileName { get; }
    public long FileSize { get; }
    public SparseImageInfo Sparse { get; }
    public bool IsMatched { get; }
    public IReadOnlyList<string> AvailablePartitions { get; }

    public string PartitionName
    {
        get => _partitionName;
        set
        {
            if (SetProperty(ref _partitionName, value))
            {
                Risk = PartitionRules.GetRisk(value);
                OnPropertyChanged(nameof(IsKnownPartition), nameof(NameStatusText), nameof(RiskText));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsKnownPartition =>
        AvailablePartitions.Any(p => p.Equals(PartitionName, StringComparison.OrdinalIgnoreCase));

    public PartitionRisk Risk { get; private set; }

    public string SizeText => Sparse.IsSparse
        ? $"{PartitionInfo.FormatBytes(FileSize)} → {PartitionInfo.FormatBytes(Sparse.ExpandedSizeBytes ?? FileSize)}"
        : PartitionInfo.FormatBytes(FileSize);

    public string MatchText => IsMatched ? "已匹配" : "未匹配";
    public string NameStatusText => IsKnownPartition ? "分区有效" : "未知分区";
    public string RiskText => Risk switch
    {
        PartitionRisk.Critical => "高危",
        PartitionRisk.DataLoss => "清数据",
        _ => string.Empty
    };
}
