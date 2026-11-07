using System.Management;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;

namespace FlashKit.Services.Diagnostics;

/// <summary>
/// 通过 WMI Win32_PnPEntity 检查设备管理器中带感叹号的设备。
/// 不尝试安装驱动，只给出可读提示；Google USB Driver 与厂商驱动均由引导页处理。
/// </summary>
public sealed class DriverDiagnosticsService : IDriverDiagnosticsService
{
    private static readonly string[] AndroidHints =
    [
        "android", "adb", "fastboot", "bootloader", "google", "qualcomm", "mediatek",
        "samsung", "xiaomi", "oneplus", "oppo", "vivo", "huawei", "honor", "motorola",
        "sony", "lg", "zte", "realme", "nothing"
    ];

    private static readonly string[] AndroidVendorIds =
    [
        "VID_18D1", // Google
        "VID_04E8", // Samsung
        "VID_05C6", // Qualcomm
        "VID_2717", // Xiaomi
        "VID_22D9", // OPPO
        "VID_2D95", // vivo
        "VID_12D1", // Huawei
        "VID_0B05", // ASUS
        "VID_0FCE", // Sony
        "VID_1004", // LG
        "VID_19D2"  // ZTE
    ];

    private readonly ILoggingService _logging;
    private readonly object _gate = new();
    private DriverHealthReport? _cache;
    private DateTimeOffset _cacheAt;

    public DriverDiagnosticsService(ILoggingService logging)
    {
        _logging = logging;
    }

    public Task<DriverHealthReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_cache is not null && DateTimeOffset.Now - _cacheAt < TimeSpan.FromSeconds(30))
            {
                return Task.FromResult(_cache);
            }
        }

        return Task.Run(() => CheckCore(cancellationToken), cancellationToken);
    }

    private DriverHealthReport CheckCore(CancellationToken cancellationToken)
    {
        try
        {
            var problems = new List<DriverProblemDevice>();
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, ConfigManagerErrorCode, Manufacturer FROM Win32_PnPEntity " +
                "WHERE ConfigManagerErrorCode <> 0");
            using var collection = searcher.Get();

            foreach (ManagementObject item in collection)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (item)
                {
                    var name = item["Name"]?.ToString()?.Trim() ?? "未知设备";
                    var deviceId = item["PNPDeviceID"]?.ToString();
                    var manufacturer = item["Manufacturer"]?.ToString();
                    var codeText = item["ConfigManagerErrorCode"]?.ToString();
                    _ = uint.TryParse(codeText, out var errorCode);

                    var likelyAndroid =
                        AndroidHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(deviceId) &&
                         AndroidVendorIds.Any(v => deviceId.Contains(v, StringComparison.OrdinalIgnoreCase)));

                    problems.Add(new DriverProblemDevice(name, deviceId, errorCode, manufacturer, likelyAndroid));
                }
            }

            var report = new DriverHealthReport
            {
                CheckSucceeded = true,
                ProblemDevices = problems
                    .OrderByDescending(p => p.LikelyAndroid)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Summary = problems.Count == 0
                    ? "设备管理器未发现异常设备"
                    : $"设备管理器发现 {problems.Count} 个异常设备，其中 {problems.Count(p => p.LikelyAndroid)} 个可能与安卓相关"
            };
            lock (_gate)
            {
                _cache = report;
                _cacheAt = DateTimeOffset.Now;
            }

            return report;
        }
        catch (Exception ex)
        {
            _logging.Warn($"驱动状态检查失败：{ex.Message}", "Driver");
            return new DriverHealthReport
            {
                CheckSucceeded = false,
                Error = ex.Message,
                Summary = "无法读取设备管理器状态"
            };
        }
    }
}
