using System.Text.RegularExpressions;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;

namespace FlashKit.Services.Flash;

/// <summary>
/// 刷入前校验。按分区名直接刷入时，分区表/getvar 仅作为可选提示：
/// 不在目录、容量未知、容量不足、动态分区、代号不一致都只产生 warning，不阻断执行。
/// 真正阻断的只有镜像文件本身不可用、目标分区名为空或含非法字符。
/// </summary>
public sealed partial class FlashValidationService : IFlashValidationService
{
    public async Task<FlashValidationResult> ValidateAsync(
        FlashItem item,
        PartitionCatalog catalog,
        FlashRunOptions options,
        bool fastbootUserspace,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<FlashValidationIssue>();
        var partition = catalog.Partitions.FirstOrDefault(p =>
            p.Name.Equals(item.PartitionName, StringComparison.OrdinalIgnoreCase));

        var targetName = item.PartitionName?.Trim() ?? string.Empty;
        if (targetName.Length == 0)
        {
            issues.Add(new FlashValidationIssue("目标分区名为空。", OperationKind.Error));
        }
        else if (!PartitionNameRegex().IsMatch(targetName) || targetName is "." or "..")
        {
            issues.Add(new FlashValidationIssue(
                $"目标分区名 \"{targetName}\" 含非法字符；只允许字母、数字、点、下划线和短横线。",
                OperationKind.Error));
        }

        if (partition is null && targetName.Length > 0)
        {
            issues.Add(new FlashValidationIssue(
                $"分区 {targetName} 不在当前可选分区目录中；仍会按名称直接执行 fastboot flash。",
                OperationKind.Warning));
        }

        if (!File.Exists(item.FilePath))
        {
            issues.Add(new FlashValidationIssue("镜像文件不存在。", OperationKind.Error));
        }
        else
        {
            var fileInfo = new FileInfo(item.FilePath);
            if (fileInfo.Length == 0)
            {
                issues.Add(new FlashValidationIssue("镜像文件为空（0 字节）。", OperationKind.Error));
            }

            try
            {
                await using var stream = new FileStream(
                    item.FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 1,
                    useAsync: true);
                if (!stream.CanRead)
                {
                    issues.Add(new FlashValidationIssue("镜像文件不可读。", OperationKind.Error));
                }
            }
            catch (Exception ex)
            {
                issues.Add(new FlashValidationIssue($"镜像文件无法打开：{ex.Message}", OperationKind.Error));
            }

            if (item.FileSize <= 0)
            {
                issues.Add(new FlashValidationIssue("镜像文件大小读取失败。", OperationKind.Error));
            }
        }

        if (item.IsSparse)
        {
            var sparse = ImageFormats.Inspect(item.FilePath);
            if (!sparse.IsSparse)
            {
                issues.Add(new FlashValidationIssue("文件具有 sparse 魔数，但头部无法解析；请重新导出镜像。", OperationKind.Error));
            }
            else if (sparse.ExpandedSizeBytes is null or <= 0)
            {
                issues.Add(new FlashValidationIssue("sparse 镜像的展开大小无效。", OperationKind.Error));
            }
            else
            {
                issues.Add(new FlashValidationIssue(
                    $"已识别 Android sparse 镜像，展开后约 {PartitionInfo.FormatBytes(sparse.ExpandedSizeBytes.Value)}。",
                    OperationKind.Info));
            }
        }

        var effectiveSize = item.ExpandedSizeBytes ?? item.FileSize;
        if (partition is not null)
        {
            if (partition.SizeBytes is null or <= 0)
            {
                issues.Add(new FlashValidationIssue(
                    $"分区 {partition.Name} 的容量未知；仅作提示，不阻断刷入。",
                    OperationKind.Warning));
            }
            else if (effectiveSize > partition.SizeBytes.Value)
            {
                issues.Add(new FlashValidationIssue(
                    $"镜像大小 {PartitionInfo.FormatBytes(effectiveSize)} 超过目录中的分区容量 " +
                    $"{PartitionInfo.FormatBytes(partition.SizeBytes.Value)}；仅提示，不阻断。",
                    OperationKind.Warning));
            }
            else if (effectiveSize > 0)
            {
                issues.Add(new FlashValidationIssue(
                    $"分区目录提示：{PartitionInfo.FormatBytes(effectiveSize)} / {PartitionInfo.FormatBytes(partition.SizeBytes.Value)}。",
                    OperationKind.Info));
            }
        }

        if (PartitionRules.IsDynamic(targetName) && !fastbootUserspace)
        {
            issues.Add(new FlashValidationIssue(
                "目标看起来是动态分区；如果当前是 bootloader，fastboot flash 可能失败，可考虑进入 Fastbootd。",
                OperationKind.Warning));
        }

        if (PartitionRules.StripSlotSuffix(targetName).Equals("super", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new FlashValidationIssue(
                "目标是 super 容器；请确认镜像类型与当前 Fastboot/Fastbootd 环境。",
                OperationKind.Warning));
        }

        if (item.Risk == PartitionRisk.Critical)
        {
            issues.Add(new FlashValidationIssue(
                "高危分区：刷错可能导致设备无法开机或基带丢失，需要二次确认。",
                OperationKind.Warning));
        }
        else if (item.Risk == PartitionRisk.DataLoss)
        {
            issues.Add(new FlashValidationIssue("该分区包含用户数据，刷入会清空数据。", OperationKind.Warning));
        }

        if (options.VerifyDeviceCodename)
        {
            if (string.IsNullOrWhiteSpace(options.ExpectedCodename))
            {
                issues.Add(new FlashValidationIssue("未填写期望设备代号，已跳过可选的设备代号核对。", OperationKind.Warning));
            }
            else if (!string.IsNullOrWhiteSpace(catalog.DeviceCodename) &&
                     !catalog.DeviceCodename.Equals(options.ExpectedCodename.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new FlashValidationIssue(
                    $"设备代号提示不一致：设备为 {catalog.DeviceCodename}，期望 {options.ExpectedCodename.Trim()}；不阻断执行。",
                    OperationKind.Warning));
            }
            else if (string.IsNullOrWhiteSpace(catalog.DeviceCodename))
            {
                issues.Add(new FlashValidationIssue("无法读取设备代号，仅作提示。", OperationKind.Warning));
            }
        }

        return new FlashValidationResult
        {
            Item = item,
            IsValid = issues.All(i => !i.IsBlocking),
            Issues = issues
        };
    }

    public async Task<IReadOnlyList<FlashValidationResult>> ValidateAllAsync(
        IReadOnlyList<FlashItem> items,
        PartitionCatalog catalog,
        FlashRunOptions options,
        bool fastbootUserspace,
        CancellationToken cancellationToken = default)
    {
        var results = new List<FlashValidationResult>(items.Count);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ValidateAsync(item, catalog, options, fastbootUserspace, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex PartitionNameRegex();
}
