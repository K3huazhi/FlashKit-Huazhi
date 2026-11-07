using System.Collections.ObjectModel;
using FlashKit.App.Services;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using Microsoft.UI.Dispatching;

namespace FlashKit.App.ViewModels;

public sealed class TaskQueueViewModel : ObservableObject
{
    private readonly ITaskQueueService _queue;
    private readonly IProcessRunner _processRunner;
    private readonly DispatcherQueue? _dispatcher;
    private readonly List<OperationTaskRecord> _all = new();
    private string _selectedType = "全部类型";
    private string _selectedStatus = "全部状态";
    private string _selectedTime = "全部时间";
    private string _selectedSerial = "全部设备";

    public TaskQueueViewModel(
        ITaskQueueService queue,
        IProcessRunner processRunner,
        WindowManager windowManager)
    {
        _queue = queue;
        _processRunner = processRunner;
        WindowManager = windowManager;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _queue.Changed += (_, _) => RunOnUi(Refresh);
        Refresh();
    }

    public WindowManager WindowManager { get; }
    public ObservableCollection<OperationTaskRecord> Tasks { get; } = new();
    public ObservableCollection<string> TypeFilters { get; } = new()
    {
        "全部类型", "刷入", "备份", "还原", "卡刷包解包", "卡刷包打包",
        "镜像转换", "payload 解包", "文件传输", "文件系统解包", "文件系统打包"
    };
    public ObservableCollection<string> StatusFilters { get; } = new()
    {
        "全部状态", "排队", "运行中", "成功", "失败", "已取消"
    };
    public ObservableCollection<string> TimeFilters { get; } = new()
    {
        "全部时间", "今天", "近 7 天"
    };
    public ObservableCollection<string> SerialFilters { get; } = new() { "全部设备" };

    public string SelectedType
    {
        get => _selectedType;
        set
        {
            if (SetProperty(ref _selectedType, value))
            {
                ApplyFilter();
            }
        }
    }

    public string SelectedStatus
    {
        get => _selectedStatus;
        set
        {
            if (SetProperty(ref _selectedStatus, value))
            {
                ApplyFilter();
            }
        }
    }

    public string SelectedTime
    {
        get => _selectedTime;
        set
        {
            if (SetProperty(ref _selectedTime, value))
            {
                ApplyFilter();
            }
        }
    }

    public string SelectedSerial
    {
        get => _selectedSerial;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "全部设备" : value;
            if (SetProperty(ref _selectedSerial, normalized))
            {
                ApplyFilter();
            }
        }
    }

    public bool HasTasks => Tasks.Count > 0;
    public string EmptyText => _all.Count == 0 ? "还没有任务记录" : "没有符合筛选条件的任务";

    public void Refresh()
    {
        _all.Clear();
        _all.AddRange(_queue.Snapshot());
        var serials = _all
            .Select(t => t.Serial)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var currentSerial = SelectedSerial;
        SerialFilters.Clear();
        SerialFilters.Add("全部设备");
        foreach (var serial in serials)
        {
            SerialFilters.Add(serial);
        }

        if (string.IsNullOrWhiteSpace(currentSerial) || !SerialFilters.Contains(currentSerial))
        {
            currentSerial = "全部设备";
        }

        _selectedSerial = currentSerial;
        OnPropertyChanged(nameof(SelectedSerial));
        ApplyFilter();
    }

    public void ClearCompleted()
    {
        _queue.ClearCompleted();
        Refresh();
    }

    public void CancelTask(OperationTaskRecord task)
    {
        if (!task.IsCompleted)
        {
            _queue.Cancel(task.Id);
        }
    }

    public Task OpenOutputAsync(OperationTaskRecord task)
    {
        if (string.IsNullOrWhiteSpace(task.OutputPath) || !Directory.Exists(task.OutputPath))
        {
            return Task.CompletedTask;
        }

        return _processRunner.OpenFolderAsync(task.OutputPath);
    }

    private void ApplyFilter()
    {
        var query = _all.AsEnumerable();

        if (SelectedType != "全部类型")
        {
            query = query.Where(t => t.TypeText == SelectedType);
        }

        if (SelectedStatus != "全部状态")
        {
            query = query.Where(t => t.StatusText == SelectedStatus);
        }

        if (SelectedSerial != "全部设备")
        {
            query = query.Where(t => t.Serial.Equals(SelectedSerial, StringComparison.OrdinalIgnoreCase));
        }

        if (SelectedTime == "今天")
        {
            var today = DateTimeOffset.Now.Date;
            query = query.Where(t => t.CreatedAt.LocalDateTime.Date == today);
        }
        else if (SelectedTime == "近 7 天")
        {
            var since = DateTimeOffset.Now.AddDays(-7);
            query = query.Where(t => t.CreatedAt >= since);
        }

        var filtered = query.OrderByDescending(t => t.CreatedAt).ToArray();
        Tasks.Clear();
        foreach (var task in filtered)
        {
            Tasks.Add(task);
        }

        OnPropertyChanged(nameof(HasTasks), nameof(EmptyText));
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
