namespace FlashKit.Services.Payload;

internal enum PayloadOperationType
{
    Replace = 0,
    ReplaceBz = 1,
    Move = 2,
    Bsdiff = 3,
    SourceCopy = 4,
    SourceBsdiff = 5,
    Zero = 6,
    Discard = 7,
    ReplaceXz = 8,
    Puffdiff = 9,
    BrotliBsdiff = 10,
    Zucchini = 11,
    Lz4diffBsdiff = 12,
    Lz4diffPuffdiff = 13,
    ReplaceZstd = 14,
    Unknown = 999
}

internal sealed class PayloadManifestInfo
{
    public uint BlockSize { get; set; } = 4096;
    public uint MinorVersion { get; set; }
    public bool PartialUpdate { get; set; }
    public List<PayloadPartitionUpdate> Partitions { get; } = new();
}

internal sealed class PayloadPartitionUpdate
{
    public string Name { get; set; } = string.Empty;
    public ulong? NewSize { get; set; }
    public byte[]? NewHash { get; set; }
    public string? NewHashHex => NewHash is { Length: > 0 } ? Convert.ToHexString(NewHash).ToLowerInvariant() : null;
    public List<PayloadInstallOperation> Operations { get; } = new();
}

internal sealed class PayloadInstallOperation
{
    public PayloadOperationType Type { get; set; }
    public ulong DataOffset { get; set; }
    public ulong DataLength { get; set; }
    public List<PayloadExtent> DestinationExtents { get; } = new();
}

internal readonly record struct PayloadExtent(ulong StartBlock, ulong NumBlocks);

internal sealed record PayloadHeader(
    ulong Version,
    ulong ManifestSize,
    uint MetadataSignatureSize,
    long DataStart,
    long FileLength);
