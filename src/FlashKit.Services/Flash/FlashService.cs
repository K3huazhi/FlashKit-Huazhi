using System.Diagnostics;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;

namespace FlashKit.Services.Flash;

public sealed class FlashService : IFlashService
{
    private readonly IFastbootClient _fastboot;
    private readonly ILoggingService _logging;

    public FlashService(IFastbootClient fastboot, ILoggingService logging)
    {
        _fastboot = fastboot;
        _logging = logging;
    }

    public async Task<FlashRunSummary> ExecuteAsync(
        FlashRunOptions options,
        IReadOnlyList<FlashItem> items,
        IProgress<FlashProgress>? progress = null,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var selected = items.Where(i => i.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidOperationException("待刷任务清单为空。");
        }

        var duplicate = selected
            .GroupBy(i => PartitionRules.StripSlotSuffix(i.PartitionName), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"分区 {duplicate.Key} 在清单中重复，请移除重复项后再刷入。");
        }

        var devices = await _fastboot.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (!devices.Any(d => d.Serial.Equals(options.Serial, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Fastboot 设备 {options.Serial} 当前不可见。请确认设备处于 Fastboot/Fastbootd 模式且驱动正常。");
        }

        var startedAt = DateTimeOffset.Now;
        var results = new List<FlashItemResult>(selected.Length);
        var cancelled = false;

        for (var index = 0; index < selected.Length; index++)
        {
            var item = selected[index];
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                results.Add(new FlashItemResult(item, FlashItemState.Cancelled, null, "已取消", TimeSpan.Zero));
                continue;
            }

            progress?.Report(new FlashProgress
            {
                CurrentIndex = index + 1,
                TotalCount = selected.Length,
                PartitionName = item.PartitionName,
                FileName = Path.GetFileName(item.FilePath),
                Percentage = index * 100.0 / selected.Length,
                Status = "正在发送镜像…"
            });

            var stopwatch = Stopwatch.StartNew();
            var lastReported = 0.0;
            void HandleLine(ProcessOutputLine line)
            {
                onOutput?.Invoke(line);
                var percent = line.Text.Contains("Writing", StringComparison.OrdinalIgnoreCase)
                    ? 80.0
                    : line.Text.Contains("Sending", StringComparison.OrdinalIgnoreCase)
                        ? 35.0
                        : 0.0;
                if (percent > lastReported)
                {
                    lastReported = percent;
                    progress?.Report(new FlashProgress
                    {
                        CurrentIndex = index + 1,
                        TotalCount = selected.Length,
                        PartitionName = item.PartitionName,
                        FileName = Path.GetFileName(item.FilePath),
                        Percentage = (index + percent / 100.0) * 100.0 / selected.Length,
                        Status = percent >= 80 ? "正在写入分区…" : "正在发送镜像…"
                    });
                }
            }

            ProcessResult result;
            try
            {
                result = await _fastboot.FlashAsync(
                    options.Serial,
                    item.PartitionName,
                    item.FilePath,
                    HandleLine,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new ProcessResult
                {
                    ExecutablePath = "fastboot.exe",
                    Arguments = ["flash", item.PartitionName, item.FilePath],
                    ExitCode = -1,
                    Cancelled = true,
                    Duration = stopwatch.Elapsed
                };
            }

            stopwatch.Stop();
            if (result.Cancelled || cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                results.Add(new FlashItemResult(item, FlashItemState.Cancelled, result.ExitCode, "用户取消", stopwatch.Elapsed));
                progress?.Report(new FlashProgress
                {
                    CurrentIndex = index + 1,
                    TotalCount = selected.Length,
                    PartitionName = item.PartitionName,
                    FileName = Path.GetFileName(item.FilePath),
                    Percentage = (index + 1) * 100.0 / selected.Length,
                    Status = "已取消"
                });
                continue;
            }

            if (result.IsSuccess)
            {
                results.Add(new FlashItemResult(item, FlashItemState.Succeeded, result.ExitCode, "刷入成功", stopwatch.Elapsed));
                progress?.Report(new FlashProgress
                {
                    CurrentIndex = index + 1,
                    TotalCount = selected.Length,
                    PartitionName = item.PartitionName,
                    FileName = Path.GetFileName(item.FilePath),
                    Percentage = (index + 1) * 100.0 / selected.Length,
                    Status = "刷入成功"
                });
                continue;
            }

            var message = result.ToReadableError();
            results.Add(new FlashItemResult(item, FlashItemState.Failed, result.ExitCode, message, stopwatch.Elapsed));
            _logging.Error($"刷入 {item.PartitionName} 失败：{message}", "Flash");
            progress?.Report(new FlashProgress
            {
                CurrentIndex = index + 1,
                TotalCount = selected.Length,
                PartitionName = item.PartitionName,
                FileName = Path.GetFileName(item.FilePath),
                Percentage = (index + 1) * 100.0 / selected.Length,
                Status = "刷入失败"
            });

            if (!options.ContinueOnFailure)
            {
                foreach (var remaining in selected.Skip(index + 1))
                {
                    results.Add(new FlashItemResult(remaining, FlashItemState.Skipped, null, "前序任务失败，已中止", TimeSpan.Zero));
                }

                break;
            }
        }

        var summary = new FlashRunSummary
        {
            Serial = options.Serial,
            Results = results,
            Cancelled = cancelled,
            StartedAt = startedAt,
            FinishedAt = DateTimeOffset.Now
        };
        _logging.Info(
            $"刷入结束：成功 {summary.SuccessCount}，失败 {summary.FailureCount}，跳过 {summary.SkippedCount}" +
            (summary.Cancelled ? "（已取消）" : string.Empty),
            "Flash");
        return summary;
    }
}
