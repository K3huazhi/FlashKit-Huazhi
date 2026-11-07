using System.Globalization;
using System.Text.RegularExpressions;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;

namespace FlashKit.Services.Files;

public sealed class DeviceFileService : IDeviceFileService
{
    private readonly IAdbClient _adb;
    private readonly ILoggingService _logging;

    public DeviceFileService(IAdbClient adb, ILoggingService logging)
    {
        _adb = adb;
        _logging = logging;
    }

    public async Task<IReadOnlyList<DeviceFileEntry>> ListAsync(string serial, string path, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(path);
        var quoted = Quote(normalized);
        var script =
            $"p={quoted}; [ -d \"$p\" ] || exit 3; " +
            "for f in \"$p\"/* \"$p\"/.[!.]*; do [ -e \"$f\" ] || continue; " +
            "stat -c '%A|%U|%G|%s|%Y|%n' \"$f\" 2>/dev/null; done";
        var output = await _adb.ShellAsync(serial, script, cancellationToken, logToHistory: false).ConfigureAwait(false);
        var items = new List<DeviceFileEntry>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|', 6);
            if (parts.Length < 6)
            {
                continue;
            }

            var permissions = parts[0].Trim();
            var name = parts[5].Trim();
            if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
            {
                continue;
            }

            if (!long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                size = 0;
            }

            DateTimeOffset? modified = null;
            if (long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch) && epoch > 0)
            {
                try
                {
                    modified = DateTimeOffset.FromUnixTimeSeconds(epoch);
                }
                catch
                {
                }
            }

            var fullPath = normalized == "/" ? "/" + name : normalized.TrimEnd('/') + "/" + name;
            items.Add(new DeviceFileEntry
            {
                Name = name,
                FullPath = fullPath,
                IsDirectory = permissions.StartsWith('d'),
                IsLink = permissions.StartsWith('l'),
                SizeBytes = size,
                Permissions = permissions,
                Owner = parts[1],
                Group = parts[2],
                ModifiedAt = modified
            });
        }

        return items
            .OrderByDescending(i => i.IsDirectory)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<FileTransferResult> PullAsync(
        string serial,
        string remotePath,
        string localPath,
        Action<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            throw new InvalidOperationException("设备路径为空。");
        }

        var parent = Path.GetDirectoryName(Path.GetFullPath(localPath));
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        progress?.Invoke(new FileTransferProgress { Status = "正在从设备下载" });
        var result = await _adb.PullAsync(
            serial,
            remotePath,
            localPath,
            line => ReportAdbProgress(line.Text, progress),
            cancellationToken).ConfigureAwait(false);
        return new FileTransferResult
        {
            LocalPath = localPath,
            RemotePath = remotePath,
            ExitCode = result.ExitCode,
            Message = result.IsSuccess ? "下载完成" : result.ToReadableError(),
            Cancelled = result.Cancelled
        };
    }

    public async Task<FileTransferResult> PushAsync(
        string serial,
        string localPath,
        string remotePath,
        Action<FileTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localPath) && !Directory.Exists(localPath))
        {
            throw new FileNotFoundException("本地路径不存在。", localPath);
        }

        if (!IsSafeWritePath(remotePath, out var reason))
        {
            throw new UnauthorizedAccessException(reason);
        }

        progress?.Invoke(new FileTransferProgress { Status = "正在上传到设备" });
        var result = await _adb.PushAsync(
            serial,
            localPath,
            remotePath,
            line => ReportAdbProgress(line.Text, progress),
            cancellationToken).ConfigureAwait(false);
        return new FileTransferResult
        {
            LocalPath = localPath,
            RemotePath = remotePath,
            ExitCode = result.ExitCode,
            Message = result.IsSuccess ? "上传完成" : result.ToReadableError(),
            Cancelled = result.Cancelled
        };
    }

    public bool IsSafeWritePath(string remotePath, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(remotePath) || !remotePath.StartsWith('/'))
        {
            reason = "设备路径必须是绝对路径。";
            return false;
        }

        var path = Normalize(remotePath);
        if (path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(s => s == ".."))
        {
            reason = "设备路径不允许包含 ..。";
            return false;
        }

        string[] blocked = ["/dev", "/proc", "/sys", "/system", "/vendor", "/product", "/odm", "/metadata"];
        if (blocked.Any(b => path.Equals(b, StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase)))
        {
            reason = $"禁止写入系统路径：{path}。可写 /data/local/tmp、/sdcard 等用户路径。";
            return false;
        }

        return true;
    }

    private static void ReportAdbProgress(string line, Action<FileTransferProgress>? progress)
    {
        if (progress is null)
        {
            return;
        }

        var match = Regex.Match(line, @"\[\s*(\d+)%\]");
        if (match.Success && double.TryParse(match.Groups[1].Value, out var percent))
        {
            progress.Invoke(new FileTransferProgress { Percentage = Math.Clamp(percent, 0, 100), Status = "传输中" });
        }
        else if (!string.IsNullOrWhiteSpace(line))
        {
            progress.Invoke(new FileTransferProgress { Status = line.Trim() });
        }
    }

    private static string Normalize(string path)
    {
        var trimmed = path.Trim();
        if (!trimmed.StartsWith('/'))
        {
            throw new InvalidOperationException("设备路径必须以 / 开头。");
        }

        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : "/";
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
