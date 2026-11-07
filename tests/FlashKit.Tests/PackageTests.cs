using System.IO.Compression;
using System.Text;
using FlashKit.Core.Models;
using FlashKit.Services.Logging;
using FlashKit.Services.Packages;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using Xunit;

namespace FlashKit.Tests;

public sealed class PackageTests
{
    private static readonly byte[] XzZeros = Convert.FromBase64String(
        "/Td6WFoAAATm1rRGAgAhARYAAAB0L+Wj4B//ACddAABv/f//o7f/Rz5IFXI5YVG4kijmo4YH+e7kHoLTL8U6PAFFmN5/AAAAOt2moach4McAAUOAQAAAALBEa2CxxGf7AgAAAAAEWVo=");

    [Fact]
    public async Task Payload_ExtractsRawImages_AndManifest()
    {
        using var temp = new TempDirectory();
        var payloadPath = Path.Combine(temp.Path, "payload.bin");
        var boot = Pattern(8192, 0x11);
        var dtbo = Pattern(8192, 0x22);
        var vendorBoot = new byte[8192];
        File.WriteAllBytes(payloadPath, BuildPayload(
            minorVersion: 0,
            partialUpdate: false,
            [
                new PartitionSpec("boot", [new OpSpec(0, boot, 0, 2)]),
                new PartitionSpec("dtbo", [new OpSpec(1, Bzip2(dtbo), 0, 2)]),
                new PartitionSpec("vendor_boot", [new OpSpec(8, XzZeros, 0, 2)]),
                new PartitionSpec("misc", [new OpSpec(6, [], 0, 1)])
            ]));

        var extractor = new PackageExtractor(new LoggingService());
        var result = await extractor.ExtractAsync(payloadPath);

        Assert.Equal(4, result.ImageCount);
        AssertReads(Path.Combine(result.OutputDirectory, "images", "boot.img"), boot);
        AssertReads(Path.Combine(result.OutputDirectory, "images", "dtbo.img"), dtbo);
        AssertReads(Path.Combine(result.OutputDirectory, "images", "vendor_boot.img"), vendorBoot);
        AssertReads(Path.Combine(result.OutputDirectory, "images", "misc.img"), new byte[4096]);
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory, "manifest.json")));

        var manifest = await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "manifest.json"));
        Assert.Contains("\"toolVersion\"", manifest);
        Assert.Contains("REPLACE_XZ", manifest);
        Assert.Contains("payloadBlockSize", manifest);
    }

    [Fact]
    public async Task Payload_Incremental_FailsClearly_AndCleansOutput()
    {
        using var temp = new TempDirectory();
        var payloadPath = Path.Combine(temp.Path, "incremental.bin");
        File.WriteAllBytes(payloadPath, BuildPayload(
            minorVersion: 1,
            partialUpdate: true,
            [new PartitionSpec("system", [new OpSpec(4, [], 0, 1)])]));

        var extractor = new PackageExtractor(new LoggingService());
        var ex = await Assert.ThrowsAsync<IncrementalOtaException>(() => extractor.ExtractAsync(payloadPath));
        Assert.Contains("增量", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "incremental")));
    }

    [Fact]
    public async Task Payload_PartialButSelfContained_ExtractsWithWarning()
    {
        using var temp = new TempDirectory();
        var payloadPath = Path.Combine(temp.Path, "partial-self-contained.bin");
        var boot = Pattern(4096, 0x66);
        File.WriteAllBytes(payloadPath, BuildPayload(
            minorVersion: 7,
            partialUpdate: true,
            [new PartitionSpec("boot", [new OpSpec(0, boot, 0, 1)])]));

        var extractor = new PackageExtractor(new LoggingService());
        var result = await extractor.ExtractAsync(payloadPath);

        AssertReads(Path.Combine(result.OutputDirectory, "images", "boot.img"), boot);
        Assert.Contains(result.Warnings, w => w.Contains("partial_update"));
        Assert.Contains("payloadMinorVersion", await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "manifest.json")));
    }

    [Fact]
    public async Task Payload_VerifiesNewPartitionInfoHash()
    {
        using var temp = new TempDirectory();
        var payloadPath = Path.Combine(temp.Path, "hash-verified.bin");
        var boot = Pattern(8192, 0x77);
        var hash = System.Security.Cryptography.SHA256.HashData(boot);
        File.WriteAllBytes(payloadPath, BuildPayload(
            minorVersion: 0,
            partialUpdate: false,
            [new PartitionSpec("boot", [new OpSpec(0, boot, 0, 2)], (ulong)boot.Length, hash)]));

        var extractor = new PackageExtractor(new LoggingService());
        var result = await extractor.ExtractAsync(payloadPath);
        Assert.Equal(Convert.ToHexString(hash).ToLowerInvariant(), result.Manifest.Images.Single().Sha256);
        AssertReads(Path.Combine(result.OutputDirectory, "images", "boot.img"), boot);
    }

    [Fact]
    public async Task Payload_ManifestHashMismatch_FailsAndCleans()
    {
        using var temp = new TempDirectory();
        var payloadPath = Path.Combine(temp.Path, "hash-mismatch.bin");
        var boot = Pattern(4096, 0x88);
        File.WriteAllBytes(payloadPath, BuildPayload(
            minorVersion: 0,
            partialUpdate: false,
            [new PartitionSpec("boot", [new OpSpec(0, boot, 0, 1)], 4096, new byte[32])]));

        var extractor = new PackageExtractor(new LoggingService());
        await Assert.ThrowsAsync<PackageFormatException>(() => extractor.ExtractAsync(payloadPath));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "hash-mismatch")));
    }

    [Fact]
    public async Task ZipWithPayload_PreservesMetaInf()
    {
        using var temp = new TempDirectory();
        var zipPath = Path.Combine(temp.Path, "ota.zip");
        var boot = Pattern(8192, 0x33);
        var payload = BuildPayload(0, false, [new PartitionSpec("boot", [new OpSpec(0, boot, 0, 2)])]);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var payloadEntry = zip.CreateEntry("payload.bin", CompressionLevel.NoCompression);
            await using (var stream = payloadEntry.Open())
            {
                await stream.WriteAsync(payload);
            }

            var script = zip.CreateEntry("META-INF/com/google/android/updater-script", CompressionLevel.Optimal);
            await using (var stream = script.Open())
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("ui_print(\"test\");\n"));
            }
        }

        var extractor = new PackageExtractor(new LoggingService());
        var result = await extractor.ExtractAsync(zipPath);

        Assert.True(File.Exists(Path.Combine(result.OutputDirectory, "images", "boot.img")));
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory, "META-INF", "com", "google", "android", "updater-script")));
        AssertReads(Path.Combine(result.OutputDirectory, "images", "boot.img"), boot);
    }

    [Fact]
    public async Task Packer_WritesRootImagesStoreAndScripts_ThenRoundTrips()
    {
        using var temp = new TempDirectory();
        var bootPath = Path.Combine(temp.Path, "boot.img");
        var dtboPath = Path.Combine(temp.Path, "dtbo.img");
        var boot = Pattern(4096, 0x44);
        var dtbo = Pattern(4096, 0x55);
        await File.WriteAllBytesAsync(bootPath, boot);
        await File.WriteAllBytesAsync(dtboPath, dtbo);

        var output = Path.Combine(temp.Path, "out.zip");
        var packer = new PackagePacker(new LoggingService());
        var result = await packer.PackAsync(
            [
                new PackImageInput { FilePath = bootPath, PartitionName = "boot" },
                new PackImageInput { FilePath = dtboPath, PartitionName = "dtbo" }
            ],
            output);

        Assert.Equal(2, result.ImageCount);
        using (var zip = ZipFile.OpenRead(output))
        {
            Assert.Equal(4, zip.Entries.Count);
            var bootEntry = zip.GetEntry("boot.img");
            var dtboEntry = zip.GetEntry("dtbo.img");
            Assert.NotNull(bootEntry);
            Assert.NotNull(dtboEntry);
            Assert.Equal(bootEntry!.Length, bootEntry.CompressedLength);
            Assert.Equal(dtboEntry!.Length, dtboEntry.CompressedLength);

            var updateBinary = ReadEntry(zip.GetEntry("META-INF/com/google/android/update-binary")!);
            var updaterScript = ReadEntry(zip.GetEntry("META-INF/com/google/android/updater-script")!);
            Assert.Contains("PARTITIONS=\"boot dtbo\"", updateBinary);
            Assert.Contains("package_extract_file(\"boot.img\"", updaterScript);
            Assert.Contains("package_extract_file(\"dtbo.img\"", updaterScript);
            Assert.Equal(0x81ED, (zip.GetEntry("META-INF/com/google/android/update-binary")!.ExternalAttributes >> 16) & 0xFFFF);
            Assert.Equal(0x81A4, (zip.GetEntry("boot.img")!.ExternalAttributes >> 16) & 0xFFFF);
        }

        var extractor = new PackageExtractor(new LoggingService());
        var unpacked = await extractor.ExtractAsync(output);
        AssertReads(Path.Combine(unpacked.OutputDirectory, "images", "boot.img"), boot);
        AssertReads(Path.Combine(unpacked.OutputDirectory, "images", "dtbo.img"), dtbo);
    }

    [Fact]
    public void Packer_RejectsDuplicatePartitions()
    {
        var packer = new PackagePacker(new LoggingService());
        var result = packer.Validate(
        [
            new PackImageInput { FilePath = "a.img", PartitionName = "boot" },
            new PackImageInput { FilePath = "b.img", PartitionName = "boot" }
        ]);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("重复"));
    }

    private static byte[] BuildPayload(uint minorVersion, bool partialUpdate, IReadOnlyList<PartitionSpec> partitions)
    {
        var blobs = new List<byte>();
        var partitionMessages = new List<byte[]>();
        foreach (var partition in partitions)
        {
            var ops = new List<byte[]>();
            foreach (var op in partition.Operations)
            {
                var offset = blobs.Count;
                blobs.AddRange(op.Data);
                var msg = new MemoryStream();
                WriteTag(msg, 1, 0);
                WriteVarint(msg, (ulong)op.Type);
                if (op.Data.Length > 0)
                {
                    WriteTag(msg, 2, 0);
                    WriteVarint(msg, (ulong)offset);
                    WriteTag(msg, 3, 0);
                    WriteVarint(msg, (ulong)op.Data.Length);
                }

                WriteTag(msg, 6, 2);
                var extent = new MemoryStream();
                WriteTag(extent, 1, 0);
                WriteVarint(extent, (ulong)op.StartBlock);
                WriteTag(extent, 2, 0);
                WriteVarint(extent, (ulong)op.BlockCount);
                WriteBytes(msg, extent.ToArray());
                ops.Add(msg.ToArray());
            }

            var part = new MemoryStream();
            WriteTag(part, 1, 2);
            WriteString(part, partition.Name);
            if (partition.NewSize is not null || partition.NewHash is not null)
            {
                var info = new MemoryStream();
                if (partition.NewSize is { } newSize)
                {
                    WriteTag(info, 1, 0);
                    WriteVarint(info, newSize);
                }

                if (partition.NewHash is { } newHash)
                {
                    WriteTag(info, 2, 2);
                    WriteBytes(info, newHash);
                }

                WriteTag(part, 7, 2);
                WriteBytes(part, info.ToArray());
            }

            foreach (var op in ops)
            {
                WriteTag(part, 8, 2);
                WriteBytes(part, op);
            }

            partitionMessages.Add(part.ToArray());
        }

        var manifest = new MemoryStream();
        WriteTag(manifest, 3, 0);
        WriteVarint(manifest, 4096);
        if (minorVersion > 0)
        {
            WriteTag(manifest, 12, 0);
            WriteVarint(manifest, minorVersion);
        }

        if (partialUpdate)
        {
            WriteTag(manifest, 16, 0);
            WriteVarint(manifest, 1);
        }

        foreach (var partition in partitionMessages)
        {
            WriteTag(manifest, 13, 2);
            WriteBytes(manifest, partition);
        }

        var manifestBytes = manifest.ToArray();
        using var payload = new MemoryStream();
        payload.Write("CrAU"u8);
        WriteUInt64BigEndian(payload, 2);
        WriteUInt64BigEndian(payload, (ulong)manifestBytes.Length);
        WriteUInt32BigEndian(payload, 0);
        payload.Write(manifestBytes);
        payload.Write(blobs.ToArray());
        return payload.ToArray();
    }

    private static void WriteTag(Stream stream, int field, int wire)
    {
        WriteVarint(stream, (ulong)((field << 3) | wire));
    }

    private static void WriteString(Stream stream, string value) => WriteBytes(stream, Encoding.UTF8.GetBytes(value));

    private static void WriteBytes(Stream stream, byte[] value)
    {
        WriteVarint(stream, (ulong)value.Length);
        stream.Write(value);
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static void WriteUInt64BigEndian(Stream stream, ulong value)
    {
        for (var i = 7; i >= 0; i--)
        {
            stream.WriteByte((byte)(value >> (i * 8)));
        }
    }

    private static void WriteUInt32BigEndian(Stream stream, uint value)
    {
        for (var i = 3; i >= 0; i--)
        {
            stream.WriteByte((byte)(value >> (i * 8)));
        }
    }

    private static byte[] Bzip2(byte[] data)
    {
        using var output = new MemoryStream();
        using (var bzip = new BZip2Stream(output, SharpCompress.Compressors.CompressionMode.Compress, decompressConcatenated: false))
        {
            bzip.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] Pattern(int length, byte value)
    {
        var data = new byte[length];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(value + i % 7);
        }

        return data;
    }

    private static void AssertReads(string path, byte[] expected)
    {
        Assert.True(File.Exists(path), path);
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed record PartitionSpec(
        string Name,
        IReadOnlyList<OpSpec> Operations,
        ulong? NewSize = null,
        byte[]? NewHash = null);
    private sealed record OpSpec(int Type, byte[] Data, int StartBlock, int BlockCount);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flashkit-tests-" + Guid.NewGuid().ToString("N"));
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
                // 测试清理失败不掩盖断言。
            }
        }
    }
}
