using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;

namespace FlashKit.Services.FileSystems;

/// <summary>
/// 文件系统层解包。当前明确支持：
/// - erofs：把镜像推到 /data/local/tmp，用设备上的 fsck.erofs --extract 解出文件树；
/// - raw/sparse/payload 封装层先由 ImageToolService 识别/解开。
/// ext4/f2fs 完整解包和 ext4/f2fs/erofs 回打包需要 Linux 工具链，未具备时明确拒绝，不做假实现。
/// </summary>
public sealed class FileSystemService : IFileSystemService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IAdbClient _adb;
    private readonly IImageToolService _imageTools;
    private readonly IProcessRunner _runner;
    private readonly ILoggingService _logging;

    public FileSystemService(IAdbClient adb, IImageToolService imageTools, IProcessRunner runner, ILoggingService logging)
    {
        _adb = adb;
        _imageTools = imageTools;
        _runner = runner;
        _logging = logging;
    }

    public async Task<FileSystemCapabilities> CheckCapabilitiesAsync(string serial, CancellationToken cancellationToken = default)
    {
        var script =
            "for c in fsck.erofs dump.erofs debugfs mke2fs e2fsdroid mkfs.erofs mkfs.f2fs make_f2fs sload_f2fs dump.f2fs; do " +
            "p=$(command -v $c 2>/dev/null); [ -n \"$p\" ] && echo \"$c=$p\"; done; true";
        var result = await _adb.ExecuteRawAsync(
            serial,
            ["shell", script],
            cancellationToken: cancellationToken,
            logToHistory: false,
            timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var tools = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2)[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sevenZip = FindSevenZip();
        return new FileSystemCapabilities
        {
            Serial = serial,
            ErofsUnpack = tools.Contains("fsck.erofs"),
            Ext4Unpack = sevenZip is not null || tools.Contains("debugfs"),
            Ext4Pack = tools.Contains("mke2fs"),
            F2fsUnpack = tools.Contains("dump.f2fs"),
            F2fsPack = tools.Contains("make_f2fs") && tools.Contains("sload_f2fs"),
            ErofsPack = tools.Contains("mkfs.erofs"),
            Toolchain = (tools.Count == 0 ? "设备上没有文件系统工具" : string.Join(", ", tools.OrderBy(t => t))) +
                        (sevenZip is null ? string.Empty : " + 7-Zip"),
            Warnings =
            [
                sevenZip is null
                    ? "ext4 解包需要 debugfs 或内置 7-Zip；当前均未找到。"
                    : "ext4 解包使用 7-Zip 只读展开，SELinux/UID/GID 元数据不保留。",
                "f2fs 解包需要 f2fs-tools；当前设备未提供。",
                "erofs 回打包需要 mkfs.erofs；当前设备未提供。",
                "文件树中的 SELinux/capabilities 元数据采集仍不完整。"
            ]
        };
    }

    public async Task<FileSystemUnpackResult> UnpackAsync(
        FileSystemUnpackRequest request,
        IProgress<OperationProgress>? progress = null,
        Action<string>? logLine = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.SourcePath))
        {
            throw new FileNotFoundException("镜像不存在。", request.SourcePath);
        }

        var capabilities = await CheckCapabilitiesAsync(request.Serial, cancellationToken).ConfigureAwait(false);
        var source = Path.GetFullPath(request.SourcePath);
        var sourceInfo = await _imageTools.InspectAsync(source, cancellationToken).ConfigureAwait(false);
        var temporarySource = (string?)null;
        try
        {
            if (sourceInfo.Format == ImageFormatKind.Sparse)
            {
                temporarySource = Path.Combine(Path.GetTempPath(), "FlashKit", "fs-" + Guid.NewGuid().ToString("N") + ".raw");
                Directory.CreateDirectory(Path.GetDirectoryName(temporarySource)!);
                logLine?.Invoke("sparse 封装：先展开为 raw…");
                await _imageTools.ConvertAsync(new ImageConversionOptions
                {
                    SourcePath = source,
                    OutputPath = temporarySource,
                    TargetFormat = ImageFormatKind.Raw
                }, progress, cancellationToken).ConfigureAwait(false);
                source = temporarySource;
                sourceInfo = await _imageTools.InspectAsync(source, cancellationToken).ConfigureAwait(false);
            }

            if (sourceInfo.Format == ImageFormatKind.Ext4)
            {
                return await UnpackExt4Async(request, source, sourceInfo, progress, logLine, cancellationToken).ConfigureAwait(false);
            }

            var isErofs = sourceInfo.Format == ImageFormatKind.Erofs;
            var isF2fs = sourceInfo.Format == ImageFormatKind.F2fs;
            if (!isErofs && !isF2fs)
            {
                throw new FileSystemToolchainException(
                    "当前输入不是 ext4/erofs/f2fs，且未实现该文件系统的解包。");
            }

            if (isErofs && !capabilities.ErofsUnpack)
            {
                throw new FileSystemToolchainException("当前设备未提供 fsck.erofs，无法解包 erofs。");
            }

            if (isF2fs && !capabilities.F2fsUnpack)
            {
                throw new FileSystemToolchainException("当前设备未提供 dump.f2fs，无法解包 f2fs。");
            }

            var output = string.IsNullOrWhiteSpace(request.OutputDirectory)
                ? Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + "_unpacked")
                : Path.GetFullPath(request.OutputDirectory);
            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            {
                throw new IOException("输出目录已存在且非空，请换一个目录。");
            }

            Directory.CreateDirectory(output);
            var filesDirectory = Path.Combine(output, "files");
            var remoteRoot = $"/data/local/tmp/flashkit-fs-{Guid.NewGuid():N}";
            var remoteSource = $"{remoteRoot}/source.img";
            var remoteOut = $"{remoteRoot}/out";
            try
            {
                var mk = await _adb.ExecuteRawAsync(
                    request.Serial,
                    ["shell", "mkdir", "-p", remoteRoot],
                    cancellationToken: cancellationToken,
                    logToHistory: false,
                    timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (mk.ExitCode != 0)
                {
                    throw new IOException("创建设备临时目录失败：" + mk.ToReadableError());
                }

                logLine?.Invoke("正在把镜像推送到设备临时目录…");
                var push = await _adb.PushAsync(request.Serial, source, remoteSource, line => logLine?.Invoke(line.Text), cancellationToken).ConfigureAwait(false);
                if (push.ExitCode != 0)
                {
                    throw new IOException("adb push 失败：" + push.ToReadableError());
                }

                var extractScript = isErofs
                    ? $"rm -rf {remoteOut}; mkdir -p {remoteOut}; " +
                      $"fsck.erofs --extract={remoteOut} --preserve --xattrs {remoteSource}; rc=$?; " +
                      "if [ $rc -ne 0 ]; then rm -rf " + remoteOut + "; mkdir -p " + remoteOut + "; " +
                      $"fsck.erofs --extract={remoteOut} --preserve --no-xattrs {remoteSource}; rc=$?; " +
                      "echo FLASHKIT_XATTR_FALLBACK=1; fi; " +
                      "echo \"FLASHKIT_RC=$rc\"; exit $rc"
                    : $"rm -rf {remoteOut}; mkdir -p {remoteOut}; " +
                      $"dump.f2fs -r -f -o {remoteOut} -P {remoteSource}; rc=$?; " +
                      "echo \"FLASHKIT_RC=$rc\"; exit $rc";
                logLine?.Invoke(isErofs ? "设备端 fsck.erofs 正在解包…" : "设备端 dump.f2fs 正在解包…");
                var extract = await _adb.ExecuteRawAsync(
                    request.Serial,
                    ["shell", extractScript],
                    cancellationToken: cancellationToken,
                    logToHistory: true,
                    timeout: TimeSpan.FromMinutes(30)).ConfigureAwait(false);
                if (extract.ExitCode != 0)
                {
                    throw new FileSystemToolchainException(
                        (isErofs ? "fsck.erofs 解包失败：" : "dump.f2fs 解包失败：") + extract.ToReadableError());
                }

                var warnings = capabilities.Warnings.ToList();
                if (extract.StandardOutput.Contains("FLASHKIT_XATTR_FALLBACK=1", StringComparison.Ordinal))
                {
                    warnings.Add("设备临时目录不支持 security.selinux xattr，已回退到 --no-xattrs；SELinux 上下文未随文件树保存。");
                }

                if (isF2fs)
                {
                    warnings.Add("dump.f2fs 保留 mode/owner/group，但当前未采集 SELinux context/capabilities。");
                }

                var metadata = await ReadMetadataAsync(request.Serial, remoteOut, cancellationToken).ConfigureAwait(false);
                logLine?.Invoke($"解出 {metadata.Count} 个文件系统条目，正在拉回电脑…");
                if (Directory.Exists(filesDirectory))
                {
                    Directory.Delete(filesDirectory, recursive: true);
                }

                var pull = await _adb.PullAsync(request.Serial, remoteOut, filesDirectory, null, cancellationToken).ConfigureAwait(false);
                if (pull.ExitCode != 0)
                {
                    throw new IOException("拉取解包目录失败：" + pull.ToReadableError());
                }

                var manifest = new
                {
                    manifestVersion = "1",
                    toolVersion = AppInfo.Version,
                    format = isErofs ? "erofs" : "f2fs",
                    source = source,
                    sourceImageSize = new FileInfo(source).Length,
                    blockSize = sourceInfo.BlockSize,
                    outputDirectory = output,
                    filesDirectory = "files",
                    fileCount = metadata.Count,
                    metadata,
                    toolchain = capabilities.Toolchain,
                    warnings,
                    createdAt = DateTimeOffset.Now
                };
                var manifestPath = Path.Combine(output, "manifest.json");
                await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(
                    Path.Combine(output, "metadata.jsonl"),
                    string.Join('\n', metadata.Select(m => JsonSerializer.Serialize(m, JsonOptions))),
                    cancellationToken).ConfigureAwait(false);

                return new FileSystemUnpackResult
                {
                    OutputDirectory = output,
                    FilesDirectory = filesDirectory,
                    Format = isErofs ? FileSystemFormat.Erofs : FileSystemFormat.F2fs,
                    FileCount = metadata.Count,
                    ManifestPath = manifestPath,
                    Warnings = warnings
                };
            }
            finally
            {
                try
                {
                    await _adb.ExecuteRawAsync(
                        request.Serial,
                        ["shell", "rm", "-rf", remoteRoot],
                        cancellationToken: CancellationToken.None,
                        logToHistory: false,
                        timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
        finally
        {
            if (temporarySource is not null && File.Exists(temporarySource))
            {
                try
                {
                    File.Delete(temporarySource);
                }
                catch
                {
                }
            }
        }
    }

    public async Task<string> PackAsync(
        FileSystemPackRequest request,
        IProgress<OperationProgress>? progress = null,
        Action<string>? logLine = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(request.SourceDirectory))
        {
            throw new DirectoryNotFoundException("源目录不存在：" + request.SourceDirectory);
        }

        var capabilities = await CheckCapabilitiesAsync(request.Serial, cancellationToken).ConfigureAwait(false);
        return request.Format switch
        {
            FileSystemFormat.Erofs when capabilities.ErofsPack =>
                await PackErofsAsync(request, progress, logLine, cancellationToken).ConfigureAwait(false),
            FileSystemFormat.F2fs when capabilities.F2fsPack =>
                await PackF2fsAsync(request, progress, logLine, cancellationToken).ConfigureAwait(false),
            FileSystemFormat.Ext4 when capabilities.Ext4Pack =>
                await PackExt4Async(request, progress, logLine, cancellationToken).ConfigureAwait(false),
            FileSystemFormat.Erofs => throw new FileSystemToolchainException("设备未提供 mkfs.erofs，无法回打包 erofs。"),
            FileSystemFormat.F2fs => throw new FileSystemToolchainException("设备未提供 make_f2fs/sload_f2fs，无法回打包 f2fs。"),
            FileSystemFormat.Ext4 => throw new FileSystemToolchainException("设备未提供 mke2fs/e2fsdroid，无法回打包 ext4。"),
            _ => throw new FileSystemToolchainException("当前只接入了 erofs/f2fs/ext4 回打包。")
        };
    }

    private async Task<string> PackF2fsAsync(
        FileSystemPackRequest request,
        IProgress<OperationProgress>? progress,
        Action<string>? logLine,
        CancellationToken cancellationToken)
    {
        var output = Path.GetFullPath(request.OutputImagePath);
        var parent = Path.GetDirectoryName(output);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var remoteRoot = $"/data/local/tmp/flashkit-pack-{Guid.NewGuid():N}";
        var remoteSource = $"{remoteRoot}/src";
        var remoteImage = $"{remoteRoot}/out.img";
        var size = ResolveImageSize(request);
        try
        {
            await PrepareRemoteAsync(request.Serial, remoteRoot, cancellationToken).ConfigureAwait(false);
            progress?.Report(new OperationProgress { Percentage = 10, Status = "推送 f2fs 源目录" });
            await PushSourceAsync(request, remoteSource, logLine, cancellationToken).ConfigureAwait(false);
            await ApplyMetadataAsync(request, remoteRoot, remoteSource, logLine, cancellationToken).ConfigureAwait(false);

            progress?.Report(new OperationProgress { Percentage = 45, Status = "设备端 make_f2fs" });
            var script =
                $"truncate -s {size} {remoteImage}; " +
                $"make_f2fs -f {remoteImage}; rc=$?; " +
                $"if [ $rc -eq 0 ]; then sload_f2fs -f {remoteSource} -t / -P {remoteImage}; rc=$?; fi; " +
                "echo \"FLASHKIT_RC=$rc\"; exit $rc";
            var build = await _adb.ExecuteRawAsync(
                request.Serial,
                ["shell", script],
                cancellationToken: cancellationToken,
                logToHistory: true,
                timeout: TimeSpan.FromMinutes(30)).ConfigureAwait(false);
            if (build.ExitCode != 0)
            {
                throw new FileSystemToolchainException("f2fs 打包失败：" + build.ToReadableError());
            }

            progress?.Report(new OperationProgress { Percentage = 85, Status = "拉回 f2fs 镜像" });
            await PullImageAsync(request.Serial, remoteImage, output, logLine, cancellationToken).ConfigureAwait(false);
            progress?.Report(new OperationProgress { Percentage = 100, Status = "f2fs 打包完成" });
            logLine?.Invoke("f2fs 打包完成；mode/owner/group 由 sload_f2fs -P 尽力保留，SELinux/capabilities 未注入。");
            return output;
        }
        finally
        {
            await CleanupRemoteAsync(request.Serial, remoteRoot).ConfigureAwait(false);
        }
    }

    private async Task<string> PackExt4Async(
        FileSystemPackRequest request,
        IProgress<OperationProgress>? progress,
        Action<string>? logLine,
        CancellationToken cancellationToken)
    {
        var output = Path.GetFullPath(request.OutputImagePath);
        var parent = Path.GetDirectoryName(output);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var remoteRoot = $"/data/local/tmp/flashkit-pack-{Guid.NewGuid():N}";
        var remoteSource = $"{remoteRoot}/src";
        var remoteImage = $"{remoteRoot}/out.img";
        var size = ResolveImageSize(request);
        try
        {
            await PrepareRemoteAsync(request.Serial, remoteRoot, cancellationToken).ConfigureAwait(false);
            progress?.Report(new OperationProgress { Percentage = 10, Status = "推送 ext4 源目录" });
            await PushSourceAsync(request, remoteSource, logLine, cancellationToken).ConfigureAwait(false);
            await ApplyMetadataAsync(request, remoteRoot, remoteSource, logLine, cancellationToken).ConfigureAwait(false);

            progress?.Report(new OperationProgress { Percentage = 45, Status = "设备端 mke2fs -d" });
            var script =
                $"truncate -s {size} {remoteImage}; " +
                $"mke2fs -t ext4 -b 4096 -F -d {remoteSource} {remoteImage}; rc=$?; " +
                "echo \"FLASHKIT_RC=$rc\"; exit $rc";
            var build = await _adb.ExecuteRawAsync(
                request.Serial,
                ["shell", script],
                cancellationToken: cancellationToken,
                logToHistory: true,
                timeout: TimeSpan.FromMinutes(30)).ConfigureAwait(false);
            if (build.ExitCode != 0)
            {
                throw new FileSystemToolchainException("ext4 打包失败：" + build.ToReadableError());
            }

            progress?.Report(new OperationProgress { Percentage = 85, Status = "拉回 ext4 镜像" });
            await PullImageAsync(request.Serial, remoteImage, output, logLine, cancellationToken).ConfigureAwait(false);
            progress?.Report(new OperationProgress { Percentage = 100, Status = "ext4 打包完成" });
            logLine?.Invoke("ext4 打包完成；mke2fs -d 保留目录内容，但未注入 fs_config/file_contexts，SELinux/capabilities 元数据不完整。");
            return output;
        }
        finally
        {
            await CleanupRemoteAsync(request.Serial, remoteRoot).ConfigureAwait(false);
        }
    }

    private static long ResolveImageSize(FileSystemPackRequest request)
    {
        if (request.PartitionSizeBytes is > 0)
        {
            return request.PartitionSizeBytes.Value;
        }

        var estimate = Directory.EnumerateFiles(request.SourceDirectory, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        var size = Math.Max(64L * 1024 * 1024, estimate * 2 + 16L * 1024 * 1024);
        return (size + 4095) / 4096 * 4096;
    }

    private async Task PrepareRemoteAsync(string serial, string remoteRoot, CancellationToken ct)
    {
        var mk = await _adb.ExecuteRawAsync(
            serial,
            ["shell", "mkdir", "-p", remoteRoot],
            cancellationToken: ct,
            logToHistory: false,
            timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (mk.ExitCode != 0)
        {
            throw new IOException("创建临时目录失败：" + mk.ToReadableError());
        }
    }

    private async Task ApplyMetadataAsync(
        FileSystemPackRequest request,
        string remoteRoot,
        string remoteSource,
        Action<string>? logLine,
        CancellationToken ct)
    {
        var metadataPath = FindMetadataPath(request);
        if (metadataPath is null)
        {
            logLine?.Invoke("未找到 metadata.jsonl；本次打包不恢复 mode/UID/GID（SELinux/capabilities 始终不注入）。");
            return;
        }

        var entries = new List<FileSystemMetadataEntry>();
        foreach (var line in await File.ReadAllLinesAsync(metadataPath, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<FileSystemMetadataEntry>(line, JsonOptions);
                if (entry is not null && !string.IsNullOrWhiteSpace(entry.RelativePath))
                {
                    entries.Add(entry);
                }
            }
            catch
            {
            }
        }

        if (entries.Count == 0)
        {
            logLine?.Invoke("metadata.jsonl 没有可应用条目，跳过元数据恢复。");
            return;
        }

        var tsv = new System.Text.StringBuilder();
        foreach (var entry in entries)
        {
            var relative = entry.RelativePath.Replace('\\', '/').TrimStart('/');
            var mode = string.Empty;
            try
            {
                var parsedMode = Convert.ToInt32(entry.Mode, 8);
                // metadata 里是包含文件类型的八进制（如 100644），chmod 只接受权限位。
                mode = Convert.ToString(parsedMode & 0xFFF, 8);
            }
            catch
            {
            }

            tsv.Append(relative).Append('\t')
                .Append(mode).Append('\t')
                .Append(entry.Uid).Append('\t')
                .Append(entry.Gid).Append('\t')
                .Append(entry.Kind).Append('\n');
        }

        var localMeta = Path.Combine(Path.GetTempPath(), "flashkit-meta-" + Guid.NewGuid().ToString("N") + ".tsv");
        await File.WriteAllTextAsync(localMeta, tsv.ToString(), ct).ConfigureAwait(false);
        var remoteMeta = $"{remoteRoot}/meta.tsv";
        try
        {
            var push = await _adb.PushAsync(request.Serial, localMeta, remoteMeta, null, ct).ConfigureAwait(false);
            if (push.ExitCode != 0)
            {
                logLine?.Invoke("metadata.tsv 推送失败，跳过元数据恢复：" + push.ToReadableError());
                return;
            }

            var script =
                $"base={remoteSource}; " +
                "while IFS=\"$(printf '\\t')\" read -r rel mode uid gid kind; do " +
                "[ -n \"$rel\" ] || continue; " +
                "p=\"$base/$rel\"; " +
                "if [ -L \"$p\" ]; then chown -h \"$uid:$gid\" \"$p\" 2>/dev/null; " +
                "elif [ -e \"$p\" ]; then " +
                "[ -n \"$mode\" ] && chmod \"$mode\" \"$p\" 2>/dev/null; " +
                "chown \"$uid:$gid\" \"$p\" 2>/dev/null; " +
                "fi; done < " + remoteMeta + "; true";
            await _adb.ExecuteRawAsync(
                request.Serial,
                ["shell", script],
                cancellationToken: ct,
                logToHistory: false,
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            logLine?.Invoke($"已应用 {entries.Count} 条 mode/UID/GID 元数据（SELinux/capabilities 未注入）。");
        }
        finally
        {
            try
            {
                File.Delete(localMeta);
            }
            catch
            {
            }
        }
    }

    private static string? FindMetadataPath(FileSystemPackRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.MetadataPath) && File.Exists(request.MetadataPath))
        {
            return request.MetadataPath;
        }

        var source = Path.GetFullPath(request.SourceDirectory);
        string?[] candidates =
        [
            Path.Combine(source, "metadata.jsonl"),
            Path.Combine(Path.GetDirectoryName(source) ?? source, "metadata.jsonl"),
            Path.Combine(source, "..", "metadata.jsonl")
        ];
        return candidates.Where(p => p is not null).Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private async Task PushSourceAsync(FileSystemPackRequest request, string remoteSource, Action<string>? logLine, CancellationToken ct)
    {
        var push = await _adb.PushAsync(
            request.Serial,
            request.SourceDirectory,
            remoteSource,
            line => logLine?.Invoke(line.Text),
            ct).ConfigureAwait(false);
        if (push.ExitCode != 0)
        {
            throw new IOException("推送源目录失败：" + push.ToReadableError());
        }
    }

    private async Task PullImageAsync(string serial, string remoteImage, string output, Action<string>? logLine, CancellationToken ct)
    {
        var pull = await _adb.PullAsync(
            serial,
            remoteImage,
            output,
            line => logLine?.Invoke(line.Text),
            ct).ConfigureAwait(false);
        if (pull.ExitCode != 0)
        {
            throw new IOException("拉回镜像失败：" + pull.ToReadableError());
        }
    }

    private async Task CleanupRemoteAsync(string serial, string remoteRoot)
    {
        try
        {
            await _adb.ExecuteRawAsync(
                serial,
                ["shell", "rm", "-rf", remoteRoot],
                cancellationToken: CancellationToken.None,
                logToHistory: false,
                timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task<string> PackErofsAsync(
        FileSystemPackRequest request,
        IProgress<OperationProgress>? progress,
        Action<string>? logLine,
        CancellationToken cancellationToken)
    {
        var output = Path.GetFullPath(request.OutputImagePath);
        if (Directory.Exists(output))
        {
            throw new IOException("输出路径是目录：" + output);
        }

        var parent = Path.GetDirectoryName(output);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var remoteRoot = $"/data/local/tmp/flashkit-pack-{Guid.NewGuid():N}";
        var remoteSource = $"{remoteRoot}/src";
        var remoteImage = $"{remoteRoot}/out.erofs";
        try
        {
            progress?.Report(new OperationProgress { Percentage = 5, Status = "准备设备临时目录" });
            var mk = await _adb.ExecuteRawAsync(
                request.Serial,
                ["shell", "mkdir", "-p", remoteRoot],
                cancellationToken: cancellationToken,
                logToHistory: false,
                timeout: TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (mk.ExitCode != 0)
            {
                throw new IOException("创建临时目录失败：" + mk.ToReadableError());
            }

            progress?.Report(new OperationProgress { Percentage = 15, Status = "推送源目录" });
            logLine?.Invoke("正在把源目录推送到设备…");
            var push = await _adb.PushAsync(
                request.Serial,
                request.SourceDirectory,
                remoteSource,
                line => logLine?.Invoke(line.Text),
                cancellationToken).ConfigureAwait(false);
            if (push.ExitCode != 0)
            {
                throw new IOException("推送源目录失败：" + push.ToReadableError());
            }

            await ApplyMetadataAsync(request, remoteRoot, remoteSource, logLine, cancellationToken).ConfigureAwait(false);

            progress?.Report(new OperationProgress { Percentage = 55, Status = "设备端 mkfs.erofs" });
            logLine?.Invoke("设备端 mkfs.erofs 正在生成镜像…");
            var script =
                $"rm -f {remoteImage}; " +
                $"mkfs.erofs -b 4096 -T 0 --all-time {remoteImage} {remoteSource}/; rc=$?; " +
                "echo \"FLASHKIT_RC=$rc\"; exit $rc";
            var build = await _adb.ExecuteRawAsync(
                request.Serial,
                ["shell", script],
                cancellationToken: cancellationToken,
                logToHistory: true,
                timeout: TimeSpan.FromMinutes(30)).ConfigureAwait(false);
            if (build.ExitCode != 0)
            {
                throw new FileSystemToolchainException("mkfs.erofs 失败：" + build.ToReadableError());
            }

            progress?.Report(new OperationProgress { Percentage = 85, Status = "拉回镜像" });
            var pull = await _adb.PullAsync(
                request.Serial,
                remoteImage,
                output,
                line => logLine?.Invoke(line.Text),
                cancellationToken).ConfigureAwait(false);
            if (pull.ExitCode != 0)
            {
                throw new IOException("拉回 erofs 镜像失败：" + pull.ToReadableError());
            }

            progress?.Report(new OperationProgress { Percentage = 100, Status = "打包完成" });
            logLine?.Invoke("erofs 打包完成（未注入 file_contexts/fs_config，元数据可能不完整）。");
            return output;
        }
        finally
        {
            try
            {
                await _adb.ExecuteRawAsync(
                    request.Serial,
                    ["shell", "rm", "-rf", remoteRoot],
                    cancellationToken: CancellationToken.None,
                    logToHistory: false,
                    timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task<FileSystemUnpackResult> UnpackExt4Async(
        FileSystemUnpackRequest request,
        string source,
        ImageInspection sourceInfo,
        IProgress<OperationProgress>? progress,
        Action<string>? logLine,
        CancellationToken cancellationToken)
    {
        var sevenZip = FindSevenZip()
                       ?? throw new FileSystemToolchainException(
                           "ext4 解包需要 debugfs 或内置 7-Zip；当前均未找到。");
        var output = string.IsNullOrWhiteSpace(request.OutputDirectory)
            ? Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + "_unpacked")
            : Path.GetFullPath(request.OutputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            throw new IOException("输出目录已存在且非空，请换一个目录。");
        }

        Directory.CreateDirectory(output);
        var filesDirectory = Path.Combine(output, "files");
        if (Directory.Exists(filesDirectory))
        {
            Directory.Delete(filesDirectory, recursive: true);
        }

        progress?.Report(new OperationProgress { Percentage = 10, Status = "7-Zip 展开 ext4" });
        logLine?.Invoke("使用内置 7-Zip 只读展开 ext4 文件树…");
        var result = await _runner.RunAsync(new ProcessRequest
        {
            ExecutablePath = sevenZip,
            WorkingDirectory = Path.GetDirectoryName(sevenZip) ?? AppContext.BaseDirectory,
            Arguments = ["x", source, "-o" + filesDirectory, "-y"],
            Timeout = TimeSpan.FromMinutes(30),
            LogToHistory = true,
            Category = "FileSystem",
            Purpose = "7-Zip 展开 ext4"
        }, line => logLine?.Invoke(line.Text), cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new FileSystemToolchainException("7-Zip ext4 展开失败：" + result.ToReadableError());
        }

        var listing = await _runner.RunAsync(new ProcessRequest
        {
            ExecutablePath = sevenZip,
            WorkingDirectory = Path.GetDirectoryName(sevenZip) ?? AppContext.BaseDirectory,
            Arguments = ["l", "-slt", source],
            Timeout = TimeSpan.FromMinutes(10),
            LogToHistory = false,
            Category = "FileSystem",
            Purpose = "7-Zip 读取 ext4 元数据"
        }, null, cancellationToken).ConfigureAwait(false);
        var metadata = listing.IsSuccess
            ? ParseSevenZipMetadata(listing.StandardOutput)
            : new List<FileSystemMetadataEntry>();

        var sysDir = Path.Combine(filesDirectory, "[SYS]");
        if (Directory.Exists(sysDir))
        {
            Directory.Delete(sysDir, recursive: true);
        }

        if (metadata.Count == 0)
        {
            metadata = Directory.EnumerateFileSystemEntries(filesDirectory, "*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    var relative = Path.GetRelativePath(filesDirectory, path);
                    var isDir = Directory.Exists(path);
                    return new FileSystemMetadataEntry
                    {
                        RelativePath = "/" + relative.Replace('\\', '/'),
                        Kind = isDir ? "directory" : "file",
                        SizeBytes = isDir ? 0 : new FileInfo(path).Length
                    };
                })
                .ToList();
        }

        var warnings = new List<string>
        {
            listing.IsSuccess
                ? "ext4 由 7-Zip 只读展开：已记录 mode/UID/GID，SELinux context/capabilities 仍未保留。"
                : "ext4 由 7-Zip 只读展开：7-Zip 元数据读取失败，mode/UID/GID/SELinux 均未记录。",
            "7-Zip 解包会跳过 journal/[SYS] 元数据，仅用于查看内容。"
        };
        var manifestPath = Path.Combine(output, "manifest.json");
        var manifest = new
        {
            manifestVersion = "1",
            toolVersion = AppInfo.Version,
            format = "ext4",
            source,
            sourceImageSize = new FileInfo(source).Length,
            blockSize = sourceInfo.BlockSize,
            outputDirectory = output,
            filesDirectory = "files",
            fileCount = metadata.Count,
            metadata,
            warnings,
            createdAt = DateTimeOffset.Now
        };
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(output, "metadata.jsonl"),
            string.Join('\n', metadata.Select(m => JsonSerializer.Serialize(m, JsonOptions))),
            cancellationToken).ConfigureAwait(false);
        progress?.Report(new OperationProgress { Percentage = 100, Status = "ext4 解包完成" });
        return new FileSystemUnpackResult
        {
            OutputDirectory = output,
            FilesDirectory = filesDirectory,
            Format = FileSystemFormat.Ext4,
            FileCount = metadata.Count,
            ManifestPath = manifestPath,
            Warnings = warnings
        };
    }

    private static List<FileSystemMetadataEntry> ParseSevenZipMetadata(string output)
    {
        var entries = new List<FileSystemMetadataEntry>();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                Flush();
                continue;
            }

            var index = trimmed.IndexOf(" = ", StringComparison.Ordinal);
            if (index <= 0)
            {
                continue;
            }

            fields[trimmed[..index].Trim()] = trimmed[(index + 3)..].Trim();
        }

        Flush();
        return entries;

        void Flush()
        {
            if (!fields.TryGetValue("Path", out var path) || string.IsNullOrWhiteSpace(path))
            {
                fields.Clear();
                return;
            }

            if (path.StartsWith("[SYS]", StringComparison.OrdinalIgnoreCase) || path.Contains(':'))
            {
                // 跳过 7-Zip 的归档头（Path = C:\...\image.img）和 [SYS]。
                fields.Clear();
                return;
            }

            var normalized = "/" + path.Replace('\\', '/').TrimStart('/');
            var mode = fields.GetValueOrDefault("Mode", string.Empty);
            int.TryParse(fields.GetValueOrDefault("User ID"), out var uid);
            int.TryParse(fields.GetValueOrDefault("Group ID"), out var gid);
            long.TryParse(fields.GetValueOrDefault("Size"), out var size);
            var link = fields.GetValueOrDefault("Symbolic Link", string.Empty);
            var isDir = fields.GetValueOrDefault("Folder") == "+";
            entries.Add(new FileSystemMetadataEntry
            {
                RelativePath = normalized,
                Kind = isDir ? "directory" : string.IsNullOrWhiteSpace(link) ? "file" : "symlink",
                Permissions = mode,
                Mode = SymbolicModeToOctal(mode),
                Uid = uid,
                Gid = gid,
                SizeBytes = isDir ? 0 : size,
                LinkTarget = string.IsNullOrWhiteSpace(link) ? null : link
            });
            fields.Clear();
        }
    }

    private static string SymbolicModeToOctal(string mode)
    {
        if (mode.Length < 10)
        {
            return string.Empty;
        }

        var value = 0;
        value |= Bits(mode, 1, 3) << 6;
        value |= Bits(mode, 4, 3) << 3;
        value |= Bits(mode, 7, 3);
        if (mode[0] == 'd')
        {
            value |= 0x4000;
        }
        else if (mode[0] == 'l')
        {
            value |= 0xA000;
        }
        else
        {
            value |= 0x8000;
        }

        return Convert.ToString(value, 8);
    }

    private static int Bits(string mode, int start, int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++)
        {
            if (mode[start + i] != '-')
            {
                value |= 1 << (count - 1 - i);
            }
        }

        return value;
    }

    private static string? FindSevenZip()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7z.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        string[] candidates =
        [
            @"C:\Program Files\7-Zip-Zstandard\7z.exe",
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe"
        ];
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), "7z.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<FileSystemMetadataEntry>> ReadMetadataAsync(
        string serial,
        string remoteOut,
        CancellationToken cancellationToken)
    {
        var script =
            $"cd {remoteOut} || exit 1; " +
            "find . -type f -o -type d -o -type l | while IFS= read -r p; do " +
            "stat -c '%F|%a|%u|%g|%s|%n' \"$p\" 2>/dev/null; " +
            "done";
        var result = await _adb.ExecuteRawAsync(
            serial,
            ["shell", script],
            cancellationToken: cancellationToken,
            logToHistory: false,
            timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        var entries = new List<FileSystemMetadataEntry>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|', 6);
            if (parts.Length < 6)
            {
                continue;
            }

            var type = parts[0];
            var mode = parts[1];
            int.TryParse(parts[2], out var uid);
            int.TryParse(parts[3], out var gid);
            long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            var path = parts[5];
            if (path == ".")
            {
                continue;
            }

            entries.Add(new FileSystemMetadataEntry
            {
                RelativePath = path.Replace('\\', '/'),
                Kind = type.Contains("directory", StringComparison.OrdinalIgnoreCase) ? "directory"
                    : type.Contains("symbolic", StringComparison.OrdinalIgnoreCase) ? "symlink"
                    : "file",
                Permissions = type,
                Mode = mode,
                Uid = uid,
                Gid = gid,
                SizeBytes = size
            });
        }

        return entries;
    }
}

public sealed class FileSystemToolchainException : Exception
{
    public FileSystemToolchainException(string message) : base(message)
    {
    }
}
