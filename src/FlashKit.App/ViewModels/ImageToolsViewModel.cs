using System.Collections.ObjectModel;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using Microsoft.UI.Dispatching;

namespace FlashKit.App.ViewModels;

public sealed class ImageToolsViewModel : ObservableObject
{
    private readonly IImageToolService _imageTools;
    private readonly ITaskQueueService _taskQueue;
    private readonly IProcessRunner _processRunner;
    private readonly DispatcherQueue? _dispatcher;
    private string _sourcePath = string.Empty;
    private string _outputPath = string.Empty;
    private string _expectedHash = string.Empty;
    private string _status = "选择镜像与操作后开始";
    private string _resultText = string.Empty;
    private double _progress;
    private bool _isBusy;
    private int _selectedOperationIndex;

    public ImageToolsViewModel(
        IImageToolService imageTools,
        ITaskQueueService taskQueue,
        IProcessRunner processRunner)
    {
        _imageTools = imageTools;
        _taskQueue = taskQueue;
        _processRunner = processRunner;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Operations = new ObservableCollection<string>
        {
            "查看镜像信息",
            "Sparse → Raw",
            "Raw → Sparse",
            "计算 MD5 / SHA256",
            "payload.bin 解包"
        };
    }

    public ObservableCollection<string> Operations { get; }

    public int SelectedOperationIndex
    {
        get => _selectedOperationIndex;
        set
        {
            if (SetProperty(ref _selectedOperationIndex, value))
            {
                OnPropertyChanged(nameof(IsConversion), nameof(IsPayload), nameof(IsHash), nameof(OutputHint), nameof(CanStart));
            }
        }
    }

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
        set
        {
            if (SetProperty(ref _outputPath, value))
            {
                OnPropertyChanged(nameof(CanStart));
            }
        }
    }

    public string ExpectedHash
    {
        get => _expectedHash;
        set => SetProperty(ref _expectedHash, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (SetProperty(ref _resultText, value))
            {
                OnPropertyChanged(nameof(HasResult));
            }
        }
    }

    public bool HasResult => !string.IsNullOrWhiteSpace(ResultText);
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy), nameof(CanStart));
            }
        }
    }

    public bool IsNotBusy => !IsBusy;
    public bool IsConversion => SelectedOperationIndex is 1 or 2;
    public bool IsPayload => SelectedOperationIndex == 4;
    public bool IsHash => SelectedOperationIndex == 3;
    public string OutputHint => SelectedOperationIndex switch
    {
        1 => "输出 raw img 路径",
        2 => "输出 sparse img 路径",
        4 => "payload 解包输出目录",
        _ => "输出目录（可选）"
    };

    public bool CanStart => !IsBusy && !string.IsNullOrWhiteSpace(SourcePath) &&
                            (!IsConversion && !IsPayload || !string.IsNullOrWhiteSpace(OutputPath));

    public async Task StartAsync()
    {
        if (!CanStart)
        {
            return;
        }

        IsBusy = true;
        Progress = 0;
        ResultText = string.Empty;
        Status = "准备中…";
        try
        {
            var source = SourcePath;
            var target = OutputPath;
            var expected = ExpectedHash;
            var operation = SelectedOperationIndex;
            var request = new OperationTaskRequest
            {
                Type = operation == 4 ? OperationTaskType.PayloadExtract : OperationTaskType.ImageConvert,
                Title = Operations[operation] + " " + Path.GetFileName(source),
                InputSummary = source,
                OutputPath = target
            };

            TaskExecutionContext? queueContext = null;
            var progress = new Progress<OperationProgress>(p =>
            {
                Progress = p.Percentage;
                Status = p.Status;
                queueContext?.Report(p.Percentage, p.Status);
            });
            var result = await _taskQueue.RunAsync(request, async context =>
            {
                queueContext = context;

                switch (operation)
                {
                    case 0:
                    {
                        var info = await _imageTools.InspectAsync(source, context.CancellationToken).ConfigureAwait(false);
                        context.Report(100, "信息读取完成");
                        return info.Summary +
                               $"\n格式：{info.Format}\n大小：{PartitionInfo.FormatBytes(info.FileSize)}" +
                               (info.ExpandedSizeBytes is { } expanded ? $"\n展开：{PartitionInfo.FormatBytes(expanded)}" : string.Empty) +
                               (info.BlockSize is { } block ? $"\n块大小：{block}" : string.Empty) +
                               (info.SparseChunks is { } chunks ? $"\nchunks：{chunks}，header CRC：0x{info.SparseChecksum:X8}" : string.Empty);
                    }
                    case 1:
                    case 2:
                    {
                        var targetFormat = operation == 1 ? ImageFormatKind.Raw : ImageFormatKind.Sparse;
                        var converted = await _imageTools.ConvertAsync(
                            new ImageConversionOptions
                            {
                                SourcePath = source,
                                OutputPath = target,
                                TargetFormat = targetFormat
                            },
                            progress,
                            context.CancellationToken).ConfigureAwait(false);
                        context.SetOutputPath(converted.OutputPath);
                        context.Report(100, "转换完成");
                        return $"输出：{converted.OutputPath}\n大小：{PartitionInfo.FormatBytes(converted.SizeBytes)}";
                    }
                    case 3:
                    {
                        var hash = await _imageTools.HashAsync(source, expected, context.CancellationToken).ConfigureAwait(false);
                        context.Report(100, "哈希计算完成");
                        return $"MD5：{hash.Md5}\nSHA256：{hash.Sha256}" +
                               (hash.MatchesExpected is null
                                   ? string.Empty
                                   : hash.MatchesExpected == true ? "\n比对：匹配" : "\n比对：不匹配");
                    }
                    default:
                    {
                        var payload = await _imageTools.ExtractPayloadAsync(
                            source,
                            target,
                            progress,
                            line => RunOnUi(() => Status = line),
                            context.CancellationToken).ConfigureAwait(false);
                        context.SetOutputPath(payload.OutputDirectory);
                        context.Report(100, "payload 解包完成");
                        return $"输出：{payload.OutputDirectory}\n分区：{payload.Images.Count} 个";
                    }
                }
            }, CancellationToken.None).ConfigureAwait(true);

            ResultText = result;
            Status = "完成";
            Progress = 100;
        }
        catch (OperationCanceledException)
        {
            Status = "已取消";
        }
        catch (Exception ex)
        {
            Status = "失败：" + ex.Message;
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
