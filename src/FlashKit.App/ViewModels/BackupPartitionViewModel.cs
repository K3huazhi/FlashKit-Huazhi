using FlashKit.Core.Models;

namespace FlashKit.App.ViewModels;

public sealed class BackupPartitionViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isEnabled = true;
    private bool _isCoveredBySuper;

    public BackupPartitionViewModel(PartitionInfo partition)
    {
        Name = partition.Name;
        SizeBytes = partition.SizeBytes;
        Risk = partition.Risk;
        BlockPath = partition.BlockPath;
        Group = partition.Name.Equals("super", StringComparison.OrdinalIgnoreCase)
            ? BackupPartitionGroup.SuperLogical
            : partition.Risk != PartitionRisk.Normal
                ? BackupPartitionGroup.HighRisk
                : partition.IsDynamic
                    ? BackupPartitionGroup.SuperLogical
                    : BackupPartitionGroup.Regular;
        IsSuperContainer = partition.Name.Equals("super", StringComparison.OrdinalIgnoreCase);
    }

    public string Name { get; }
    public long? SizeBytes { get; }
    public PartitionRisk Risk { get; }
    public string? BlockPath { get; }
    public BackupPartitionGroup Group { get; }
    public bool IsSuperContainer { get; }

    public string DisplayName => IsSuperContainer ? "super（整体）" : Name;
    public string SizeText => SizeBytes is > 0 ? PartitionInfo.FormatBytes(SizeBytes.Value) : "大小未知";
    public string RiskText => Risk switch
    {
        PartitionRisk.Critical => "高风险",
        PartitionRisk.DataLoss => "清数据",
        _ => string.Empty
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!IsEnabled && value)
            {
                return;
            }

            SetProperty(ref _isSelected, value);
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        private set => SetProperty(ref _isEnabled, value);
    }

    public bool IsCoveredBySuper
    {
        get => _isCoveredBySuper;
        private set
        {
            if (SetProperty(ref _isCoveredBySuper, value))
            {
                OnPropertyChanged(nameof(StatusText), nameof(IsStatusVisible));
            }
        }
    }

    public string StatusText => IsCoveredBySuper ? "已包含于 super 备份" : string.Empty;
    public bool IsStatusVisible => IsCoveredBySuper;

    public BackupPartitionSelection ToSelection() =>
        new(Name, SizeBytes, Risk, Group, IsSuperContainer, BlockPath);

    /// <summary>super 被勾选时，同组逻辑分区不再单独备份，也不允许重复勾选。</summary>
    public void SetCoveredBySuper(bool covered)
    {
        IsCoveredBySuper = covered;
        IsEnabled = !covered;
        if (covered)
        {
            IsSelected = false;
        }
    }
}
