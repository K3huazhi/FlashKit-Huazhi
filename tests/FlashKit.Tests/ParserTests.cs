using FlashKit.Core.Parsing;
using Xunit;

namespace FlashKit.Tests;

public sealed class ParserTests
{
    [Fact]
    public void Battery_ParsesAndClamps()
    {
        var result = DeviceOutputParser.ParseBattery("""
            Current Battery Service state:
              AC powered: false
              USB powered: true
              status: 2
              present: true
              level: 101
              voltage: 4321
              temperature: 325
            """);

        Assert.Equal(100, result.Level);
        Assert.Equal("充电中", result.Status);
        Assert.Equal(33, result.TemperatureCelsius);
        Assert.Equal(4321, result.VoltageMillivolts);
    }

    [Fact]
    public void Battery_MissingFieldsDoesNotThrow()
    {
        var result = DeviceOutputParser.ParseBattery("vendor garbage\nnot-a-key");
        Assert.Null(result.Level);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void MemInfo_UsesAvailableAndFallback()
    {
        var direct = DeviceOutputParser.ParseMemInfo("MemTotal: 8192000 kB\nMemAvailable: 4096000 kB\n");
        Assert.Equal(8192000L * 1024, direct.TotalBytes);
        Assert.Equal(4096000L * 1024, direct.AvailableBytes);

        var fallback = DeviceOutputParser.ParseMemInfo("MemTotal: 1000 kB\nMemFree: 100 kB\nBuffers: 50 kB\nCached: 150 kB\n");
        Assert.Equal(300L * 1024, fallback.AvailableBytes);
        Assert.Contains(fallback.Warnings, w => w.Contains("MemAvailable"));
    }

    [Fact]
    public void Df_SelectsDataMountAndHandlesHumanUnits()
    {
        var output = """
            Filesystem     1K-blocks     Used Available Use% Mounted on
            tmpfs             204800        0    204800   0% /dev
            /dev/block/dm-6 112640000 12000000 100640000  11% /data
            /dev/block/dm-7  10240000  1024000   9216000  10% /storage/emulated
            """;
        var result = DeviceOutputParser.ParseDf(output);
        Assert.Equal("/data", result.MountPoint);
        Assert.Equal(112640000L * 1024, result.TotalBytes);

        var human = DeviceOutputParser.ParseDf("""
            Filesystem Size Used Avail Use% Mounted on
            /dev/block/dm-6 108G 12G 96G 12% /data
            """);
        Assert.Equal("/data", human.MountPoint);
        Assert.NotNull(human.TotalBytes);
        Assert.True(human.TotalBytes > 100L * 1024 * 1024 * 1024);
    }

    [Fact]
    public void GetProp_ParsesBracketsAndValuesWithBrackets()
    {
        var props = DeviceOutputParser.ParseGetProp("""
            [ro.product.model]: [Pixel 7]
            [ro.product.device]: [panther]
            [ro.build.version.release]: [14]
            [weird.value]: [a]b]
            """);
        Assert.Equal("Pixel 7", props["ro.product.model"]);
        Assert.Equal("14", props["ro.build.version.release"]);
        Assert.Contains("a", props["weird.value"]);
    }

    [Fact]
    public void FastbootGetVar_ParsesPartitionSizeKeys()
    {
        var vars = DeviceOutputParser.ParseFastbootGetVarAll("""
            (bootloader) partition-size:boot: 0x0000000001000000
            (bootloader) partition-size:vendor_boot: 0x0000000001800000
            (bootloader) is-userspace: yes
            (bootloader) product: raven
            (bootloader) Finished. Total time: 0.010s
            """);

        Assert.Equal("0x0000000001000000", vars["partition-size:boot"]);
        Assert.Equal("yes", vars["is-userspace"]);
        Assert.Equal("raven", vars["product"]);
    }

    [Fact]
    public void PartitionMatcher_PrefersExactName()
    {
        var partitions = new[] { "boot", "vendor_boot", "boot_a", "boot_b" };
        Assert.Equal("vendor_boot", PartitionMatcher.InferPartitionName("vendor_boot.img", partitions));
        Assert.Equal("boot_a", PartitionMatcher.InferPartitionName("boot_a.img", partitions));
        Assert.Null(PartitionMatcher.InferPartitionName("magisk_patched.img", partitions));
    }

    [Fact]
    public void CommandLineTokenizer_RejectsUnclosedQuote()
    {
        Assert.Throws<FormatException>(() => CommandLineTokenizer.Tokenize("adb shell \"unterminated"));
        var tokens = CommandLineTokenizer.Tokenize("adb -s ABC shell 'echo hello world'");
        Assert.Equal(["adb", "-s", "ABC", "shell", "echo hello world"], tokens);
    }
}
