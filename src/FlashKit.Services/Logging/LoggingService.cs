using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;

namespace FlashKit.Services.Logging;

/// <summary>
/// 线程安全的全局日志存储。UI 不直接绑定本服务的集合，而是订阅事件并在 UI 线程
/// 维护自己的 ObservableCollection，避免后台轮询命令触发跨线程集合异常。
/// </summary>
public sealed class LoggingService : ILoggingService
{
    private const int MaxEntries = 10_000;
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = new(MaxEntries);

    public event EventHandler<LogEntry>? EntryAdded;
    public event EventHandler? Cleared;

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
            }
        }

        EntryAdded?.Invoke(this, entry);
    }

    public void Info(string message, string category = "App") =>
        Add(new LogEntry { Kind = LogKind.Info, Message = message, Category = category });

    public void Warn(string message, string category = "App") =>
        Add(new LogEntry { Kind = LogKind.Warning, Message = message, Category = category });

    public void Error(string message, string category = "App") =>
        Add(new LogEntry { Kind = LogKind.Error, Message = message, Category = category });

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }

        Cleared?.Invoke(this, EventArgs.Empty);
    }
}
