using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;

namespace FlashKit.Services.Tools;

public sealed class ToolchainService : IToolchainService
{
    private readonly IProcessRunner _runner;
    private ToolchainReport? _lastReport;

    public ToolchainService(IProcessRunner runner)
    {
        _runner = runner;
        ToolsDirectory = Path.Combine(AppContext.BaseDirectory, "tools", "platform-tools");
        AdbPath = Path.Combine(ToolsDirectory, "adb.exe");
        FastbootPath = Path.Combine(ToolsDirectory, "fastboot.exe");
    }

    public string ToolsDirectory { get; }
    public string AdbPath { get; }
    public string FastbootPath { get; }
    public ToolchainReport? LastReport => _lastReport;

    /// <summary>
    /// 启动时自检。即使 tools 缺失也不让应用崩溃，而是把错误显示在设置页和设备页，
    /// 让用户能先看到明确原因，再修复安装目录。
    /// </summary>
    public async Task<ToolchainReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var adbTask = CheckToolAsync("adb", AdbPath, ["version"], cancellationToken);
        var fastbootTask = CheckToolAsync("fastboot", FastbootPath, ["--version"], cancellationToken);
        var tools = await Task.WhenAll(adbTask, fastbootTask).ConfigureAwait(false);

        var report = new ToolchainReport
        {
            Tools = tools,
            ToolsDirectory = ToolsDirectory,
            CheckedAt = DateTimeOffset.Now
        };
        _lastReport = report;
        return report;
    }

    private async Task<ToolInfoItem> CheckToolAsync(
        string name,
        string path,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new ToolInfoItem(name, path, false, null, "文件不存在。请确认 tools\\platform-tools 已随应用一起发布。");
        }

        var result = await _runner.RunAsync(
            new ProcessRequest
            {
                ExecutablePath = path,
                WorkingDirectory = ToolsDirectory,
                Arguments = arguments,
                Timeout = TimeSpan.FromSeconds(15),
                LogToHistory = false,
                Category = "Toolchain",
                Purpose = $"{name} 版本自检"
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var output = result.CombinedOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);

        var error = result.IsSuccess
            ? null
            : result.ToReadableError();

        return new ToolInfoItem(name, path, true, output, error);
    }
}
