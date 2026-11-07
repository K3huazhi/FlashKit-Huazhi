using FlashKit.Core.Models;
using FlashKit.Services.Images;
using Xunit;

namespace FlashKit.Tests;

public sealed class ImageToolsTests
{
    [Fact]
    public async Task SparseRawRoundTrip_PreservesBytes()
    {
        using var temp = new TempDirectory();
        var raw = Path.Combine(temp.Path, "raw.img");
        var sparse = Path.Combine(temp.Path, "raw.sparse.img");
        var back = Path.Combine(temp.Path, "back.img");
        var data = new byte[1024 * 1024 + 123];
        Random.Shared.NextBytes(data);
        await File.WriteAllBytesAsync(raw, data);

        var service = new ImageToolService();
        var toSparse = await service.ConvertAsync(new ImageConversionOptions
        {
            SourcePath = raw,
            OutputPath = sparse,
            TargetFormat = ImageFormatKind.Sparse
        });
        Assert.Equal(ImageFormatKind.Sparse, (await service.InspectAsync(sparse)).Format);

        var toRaw = await service.ConvertAsync(new ImageConversionOptions
        {
            SourcePath = sparse,
            OutputPath = back,
            TargetFormat = ImageFormatKind.Raw
        });
        Assert.Equal(ImageFormatKind.Raw, (await service.InspectAsync(back)).Format);
        Assert.True(toRaw.SizeBytes >= data.Length);
        var restored = await File.ReadAllBytesAsync(back);
        Assert.Equal(data, restored.Take(data.Length).ToArray());
        Assert.All(restored.Skip(data.Length), b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Hash_MatchesExpectedSha256()
    {
        using var temp = new TempDirectory();
        var file = Path.Combine(temp.Path, "hash.img");
        await File.WriteAllBytesAsync(file, "flashkit-image"u8.ToArray());
        var service = new ImageToolService();
        var first = await service.HashAsync(file, null);
        var second = await service.HashAsync(file, first.Sha256);
        Assert.True(second.MatchesExpected);
        Assert.Equal(first.Sha256, second.Sha256);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FlashKitImageTests", Guid.NewGuid().ToString("N"));
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
