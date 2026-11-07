namespace FlashKit.Services.Packages;

/// <summary>
/// .dat.br / .new.dat（OTA 增量数据流）预留接口。
/// 首版不实现解包，只保留清晰的扩展点：后续可在这里接入 bspatch 与 dat 解析，
/// PackageExtractor 只需把分派目标指向此类即可。
/// </summary>
internal sealed class DatBrExtractor
{
    public Task ExtractAsync(
        string sourcePath,
        string outputDirectory,
        IProgress<Core.Models.OperationProgress>? progress = null,
        Action<string>? logLine = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            ".dat.br / .new.dat 解包将在后续版本提供。当前版本请使用 payload.bin 或纯 img ZIP。" +
            $"（输入：{Path.GetFileName(sourcePath)}）");
    }
}
