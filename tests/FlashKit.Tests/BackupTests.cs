using System.Text.Json;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using FlashKit.Services.Backup;
using FlashKit.Services.Partitions;
using Xunit;

namespace FlashKit.Tests;

public sealed class BackupTests
{
    [Fact]
    public async Task SelectiveBackup_WritesOnlySelectedAndListsSkipped()
    {
        using var temp = new TempDirectory();
        var all = new[]
        {
            Selection("boot", 4096, BackupPartitionGroup.Regular),
            Selection("system", 8192, BackupPartitionGroup.SuperLogical),
            Selection("userdata", 16384, BackupPartitionGroup.HighRisk, PartitionRisk.DataLoss),
            Selection("super", 32768, BackupPartitionGroup.SuperLogical, PartitionRisk.Critical, isSuper: true)
        };
        var service = new BackupService(new FakeAdb(), new NullLogging());
        var result = await service.BackupAsync(new BackupRunOptions
        {
            Serial = "SERIAL",
            DestinationRoot = temp.Path,
            AllPartitions = all,
            SelectedPartitions = [all[0]]
        });

        Assert.Single(result.Manifest.Images);
        Assert.Equal("boot", result.Manifest.Images[0].PartitionName);
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory, "images", "boot.img")));
        Assert.False(File.Exists(Path.Combine(result.OutputDirectory, "images", "system.img")));
        Assert.False(File.Exists(Path.Combine(result.OutputDirectory, "images", "userdata.img")));
        Assert.Contains("system", result.Manifest.NotBackedUpPartitions);
        Assert.Contains("userdata", result.Manifest.NotBackedUpPartitions);
        Assert.Contains("super", result.Manifest.NotBackedUpPartitions);
        Assert.DoesNotContain("boot", result.Manifest.NotBackedUpPartitions);
        Assert.True(result.Manifest.IsSelective);

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(result.ManifestPath!));
        Assert.True(manifest.RootElement.GetProperty("isSelective").GetBoolean());
        Assert.Equal(1, manifest.RootElement.GetProperty("images").GetArrayLength());
        Assert.Equal("boot", manifest.RootElement.GetProperty("images")[0].GetProperty("partitionName").GetString());
    }

    [Fact]
    public async Task RecoveryCatalog_ParsesByNameAndMapperEntries()
    {
        var adb = new FakeAdb
        {
            ScriptOutput = "boot|/dev/block/by-name/boot|4096\n" +
                           "system|/dev/block/mapper/system|8192\n" +
                           "super|/dev/block/by-name/super|32768\n"
        };
        var service = new PartitionCatalogService(adb, null!, new NullLogging());
        var catalog = await service.GetAsync("SERIAL", DeviceMode.Recovery, forceRefresh: true);

        Assert.Equal(3, catalog.Partitions.Count);
        Assert.Equal(4096, catalog.Partitions.Single(p => p.Name == "boot").SizeBytes);
        Assert.Equal(8192, catalog.Partitions.Single(p => p.Name == "system").SizeBytes);
        Assert.True(catalog.Partitions.Single(p => p.Name == "system").IsDynamic);
        Assert.True(catalog.HasSizes);
    }

    [Fact]
    public async Task RecoveryBackup_UsesDdWithNoErrorSync()
    {
        using var temp = new TempDirectory();
        var all = new[] { Selection("apdp", 1024, BackupPartitionGroup.Regular) };
        var adb = new FakeAdb();
        var service = new BackupService(adb, new NullLogging());
        var result = await service.BackupAsync(new BackupRunOptions
        {
            Serial = "SERIAL",
            DestinationRoot = temp.Path,
            AllPartitions = all,
            SelectedPartitions = all
        });

        Assert.Single(result.Manifest.Images);
        Assert.Contains("dd if=", adb.LastCommand);
        Assert.Contains("conv=noerror,sync", adb.LastCommand);
    }

    [Fact]
    public async Task DdPaddedOutput_IsTruncatedToPartitionSize()
    {
        using var temp = new TempDirectory();
        var all = new[] { Selection("apdp", 1024, BackupPartitionGroup.Regular) };
        var adb = new FakeAdb { AppendStatusToOutput = true };
        var service = new BackupService(adb, new NullLogging());
        var result = await service.BackupAsync(new BackupRunOptions
        {
            Serial = "SERIAL",
            DestinationRoot = temp.Path,
            AllPartitions = all,
            SelectedPartitions = all
        });

        var image = Assert.Single(result.Manifest.Images);
        Assert.Equal(1024, image.SizeBytes);
        var path = Path.Combine(result.OutputDirectory, "images", "apdp.img");
        Assert.True(File.Exists(path));
        Assert.Equal(1024, new FileInfo(path).Length);
    }

    [Fact]
    public async Task NonRootRecovery_IsRejectedBeforeWritingFiles()
    {
        using var temp = new TempDirectory();
        var all = new[] { Selection("boot", 4096, BackupPartitionGroup.Regular) };
        var service = new BackupService(new FakeAdb(root: false), new NullLogging());

        await Assert.ThrowsAsync<BackupException>(() => service.BackupAsync(new BackupRunOptions
        {
            Serial = "SERIAL",
            DestinationRoot = temp.Path,
            AllPartitions = all,
            SelectedPartitions = all
        }));

        Assert.Empty(Directory.GetFiles(temp.Path, "*.img", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SuperBackup_CoversLogicalPartitionsWithoutDuplicatingFiles()
    {
        using var temp = new TempDirectory();
        var all = new[]
        {
            Selection("boot", 4096, BackupPartitionGroup.Regular),
            Selection("system", 8192, BackupPartitionGroup.SuperLogical),
            Selection("vendor", 16384, BackupPartitionGroup.SuperLogical),
            Selection("super", 32768, BackupPartitionGroup.SuperLogical, PartitionRisk.Critical, isSuper: true)
        };
        var super = all.Single(p => p.IsSuperContainer);
        var boot = all.Single(p => p.PartitionName == "boot");
        var service = new BackupService(new FakeAdb(), new NullLogging());
        var result = await service.BackupAsync(new BackupRunOptions
        {
            Serial = "SERIAL",
            DestinationRoot = temp.Path,
            AllPartitions = all,
            SelectedPartitions = [super, boot]
        });

        Assert.Equal(2, result.Manifest.Images.Count);
        Assert.Contains(result.Manifest.Images, i => i.PartitionName == "super");
        Assert.Contains(result.Manifest.Images, i => i.PartitionName == "boot");
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory, "images", "super.img")));
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory, "images", "boot.img")));
        Assert.False(File.Exists(Path.Combine(result.OutputDirectory, "images", "system.img")));
        Assert.False(File.Exists(Path.Combine(result.OutputDirectory, "images", "vendor.img")));
        Assert.Contains("system", result.Manifest.CoveredBySuperPartitions);
        Assert.Contains("vendor", result.Manifest.CoveredBySuperPartitions);
        Assert.DoesNotContain("system", result.Manifest.NotBackedUpPartitions);
        Assert.DoesNotContain("boot", result.Manifest.NotBackedUpPartitions);
    }

    private static BackupPartitionSelection Selection(
        string name,
        long size,
        BackupPartitionGroup group,
        PartitionRisk risk = PartitionRisk.Normal,
        bool isSuper = false) => new(name, size, risk, group, isSuper);

    private sealed class FakeAdb : IAdbClient
    {
        private readonly bool _root;

        public FakeAdb(bool root = true)
        {
            _root = root;
        }

        public string ScriptOutput { get; set; } = string.Empty;
        public string LastCommand { get; private set; } = string.Empty;
        public bool AppendStatusToOutput { get; set; }

        public Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken cancellationToken = default, bool logToHistory = false) =>
            Task.FromResult<IReadOnlyList<AdbDevice>>(Array.Empty<AdbDevice>());

        public Task<string> GetStateAsync(string serial, bool logToHistory = false, CancellationToken cancellationToken = default) =>
            Task.FromResult("recovery");

        public Task<string> ShellAsync(string serial, string command, CancellationToken cancellationToken = default, bool logToHistory = true) =>
            Task.FromResult(command.Trim() == "id"
                ? _root ? "uid=0(root) gid=0(root)" : "uid=2000(shell) gid=2000(shell)"
                : ScriptOutput);

        public Task<string?> GetPropAsync(string serial, string propertyName, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<ProcessResult> PushAsync(
            string serial,
            string localPath,
            string remotePath,
            Action<ProcessOutputLine>? onOutput = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProcessResult> PullAsync(
            string serial,
            string remotePath,
            string localPath,
            Action<ProcessOutputLine>? onOutput = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<ProcessResult> ExecOutToFileAsync(
            string serial,
            string command,
            string localPath,
            Action<long>? onBytesWritten = null,
            Action<ProcessOutputLine>? onOutput = null,
            CancellationToken cancellationToken = default)
        {
            LastCommand = command;
            var marker = "/dev/block/by-name/";
            var start = command.IndexOf(marker, StringComparison.Ordinal);
            var name = start < 0
                ? "unknown"
                : command[(start + marker.Length)..].Split(' ', 2)[0].Trim();
            var size = name switch
            {
                "boot" => 4096,
                "system" => 8192,
                "vendor" => 16384,
                "userdata" => 16384,
                "super" => 32768,
                _ => 1024
            };
            var bytes = new byte[size];
            if (AppendStatusToOutput)
            {
                bytes = [.. bytes, .. System.Text.Encoding.ASCII.GetBytes("0+1 records in\n0+1 records out\n")];
            }

            await File.WriteAllBytesAsync(localPath, bytes, cancellationToken);
            onBytesWritten?.Invoke(bytes.Length);
            return new ProcessResult
            {
                ExecutablePath = "adb",
                Arguments = ["exec-out", command],
                ExitCode = 0
            };
        }

        public Task<ProcessResult> RebootAsync(string serial, string? target = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProcessResult> ExecuteRawAsync(
            string? serial,
            IReadOnlyList<string> arguments,
            Action<ProcessOutputLine>? onOutput = null,
            CancellationToken cancellationToken = default,
            bool logToHistory = true,
            TimeSpan? timeout = null)
        {
            var command = arguments.Count >= 2 && arguments[0] == "shell"
                ? arguments[1]
                : string.Empty;
            var output = command.Trim() == "id"
                ? _root ? "uid=0(root) gid=0(root)" : "uid=2000(shell) gid=2000(shell)"
                : ScriptOutput;
            return Task.FromResult(new ProcessResult
            {
                ExecutablePath = "adb",
                Arguments = arguments,
                ExitCode = 0,
                StandardOutput = output
            });
        }

        public Task<ProcessResult> StartServerAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FlashKitBackupTests", Guid.NewGuid().ToString("N"));
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
                // 测试清理失败不掩盖断言结果。
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
