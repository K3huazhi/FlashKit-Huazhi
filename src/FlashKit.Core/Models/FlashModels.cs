namespace FlashKit.Core.Models;

public sealed class FlashItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string PartitionName { get; init; }
    public required string FilePath { get; init; }
    public long FileSize { get; init; }
    public bool IsSparse { get; init; }
    public long? ExpandedSizeBytes { get; init; }
    public long? PartitionSizeBytes { get; init; }
    public PartitionRisk Risk { get; init; }
    public int SortOrder { get; init; }
    public bool IsSelected { get; set; } = true;
}

public sealed record FlashValidationIssue(string Message, OperationKind Kind)
{
    public bool IsBlocking => Kind == OperationKind.Error;
}

public sealed class FlashValidationResult
{
    public required FlashItem Item { get; init; }
    public bool IsValid { get; init; }
    public IReadOnlyList<FlashValidationIssue> Issues { get; init; } = Array.Empty<FlashValidationIssue>();
}

public sealed class FlashRunOptions
{
    public required string Serial { get; init; }
    public bool ContinueOnFailure { get; init; }
    public string? ExpectedCodename { get; init; }
    public bool VerifyDeviceCodename { get; init; } = true;
    public bool AllowCriticalPartitions { get; init; }
    public bool AllowUnknownPartitionSizes { get; init; }
}

public sealed record FlashItemResult(
    FlashItem Item,
    FlashItemState State,
    int? ExitCode,
    string? Message,
    TimeSpan Duration);

public sealed class FlashRunSummary
{
    public required string Serial { get; init; }
    public required IReadOnlyList<FlashItemResult> Results { get; init; }
    public bool Cancelled { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }

    public int SuccessCount => Results.Count(r => r.State == FlashItemState.Succeeded);
    public int FailureCount => Results.Count(r => r.State == FlashItemState.Failed);
    public int SkippedCount => Results.Count(r => r.State is FlashItemState.Skipped or FlashItemState.Cancelled);
}
