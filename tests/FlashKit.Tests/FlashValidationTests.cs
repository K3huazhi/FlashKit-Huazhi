using FlashKit.Core.Models;
using FlashKit.Services.Flash;
using Xunit;

namespace FlashKit.Tests;

public sealed class FlashValidationTests
{
    [Fact]
    public async Task ImageLargerThanPartition_IsWarningOnly()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "boot.img");
        await File.WriteAllBytesAsync(file, new byte[8192]);
        var result = await new FlashValidationService().ValidateAsync(
            Item(file, "boot", 8192, false, 8192),
            Catalog("boot", 4096),
            Options(),
            fastbootUserspace: true);
        Assert.True(result.IsValid);
        Assert.Contains(result.Issues, i => !i.IsBlocking && i.Message.Contains("超过"));
    }

    [Fact]
    public async Task SparseExpandedSize_IsUsedForCapacityWarning()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "boot.img");
        var header = new byte[28];
        new byte[] { 0x3A, 0xFF, 0x26, 0xED }.CopyTo(header, 0);
        BitConverter.GetBytes((ushort)1).CopyTo(header, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(header, 6);
        BitConverter.GetBytes((ushort)28).CopyTo(header, 8);
        BitConverter.GetBytes((ushort)12).CopyTo(header, 10);
        BitConverter.GetBytes((uint)4096).CopyTo(header, 12);
        BitConverter.GetBytes((uint)2).CopyTo(header, 16);
        BitConverter.GetBytes((uint)1).CopyTo(header, 20);
        await File.WriteAllBytesAsync(file, header);

        var sparse = ImageFormats.Inspect(file);
        Assert.True(sparse.IsSparse);
        Assert.Equal(8192, sparse.ExpandedSizeBytes);

        var result = await new FlashValidationService().ValidateAsync(
            Item(file, "boot", header.Length, true, sparse.ExpandedSizeBytes),
            Catalog("boot", 4096),
            Options(),
            fastbootUserspace: true);
        Assert.True(result.IsValid);
        Assert.Contains(result.Issues, i => !i.IsBlocking && (i.Message.Contains("8192") || i.Message.Contains("8 KB")));
    }

    [Fact]
    public async Task DynamicPartition_OutsideFastbootd_IsWarningOnly()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "system.img");
        await File.WriteAllBytesAsync(file, new byte[4096]);
        var result = await new FlashValidationService().ValidateAsync(
            Item(file, "system", 4096, false, 4096),
            Catalog("system", 8192),
            Options(),
            fastbootUserspace: false);
        Assert.True(result.IsValid);
        Assert.Contains(result.Issues, i => !i.IsBlocking && i.Message.Contains("Fastbootd"));
    }

    [Fact]
    public async Task ValidImage_PassesWithWarningsOnly()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "boot.img");
        await File.WriteAllBytesAsync(file, new byte[4096]);
        var result = await new FlashValidationService().ValidateAsync(
            Item(file, "boot", 4096, false, 4096),
            Catalog("boot", 4096, "raven"),
            Options("raven"),
            fastbootUserspace: true);
        Assert.True(result.IsValid);
        Assert.DoesNotContain(result.Issues, i => i.IsBlocking);
    }

    [Fact]
    public async Task CodenameMismatch_IsBlocked()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "boot.img");
        await File.WriteAllBytesAsync(file, new byte[4096]);
        var result = await new FlashValidationService().ValidateAsync(
            Item(file, "boot", 4096, false, 4096),
            Catalog("boot", 4096, "panther"),
            Options("raven"),
            fastbootUserspace: true);
        Assert.True(result.IsValid);
        Assert.Contains(result.Issues, i => !i.IsBlocking && i.Message.Contains("代号提示不一致"));
    }

    [Fact]
    public async Task UnknownPartition_IsWarningOnly()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "custom.img");
        await File.WriteAllBytesAsync(file, new byte[4096]);
        var result = await new FlashValidationService().ValidateAsync(
            Item(file, "custom_partition", 4096, false, 4096),
            Catalog("boot", 4096),
            Options(),
            fastbootUserspace: true);
        Assert.True(result.IsValid);
        Assert.Contains(result.Issues, i => !i.IsBlocking && i.Message.Contains("仍会按名称"));
    }

    [Fact]
    public async Task InvalidPartitionName_IsBlocked()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "boot.img");
        await File.WriteAllBytesAsync(file, new byte[4096]);
        var result = await new FlashValidationService().ValidateAsync(
            Item(file, "../boot", 4096, false, 4096),
            Catalog("boot", 8192),
            Options(),
            fastbootUserspace: true);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.IsBlocking && i.Message.Contains("非法字符"));
    }

    private static FlashItem Item(string path, string partition, long size, bool sparse, long? expanded) => new()
    {
        PartitionName = partition,
        FilePath = path,
        FileSize = size,
        IsSparse = sparse,
        ExpandedSizeBytes = expanded,
        PartitionSizeBytes = 8192,
        Risk = PartitionRules.GetRisk(partition)
    };

    private static PartitionCatalog Catalog(string name, long size, string? codename = "raven") => new()
    {
        Partitions = [new PartitionInfo
        {
            Name = name,
            SizeBytes = size,
            Source = PartitionSource.FastbootGetVar,
            Risk = PartitionRules.GetRisk(name),
            IsDynamic = PartitionRules.IsDynamic(name)
        }],
        Source = PartitionSource.FastbootGetVar,
        HasSizes = true,
        DeviceCodename = codename,
        IsUserspace = true
    };

    private static FlashRunOptions Options(string? codename = null) => new()
    {
        Serial = "ABC123",
        ExpectedCodename = codename,
        VerifyDeviceCodename = codename is not null,
        AllowUnknownPartitionSizes = false,
        AllowCriticalPartitions = true
    };

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flashkit-flash-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
