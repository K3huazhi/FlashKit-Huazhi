using System.Collections.ObjectModel;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using Microsoft.UI.Dispatching;

namespace FlashKit.App.ViewModels;

public sealed class FileSystemViewModel : ObservableObject
{
    private readonly IDeviceManager _deviceManager;
    private readonly IFileSystemService _fileSystemService;
    private readonly IImageToolService _imageTools;
    private readonly ITaskQueueService _taskQueue;
    private readonly IProcessRunner _processRunner;
    private readonly DispatcherQueue? _dispatcher;
    private string _sourcePath = string.Empty;
    private string _outputPath = string.Empty;
    private string _status = "选择镜像后开始";
    private string _resultText = string.Empty;
    private string _capabilitiesText = "尚未探测设备工具链";
    private string _sourceFormatText = "未选择镜像";
    private string _errorMessage = string.Empty;
    private double _progress;
    private bool _isBusy;
    private int _operationIndex;
    private string _selectedPackFormat = "ext4";

    public FileSystemViewModel(
        IDeviceManager deviceManager,
        IFileSystemService fileSystemService,
        IImageToolService imageTools,
        ITaskQueueService taskQueue,
        IProcessRunner processRunner)
    {
        _deviceManager = deviceManager;
        _fileSystemService = fileSystemService;
        _imageTools = imageTools;
        _taskQueue = taskQueue;
        _processRunner = processRunner;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Operations = new ObservableCollection<string> { "解包", "打包" };
        PackFormats = new ObservableCollection<string> { "ext4", "erofs", "f2fs" };
    }

    public ObservableCollection<string> Operations { get; }
    public ObservableCollection<string> PackFormats { get; }
    public string SelectedPackFormat
    {
        get => _selectedPackFormat;
        set => SetProperty(ref _selectedPackFormat, value);
    }
    public int OperationIndex
    {
        get => _operationIndex;
        set
        {
            if (SetProperty(ref _operationIndex, value))
            {
                OnPropertyChanged(nameof(IsUnpack), nameof(IsPack), nameof(OutputHint), nameof(CanStart));
            }
        }
    }

    public bool IsUnpack => OperationIndex == 0;
    public bool IsPack => OperationIndex == 1;
    public string OutputHint => IsPack ? "输出镜像路径" : "解包输出目录（留空则源文件同级）";
    public string SourcePath
    {
        get => _sourcePath;
        set
        {
            if (SetProperty(ref _sourcePath, value))
            {
                OnPropertyChanged(nameof(CanStart));
            }
        }
    }

    public string OutputPath
    {
        get => _outputPath;
        set => SetProperty(ref _outputPath, value);
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ResultText
    {
        get => _resultText;
        private set { if (SetProperty(ref _resultText, value)) OnPropertyChanged(nameof(HasResult)); }
    }

    public bool HasResult => !string.IsNullOrWhiteSpace(ResultText);
    public string CapabilitiesText { get => _capabilitiesText; private set => SetProperty(ref _capabilitiesText, value); }
    public string SourceFormatText { get => _sourceFormatText; private set => SetProperty(ref _sourceFormatText, value); }
    public string ErrorMessage { get => _errorMessage; private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(CanStart), nameof(IsNotBusy)); } }
    public bool IsNotBusy => !IsBusy;

    public bool CanStart => !IsBusy && !string.IsNullOrWhiteSpace(SourcePath) &&
                            (IsUnpack || !string.IsNullOrWhiteSpace(OutputPath));

    public async Task ActivateAsync()
    {
        var device = _deviceManager.SelectedDevice;
        if (device is null)
        {
            CapabilitiesText = "未选择设备";
            return;
        }

        try
        {
            var capabilities = await _fileSystemService.CheckCapabilitiesAsync(device.Serial).ConfigureAwait(true);
            CapabilitiesText =
                $"设备工具链：{capabilities.Toolchain}\n" +
                $"erofs 解包：{(capabilities.ErofsUnpack ? "可用" : "不可用")} · " +
                $"ext4 解包：{(capabilities.Ext4Unpack ? "可用" : "不可用")} · " +
                $"f2fs 解包：{(capabilities.F2fsUnpack ? "可用" : "不可用")}\n" +
                $"ext4 打包：{(capabilities.Ext4Pack ? "可用" : "不可用")} · " +
                $"erofs 打包：{(capabilities.ErofsPack ? "可用" : "不可用")} · " +
                $"f2fs 打包：{(capabilities.F2fsPack ? "可用" : "不可用")}";
        }
        catch (Exception ex)
        {
            CapabilitiesText = "工具链探测失败：" + ex.Message;
        }
    }

    public async Task SetSourceAsync(string path)
    {
        SourcePath = path;
        ErrorMessage = string.Empty;
        try
        {
            var info = await _imageTools.InspectAsync(path).ConfigureAwait(true);
            SourceFormatText = $"{info.Format} · {PartitionInfo.FormatBytes(info.FileSize)}" +
                               (info.BlockSize is { } bs ? $" · 块 {bs}" : string.Empty);
        }
        catch (Exception ex)
        {
            SourceFormatText = "镜像识别失败：" + ex.Message;
        }
    }

    public async Task StartAsync()
    {
        if (!CanStart)
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
        ResultText = string.Empty;
        Progress = 0;
        Status = "准备中…";
        try
        {
            var source = SourcePath;
            var output = OutputPath;
            var operation = OperationIndex;
            var request = new OperationTaskRequest
            {
                Type = operation == 0 ? OperationTaskType.FileSystemUnpack : OperationTaskType.FileSystemPack,
                Title = $"{(operation == 0 ? "文件系统解包" : "文件系统打包")} {Path.GetFileName(source)}",
                Serial = device.Serial,
                InputSummary = source,
                OutputPath = output
            };

            TaskExecutionContext? queueContext = null;
            var progress = new Progress<OperationProgress>(p =>
            {
                Progress = p.Percentage;
                Status = p.Status;
                queueContext?.Report(p.Percentage, p.Status);
            });

            var resultText = await _taskQueue.RunAsync(request, async context =>
            {
                queueContext = context;
                if (operation == 0)
                {
                    var result = await _fileSystemService.UnpackAsync(new FileSystemUnpackRequest
                    {
                        Serial = device.Serial,
                        SourcePath = source,
                        OutputDirectory = string.IsNullOrWhiteSpace(output) ? null : output
                    }, progress, line => RunOnUi(() => Status = line), context.CancellationToken).ConfigureAwait(false);
                    context.SetOutputPath(result.OutputDirectory);
                    return $"输出：{result.OutputDirectory}\n文件系统：{result.Format}\n条目：{result.FileCount}";
                }

                var format = SelectedPackFormat switch
                {
                    "erofs" => FileSystemFormat.Erofs,
                    "f2fs" => FileSystemFormat.F2fs,
                    _ => FileSystemFormat.Ext4
                };
                var image = await _fileSystemService.PackAsync(new FileSystemPackRequest
                {
                    Serial = device.Serial,
                    SourceDirectory = source,
                    OutputImagePath = output,
                    Format = format
                }, progress, line => RunOnUi(() => Status = line), context.CancellationToken).ConfigureAwait(false);
                context.SetOutputPath(image);
                return "输出：" + image;
            }, CancellationToken.None).ConfigureAwait(true);

            ResultText = resultText;
            Status = "完成";
            Progress = 100;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Status = IsUnpack ? "解包失败" : "打包失败";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task OpenOutputAsync()
    {
        var path = Directory.Exists(OutputPath) ? OutputPath : Path.GetDirectoryName(OutputPath);
        return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
            ? _processRunner.OpenFolderAsync(path)
            : Task.CompletedTask;
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
