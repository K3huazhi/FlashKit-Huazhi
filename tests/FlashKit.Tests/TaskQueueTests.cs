using FlashKit.Core.Models;
using FlashKit.Services.Tasks;
using Xunit;

namespace FlashKit.Tests;

public sealed class TaskQueueTests
{
    [Fact]
    public async Task Queue_RunsSeriallyAndPersistsHistory()
    {
        using var temp = new TempDirectory();
        var history = Path.Combine(temp.Path, "tasks.json");
        var queue = new TaskQueueService(new NullLogging(), history);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = queue.RunAsync(
            Request("第一项"),
            async _ =>
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
                return 1;
            });
        await firstStarted.Task;

        var secondCompleted = false;
        var second = queue.RunAsync(
            Request("第二项"),
            _ =>
            {
                secondCompleted = true;
                return Task.FromResult(2);
            });

        await Task.Delay(100);
        Assert.False(secondCompleted);
        releaseFirst.SetResult();
        Assert.Equal(1, await first);
        Assert.Equal(2, await second);
        Assert.True(secondCompleted);

        var reloaded = new TaskQueueService(new NullLogging(), history);
        var records = reloaded.Snapshot();
        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal(OperationTaskStatus.Succeeded, r.Status));
    }

    [Fact]
    public async Task QueuedTask_CanBeCancelled()
    {
        using var temp = new TempDirectory();
        var queue = new TaskQueueService(new NullLogging(), Path.Combine(temp.Path, "tasks.json"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.RunAsync(
            Request("占用"),
            async _ =>
            {
                started.SetResult();
                await release.Task;
                return 0;
            });
        await started.Task;

        var second = queue.RunAsync(Request("排队项"), _ => Task.FromResult(0));
        var queued = queue.Snapshot().Single(t => t.Title == "排队项");
        queue.Cancel(queued.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        release.SetResult();
        await first;

        Assert.Equal(OperationTaskStatus.Cancelled, queue.Snapshot().Single(t => t.Title == "排队项").Status);
    }

    private static OperationTaskRequest Request(string title) => new()
    {
        Type = OperationTaskType.ImageConvert,
        Title = title,
        Serial = "SERIAL",
        InputSummary = "input"
    };

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FlashKitTaskTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class NullLogging : FlashKit.Core.Abstractions.ILoggingService
    {
        public IReadOnlyList<LogEntry> Snapshot() => Array.Empty<LogEntry>();
        public event EventHandler<LogEntry>? EntryAdded
        {
            add { }
            remove { }
        }

        public event EventHandler? Cleared
        {
            add { }
            remove { }
        }

        public void Add(LogEntry entry)
        {
        }

        public void Info(string message, string category = "App")
        {
        }

        public void Warn(string message, string category = "App")
        {
        }

        public void Error(string message, string category = "App")
        {
        }

        public void Clear()
        {
        }
    }
}
