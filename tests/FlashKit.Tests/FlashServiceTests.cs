using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using FlashKit.Services.Flash;
using Xunit;

namespace FlashKit.Tests;

public sealed class FlashServiceTests
{
    [Fact]
    public async Task ExecuteAsync_UsesEditedPartitionNameVerbatim()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "custom.img");
        await File.WriteAllBytesAsync(file, new byte[4096]);
        var fastboot = new RecordingFastboot();
        var service = new FlashService(fastboot, new NullLogging());
        var item = new FlashItem
        {
            PartitionName = "user_defined_target",
            FilePath = file,
            FileSize = 4096,
            IsSelected = true
        };

        var summary = await service.ExecuteAsync(
            new FlashRunOptions { Serial = "SERIAL" },
            [item]);

        Assert.Equal(1, summary.SuccessCount);
        Assert.Equal("user_defined_target", fastboot.LastPartition);
        Assert.Equal(file, fastboot.LastImagePath);
    }

    private sealed class RecordingFastboot : IFastbootClient
    {
        public string? LastPartition { get; private set; }
        public string? LastImagePath { get; private set; }

        public Task<IReadOnlyList<FastbootDevice>> ListDevicesAsync(CancellationToken cancellationToken = default, bool logToHistory = false) =>
            Task.FromResult<IReadOnlyList<FastbootDevice>>([new FastbootDevice { Serial = "SERIAL" }]);

        public Task<ProcessResult> ExecuteRawAsync(
            string? serial,
            IReadOnlyList<string> arguments,
            Action<ProcessOutputLine>? onOutput = null,
            CancellationToken cancellationToken = default,
            bool logToHistory = true,
            TimeSpan? timeout = null) =>
            throw new NotSupportedException();

        public Task<string?> GetVarAsync(string serial, string variableName, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyDictionary<string, string>> GetVarAllAsync(string serial, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<bool?> IsUserspaceAsync(string serial, CancellationToken cancellationToken = default) =>
            Task.FromResult<bool?>(true);

        public Task<ProcessResult> FlashAsync(
            string serial,
            string partitionName,
            string imagePath,
            Action<ProcessOutputLine>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            LastPartition = partitionName;
            LastImagePath = imagePath;
            return Task.FromResult(new ProcessResult
            {
                ExecutablePath = "fastboot",
                Arguments = ["flash", partitionName, imagePath],
                ExitCode = 0
            });
        }

        public Task<ProcessResult> RebootAsync(string serial, string? target = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProcessResult> FlashLogicalPartitionAsync(
            string serial,
            string partition,
            string imagePath,
            Action<ProcessOutputLine>? onOutput = null,
            CancellationToken cancellationToken = default) =>
            FlashAsync(serial, partition, imagePath, onOutput, cancellationToken);

        public Task<ProcessResult> FetchAsync(
            string serial,
            string partitionName,
            string outputPath,
            Action<ProcessOutputLine>? onOutput = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FlashKitFlashTests", Guid.NewGuid().ToString("N"));
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

    private sealed class NullLogging : ILoggingService
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
