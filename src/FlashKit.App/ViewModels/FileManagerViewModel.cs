using System.Collections.ObjectModel;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;

namespace FlashKit.App.ViewModels;

public sealed class FileManagerViewModel : ObservableObject
{
    private readonly IDeviceManager _deviceManager;
    private readonly IDeviceFileService _fileService;
    private readonly ITaskQueueService _taskQueue;
    private readonly IProcessRunner _processRunner;
    private string _currentPath = "/sdcard";
    private string _pathInput = "/sdcard";
    private DeviceFileEntry? _selectedItem;
    private string _status = "选择设备后刷新";
    private string _errorMessage = string.Empty;
    private string _lastLocalPath = string.Empty;
    private double _progress;
    private bool _isBusy;

    public FileManagerViewModel(
        IDeviceManager deviceManager,
        IDeviceFileService fileService,
        ITaskQueueService taskQueue,
        IProcessRunner processRunner)
    {
        _deviceManager = deviceManager;
        _fileService = fileService;
        _taskQueue = taskQueue;
        _processRunner = processRunner;
    }

    public ObservableCollection<DeviceFileEntry> Items { get; } = new();
    public string CurrentPath { get => _currentPath; private set => SetProperty(ref _currentPath, value); }
    public string PathInput { get => _pathInput; set => SetProperty(ref _pathInput, value); }
    public DeviceFileEntry? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(CanDownload), nameof(SelectedText));
            }
        }
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string LastLocalPath
    {
        get => _lastLocalPath;
        private set { if (SetProperty(ref _lastLocalPath, value)) OnPropertyChanged(nameof(CanOpenLocal)); }
    }

    public bool CanOpenLocal => !string.IsNullOrWhiteSpace(LastLocalPath) && Directory.Exists(LastLocalPath);
    public string ErrorMessage
    {
        get => _errorMessage;
        private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(IsNotBusy), nameof(CanDownload)); }
    }

    public bool IsNotBusy => !IsBusy;
    public bool CanDownload => !IsBusy && SelectedItem is not null;
    public bool CanGoUp => CurrentPath != "/";
    public string SelectedText => SelectedItem is null ? "未选择" : SelectedItem.Name;
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public async Task ActivateAsync()
    {
        if (Items.Count == 0)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    public async Task NavigateAsync(string? path = null)
    {
        var target = string.IsNullOrWhiteSpace(path) ? PathInput : path;
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var device = _deviceManager.SelectedDevice;
        if (device is null)
        {
            ErrorMessage = "未选择设备。";
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        try
        {
            Status = "正在读取目录…";
            var items = await _fileService.ListAsync(device.Serial, target).ConfigureAwait(true);
            CurrentPath = target.StartsWith('/') ? target.TrimEnd('/') : "/" + target.TrimEnd('/');
            if (CurrentPath.Length == 0)
            {
                CurrentPath = "/";
            }

            PathInput = CurrentPath;
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }

            SelectedItem = null;
            Status = $"{items.Count} 项 · {CurrentPath}";
            OnPropertyChanged(nameof(CanGoUp));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Status = "目录读取失败";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task RefreshAsync() => NavigateAsync(CurrentPath);

    public Task GoUpAsync()
    {
        if (!CanGoUp)
        {
            return Task.CompletedTask;
        }

        var parent = CurrentPath.TrimEnd('/');
        var index = parent.LastIndexOf('/');
        return NavigateAsync(index <= 0 ? "/" : parent[..index]);
    }

    public async Task EnterSelectedAsync()
    {
        if (SelectedItem is { IsDirectory: true } dir)
        {
            await NavigateAsync(dir.FullPath).ConfigureAwait(true);
        }
    }

    public async Task UploadAsync(string localPath)
    {
        var device = _deviceManager.SelectedDevice;
        if (device is null || IsBusy)
        {
            return;
        }

        var name = Path.GetFileName(localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var remote = CurrentPath == "/" ? "/" + name : CurrentPath + "/" + name;
        if (!_fileService.IsSafeWritePath(remote, out var reason))
        {
            ErrorMessage = reason;
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        Progress = 0;
        Status = "准备上传…";
        try
        {
            var request = new OperationTaskRequest
            {
                Type = OperationTaskType.FileTransfer,
                Title = $"上传 {name}",
                Serial = device.Serial,
                InputSummary = localPath,
                OutputPath = remote
            };
            TaskExecutionContext? queueContext = null;
            var progress = new Progress<FileTransferProgress>(p =>
            {
                Progress = p.Percentage;
                Status = p.Status;
                queueContext?.Report(p.Percentage, p.Status);
            });
            var result = await _taskQueue.RunAsync(request, async context =>
            {
                queueContext = context;
                return await _fileService.PushAsync(
                    device.Serial, localPath, remote, p => ((IProgress<FileTransferProgress>)progress).Report(p), context.CancellationToken).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(true);
            if (result.ExitCode != 0)
            {
                ErrorMessage = result.Message;
            }

            Status = result.Message;
            LastLocalPath = localPath;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Status = "上传失败";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DownloadAsync(string localDirectory)
    {
        var device = _deviceManager.SelectedDevice;
        var selected = SelectedItem;
        if (device is null || selected is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        Progress = 0;
        Status = "准备下载…";
        try
        {
            var request = new OperationTaskRequest
            {
                Type = OperationTaskType.FileTransfer,
                Title = $"下载 {selected.Name}",
                Serial = device.Serial,
                InputSummary = selected.FullPath,
                OutputPath = localDirectory
            };
            TaskExecutionContext? queueContext = null;
            var progress = new Progress<FileTransferProgress>(p =>
            {
                Progress = p.Percentage;
                Status = p.Status;
                queueContext?.Report(p.Percentage, p.Status);
            });
            var result = await _taskQueue.RunAsync(request, async context =>
            {
                queueContext = context;
                return await _fileService.PullAsync(
                    device.Serial, selected.FullPath, localDirectory, p => ((IProgress<FileTransferProgress>)progress).Report(p), context.CancellationToken).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(true);
            if (result.ExitCode != 0)
            {
                ErrorMessage = result.Message;
            }

            Status = result.Message;
            LastLocalPath = Directory.Exists(localDirectory)
                ? Path.Combine(localDirectory, selected.Name)
                : localDirectory;
            OnPropertyChanged(nameof(CanOpenLocal));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Status = "下载失败";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task OpenLocalAsync() =>
        !string.IsNullOrWhiteSpace(LastLocalPath) && Directory.Exists(LastLocalPath)
            ? _processRunner.OpenFolderAsync(LastLocalPath)
            : Task.CompletedTask;
}
