using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using FlashKit.Services.Adb;
using FlashKit.Services.Backup;
using FlashKit.Services.Fastboot;
using FlashKit.Services.Logging;
using FlashKit.Services.Process;
using Xunit;

namespace FlashKit.Tests;

/// <summary>
/// 只有显式设置环境变量才运行的真实设备探针。默认不触碰设备。
/// FLASHKIT_REAL_SERIAL=<real-device-serial>
/// FLASHKIT_REAL_ADB=...\adb.exe
/// FLASHKIT_ALLOW_WRITE=1 时才会执行同镜像回写。
/// </summary>
public sealed class RealDeviceProbeTests
{
    [Fact]
    public async Task RealDevice_FileManager_ListAndPull()
    {
        var serial = Environment.GetEnvironmentVariable("FLASHKIT_REAL_SERIAL");
        if (string.IsNullOrWhiteSpace(serial) || Environment.GetEnvironmentVariable("FLASHKIT_ALLOW_WRITE") != "1")
        {
            return;
        }

        var adbPath = Environment.GetEnvironmentVariable("FLASHKIT_REAL_ADB");
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
        {
            return;
        }

        var logging = new LoggingService();
        var runner = new ProcessRunner(logging);
        var adb = new AdbClient(runner, new ToolchainStub(adbPath), logging);
        var files = new FlashKit.Services.Files.DeviceFileService(adb, logging);
        var items = await files.ListAsync(serial, "/sdcard");
        Assert.NotEmpty(items);

        using var temp = new TempDirectory();
        var local = Path.Combine(temp.Path, "probe.txt");
        await adb.ShellAsync(serial, "echo flashkit-probe > /data/local/tmp/flashkit-probe.txt");
        var result = await files.PullAsync(serial, "/data/local/tmp/flashkit-probe.txt", local);
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(local));
        Assert.Contains("flashkit-probe", await File.ReadAllTextAsync(local));

        var upload = Path.Combine(temp.Path, "upload.txt");
        await File.WriteAllTextAsync(upload, "flashkit-upload");
        var push = await files.PushAsync(serial, upload, "/data/local/tmp/flashkit-upload.txt");
        Assert.Equal(0, push.ExitCode);
        var verify = Path.Combine(temp.Path, "verify.txt");
        var pull = await files.PullAsync(serial, "/data/local/tmp/flashkit-upload.txt", verify);
        Assert.Equal(0, pull.ExitCode);
        Assert.Equal("flashkit-upload", await File.ReadAllTextAsync(verify));
        await adb.ShellAsync(serial, "rm -f /data/local/tmp/flashkit-upload.txt");
    }

    [Fact]
    public async Task RealDevice_ErofsUnpack_ViaDevice()
    {
        var serial = Environment.GetEnvironmentVariable("FLASHKIT_REAL_SERIAL");
        var image = Environment.GetEnvironmentVariable("FLASHKIT_EROFS_IMAGE");
        if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(image) || !File.Exists(image))
        {
            return;
        }

        var adbPath = Environment.GetEnvironmentVariable("FLASHKIT_REAL_ADB");
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
        {
            return;
        }

        var logging = new LoggingService();
        var runner = new ProcessRunner(logging);
        var adb = new AdbClient(runner, new ToolchainStub(adbPath), logging);
        var imageTools = new FlashKit.Services.Images.ImageToolService();
        var files = new FlashKit.Services.FileSystems.FileSystemService(adb, imageTools, runner, logging);
        var capabilities = await files.CheckCapabilitiesAsync(serial);
        Assert.True(capabilities.ErofsUnpack);

        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "unpacked");
        var result = await files.UnpackAsync(new FileSystemUnpackRequest
        {
            Serial = serial,
            SourcePath = image,
            OutputDirectory = output
        });
        Assert.True(result.FileCount > 0);
        Assert.True(Directory.Exists(result.FilesDirectory));
        Assert.True(File.Exists(result.ManifestPath));
    }

    [Fact]
    public async Task RealDevice_ErofsPackRoundTrip()
    {
        var serial = Environment.GetEnvironmentVariable("FLASHKIT_REAL_SERIAL");
        if (string.IsNullOrWhiteSpace(serial) ||
            Environment.GetEnvironmentVariable("FLASHKIT_ALLOW_WRITE") != "1")
        {
            return;
        }

        var adbPath = Environment.GetEnvironmentVariable("FLASHKIT_REAL_ADB");
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
        {
            return;
        }

        var logging = new LoggingService();
        var runner = new ProcessRunner(logging);
        var adb = new AdbClient(runner, new ToolchainStub(adbPath), logging);
        var imageTools = new FlashKit.Services.Images.ImageToolService();
        var files = new FlashKit.Services.FileSystems.FileSystemService(adb, imageTools, runner, logging);
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(Path.Combine(source, "etc"));
        await File.WriteAllTextAsync(Path.Combine(source, "etc", "probe.txt"), "flashkit-erofs");
        var image = Path.Combine(temp.Path, "packed.erofs");

        var packed = await files.PackAsync(new FileSystemPackRequest
        {
            Serial = serial,
            SourceDirectory = source,
            OutputImagePath = image,
            Format = FileSystemFormat.Erofs
        });
        Assert.True(File.Exists(packed));
        Assert.True(new FileInfo(packed).Length > 0);

        var unpacked = await files.UnpackAsync(new FileSystemUnpackRequest
        {
            Serial = serial,
            SourcePath = packed,
            OutputDirectory = Path.Combine(temp.Path, "unpacked")
        });
        Assert.True(unpacked.FileCount > 0);
        Assert.True(Directory.Exists(unpacked.FilesDirectory));
    }

    [Fact]
    public async Task RealDevice_F2fsPackRoundTrip()
    {
        var serial = Environment.GetEnvironmentVariable("FLASHKIT_REAL_SERIAL");
        if (string.IsNullOrWhiteSpace(serial) ||
            Environment.GetEnvironmentVariable("FLASHKIT_ALLOW_WRITE") != "1")
        {
            return;
        }

        var adbPath = Environment.GetEnvironmentVariable("FLASHKIT_REAL_ADB");
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
        {
            return;
        }

        var logging = new LoggingService();
        var runner = new ProcessRunner(logging);
        var adb = new AdbClient(runner, new ToolchainStub(adbPath), logging);
        var imageTools = new FlashKit.Services.Images.ImageToolService();
        var files = new FlashKit.Services.FileSystems.FileSystemService(adb, imageTools, runner, logging);
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(Path.Combine(source, "etc"));
        await File.WriteAllTextAsync(Path.Combine(source, "etc", "probe.txt"), "flashkit-f2fs");
        var image = Path.Combine(temp.Path, "packed.img");

        var packed = await files.PackAsync(new FileSystemPackRequest
        {
            Serial = serial,
            SourceDirectory = source,
            OutputImagePath = image,
            Format = FileSystemFormat.F2fs,
            PartitionSizeBytes = 64L * 1024 * 1024
        });
        Assert.True(new FileInfo(packed).Length > 0);
        Assert.Equal(ImageFormatKind.F2fs, (await imageTools.InspectAsync(packed)).Format);

        var unpacked = await files.UnpackAsync(new FileSystemUnpackRequest
        {
            Serial = serial,
            SourcePath = packed,
            OutputDirectory = Path.Combine(temp.Path, "unpacked")
        });
        Assert.True(unpacked.FileCount > 0);
    }

    [Fact]
    public async Task RealDevice_Ext4Pack()
    {
        var serial = Environment.GetEnvironmentVariable("FLASHKIT_REAL_SERIAL");
        if (string.IsNullOrWhiteSpace(serial) ||
            Environment.GetEnvironmentVariable("FLASHKIT_ALLOW_WRITE") != "1")
        {
            return;
        }

        var adbPath = Environment.GetEnvironmentVariable("FLASHKIT_REAL_ADB");
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
        {
            return;
        }

        var logging = new LoggingService();
        var runner = new ProcessRunner(logging);
        var adb = new AdbClient(runner, new ToolchainStub(adbPath), logging);
        var imageTools = new FlashKit.Services.Images.ImageToolService();
        var files = new FlashKit.Services.FileSystems.FileSystemService(adb, imageTools, runner, logging);
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(Path.Combine(source, "etc"));
        await File.WriteAllTextAsync(Path.Combine(source, "etc", "probe.txt"), "flashkit-ext4");
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "metadata.jsonl"),
            "{\"relativePath\":\"/etc/probe.txt\",\"kind\":\"file\",\"mode\":\"100600\",\"uid\":1000,\"gid\":2000,\"sizeBytes\":13}\n");
        var image = Path.Combine(temp.Path, "packed.img");

        var packed = await files.PackAsync(new FileSystemPackRequest
        {
            Serial = serial,
            SourceDirectory = source,
            OutputImagePath = image,
            Format = FileSystemFormat.Ext4,
            PartitionSizeBytes = 64L * 1024 * 1024
        });
        Assert.True(new FileInfo(packed).Length > 0);
        Assert.Equal(ImageFormatKind.Ext4, (await imageTools.InspectAsync(packed)).Format);

        var unpacked = await files.UnpackAsync(new FileSystemUnpackRequest
        {
            Serial = serial,
            SourcePath = packed,
            OutputDirectory = Path.Combine(temp.Path, "unpacked")
        });
        Assert.True(unpacked.FileCount > 0);
        Assert.True(File.Exists(Path.Combine(unpacked.FilesDirectory, "etc", "probe.txt")));
        var metadata = await File.ReadAllTextAsync(Path.Combine(unpacked.OutputDirectory, "metadata.jsonl"));
        Assert.Contains("etc/probe.txt", metadata);
        Assert.Contains("\"mode\"", metadata, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealDevice_Restore_SameImage()
    {
        var serial = Environment.GetEnvironmentVariable("FLASHKIT_REAL_SERIAL");
        var manifest = Environment.GetEnvironmentVariable("FLASHKIT_RESTORE_MANIFEST");
        if (string.IsNullOrWhiteSpace(serial) ||
            string.IsNullOrWhiteSpace(manifest) ||
            Environment.GetEnvironmentVariable("FLASHKIT_ALLOW_WRITE") != "1")
        {
            return;
        }

        var adbPath = Environment.GetEnvironmentVariable("FLASHKIT_REAL_ADB");
        if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
        {
            return;
        }

        var logging = new LoggingService();
        var runner = new ProcessRunner(logging);
        var toolchain = new ToolchainStub(adbPath);
        var adb = new AdbClient(runner, toolchain, logging);
        var fastboot = new FastbootClient(runner, toolchain);
        var restore = new RestoreService(adb, fastboot, logging);
        var parsed = await restore.ReadManifestAsync(manifest);
        var first = parsed.Images[0];
        var result = await restore.RestoreAsync(new RestoreRequest
        {
            ManifestPath = manifest,
            Serial = serial,
            DeviceMode = DeviceMode.System,
            SelectedPartitions = [first.PartitionName]
        });
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(0, result.FailureCount);
    }

    private sealed class ToolchainStub : IToolchainService
    {
        public ToolchainStub(string adbPath)
        {
            AdbPath = adbPath;
            ToolsDirectory = Path.GetDirectoryName(adbPath) ?? string.Empty;
        }

        public string ToolsDirectory { get; }
        public string AdbPath { get; }
        public string FastbootPath => Path.Combine(ToolsDirectory, "fastboot.exe");
        public ToolchainReport? LastReport => null;
        public Task<ToolchainReport> CheckAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ToolchainReport { Tools = Array.Empty<ToolInfoItem>() });
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FlashKitRealProbe", Guid.NewGuid().ToString("N"));
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
}

