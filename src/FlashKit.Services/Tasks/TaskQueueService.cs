using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;

namespace FlashKit.Services.Tasks;

/// <summary>
/// 串行后台任务队列。所有危险/重 IO 操作都通过这里执行并持久化历史，
/// 同一时间只允许一个任务运行（对同一设备是更强保证）。
/// </summary>
public sealed class TaskQueueService : ITaskQueueService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILoggingService _logging;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly List<OperationTaskRecord> _tasks = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _cancelSources = new();
    private readonly object _saveLock = new();
    private readonly string _historyPath;
    private int _historyDirty;

    public TaskQueueService(ILoggingService logging, string? historyPath = null)
    {
        _logging = logging;
        if (string.IsNullOrWhiteSpace(historyPath))
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FlashKit");
            Directory.CreateDirectory(directory);
            _historyPath = Path.Combine(directory, "tasks.json");
        }
        else
        {
            _historyPath = historyPath;
            Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
        }

        Load();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<OperationTaskRecord> Snapshot()
    {
        lock (_sync)
        {
            return _tasks.ToArray();
        }
    }

    public async Task<T> RunAsync<T>(
        OperationTaskRequest request,
        Func<TaskExecutionContext, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        var record = new OperationTaskRecord
        {
            Id = Guid.NewGuid(),
            Type = request.Type,
            Title = request.Title,
            Serial = request.Serial,
            InputSummary = request.InputSummary,
            OutputPath = request.OutputPath,
            LogReference = $"task-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            CreatedAt = DateTimeOffset.Now,
            Status = OperationTaskStatus.Queued,
            ProgressStatus = "排队中"
        };

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_sync)
        {
            _tasks.Insert(0, record);
            TrimHistory();
            _cancelSources[record.Id] = cts;
        }

        RaiseChanged();
        Save();

        var acquired = false;
        var startedAt = DateTimeOffset.Now;
        try
        {
            await _gate.WaitAsync(cts.Token).ConfigureAwait(false);
            acquired = true;
            if (cts.IsCancellationRequested)
            {
                throw new OperationCanceledException(cts.Token);
            }

            record.Status = OperationTaskStatus.Running;
            record.StartedAt = DateTimeOffset.Now;
            startedAt = record.StartedAt.Value;
            record.ProgressStatus = "开始执行";
            RaiseChanged();
            Save();

            var context = new TaskExecutionContext(
                record,
                cts.Token,
                progress => UpdateProgress(record, progress),
                path =>
                {
                    record.OutputPath = path;
                    RaiseChanged();
                });

            var result = await operation(context).ConfigureAwait(false);
            record.Status = OperationTaskStatus.Succeeded;
            record.ProgressPercent = 100;
            record.ProgressStatus = "完成";
            return result;
        }
        catch (OperationCanceledException)
        {
            record.Status = OperationTaskStatus.Cancelled;
            record.ProgressStatus = "已取消";
            throw;
        }
        catch (Exception ex)
        {
            record.Status = OperationTaskStatus.Failed;
            record.Error = ex.Message;
            record.ProgressStatus = "失败";
            _logging.Error($"任务 {record.Title} 失败：{ex.Message}", "Task");
            throw;
        }
        finally
        {
            record.FinishedAt = DateTimeOffset.Now;
            record.Duration = record.FinishedAt.Value - startedAt;
            lock (_sync)
            {
                _cancelSources.Remove(record.Id);
            }

            RaiseChanged();
            // 先落盘再释放队列，避免后一个任务写出的新历史被前一个任务的旧快照覆盖。
            Save();
            if (acquired)
            {
                _gate.Release();
            }
        }
    }

    public void Cancel(Guid id)
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            _cancelSources.TryGetValue(id, out cts);
        }

        cts?.Cancel();
        lock (_sync)
        {
            var record = _tasks.FirstOrDefault(t => t.Id == id);
            if (record is { IsCompleted: false })
            {
                record.ProgressStatus = record.Status == OperationTaskStatus.Queued ? "已取消（排队）" : "正在取消";
            }
        }

        RaiseChanged();
    }

    public void ClearCompleted()
    {
        lock (_sync)
        {
            _tasks.RemoveAll(t => t.IsCompleted);
        }

        RaiseChanged();
        Save();
    }

    private void UpdateProgress(OperationTaskRecord record, TaskProgress progress)
    {
        record.ProgressPercent = Math.Clamp(progress.Percentage, 0, 100);
        record.ProgressStatus = progress.Status;
        RaiseChanged();
        if (Interlocked.Exchange(ref _historyDirty, 1) == 0)
        {
            // 历史落盘放在后台，避免每个进度点同步写盘。
            _ = Task.Run(async () =>
            {
                await Task.Delay(400).ConfigureAwait(false);
                Interlocked.Exchange(ref _historyDirty, 0);
                Save();
            });
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logging.Warn($"任务队列事件处理失败：{ex.Message}", "Task");
        }
    }

    private void TrimHistory()
    {
        if (_tasks.Count > 500)
        {
            _tasks.RemoveRange(500, _tasks.Count - 500);
        }
    }

    private void Save()
    {
        try
        {
            lock (_saveLock)
            {
                OperationTaskRecord[] snapshot;
                lock (_sync)
                {
                    snapshot = _tasks.ToArray();
                }

                var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                var temp = _historyPath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _historyPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logging.Warn($"任务历史保存失败：{ex.Message}", "Task");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_historyPath))
            {
                return;
            }

            var items = JsonSerializer.Deserialize<OperationTaskRecord[]>(
                File.ReadAllText(_historyPath),
                JsonOptions);
            if (items is null)
            {
                return;
            }

            lock (_sync)
            {
                _tasks.AddRange(items);
                foreach (var task in _tasks.Where(t => t.Status == OperationTaskStatus.Running))
                {
                    task.Status = OperationTaskStatus.Failed;
                    task.Error = "程序退出时任务中断";
                    task.FinishedAt = DateTimeOffset.Now;
                }
            }
        }
        catch (Exception ex)
        {
            _logging.Warn($"任务历史读取失败：{ex.Message}", "Task");
        }
    }
}
