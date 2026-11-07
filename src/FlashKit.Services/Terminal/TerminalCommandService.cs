using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using FlashKit.Core.Parsing;

namespace FlashKit.Services.Terminal;

public sealed class TerminalCommandService : ITerminalCommandService
{
    private readonly IAdbClient _adb;
    private readonly IFastbootClient _fastboot;
    private readonly IDeviceManager _deviceManager;

    public TerminalCommandService(IAdbClient adb, IFastbootClient fastboot, IDeviceManager deviceManager)
    {
        _adb = adb;
        _fastboot = fastboot;
        _deviceManager = deviceManager;
    }

    public async Task ExecuteAsync(
        string commandLine,
        Action<ProcessOutputLine> onOutput,
        CancellationToken cancellationToken = default)
    {
        var tokens = CommandLineTokenizer.Tokenize(commandLine);
        if (tokens.Count == 0)
        {
            throw new InvalidOperationException("请输入 adb 或 fastboot 命令。");
        }

        var executable = tokens[0].ToLowerInvariant();
        if (!CommandLineTokenizer.IsAllowedExecutable(executable))
        {
            throw new InvalidOperationException("终端只接受 adb 或 fastboot 开头的命令，不开放任意系统命令。");
        }

        var arguments = tokens.Skip(1).ToList();
        var serial = ExtractSerial(arguments);
        var isAdb = executable == "adb";

        if (string.IsNullOrWhiteSpace(serial))
        {
            var selected = _deviceManager.SelectedDevice;
            if (selected is not null &&
                ((isAdb && selected.Mode != DeviceMode.Fastboot) ||
                 (!isAdb && selected.Mode == DeviceMode.Fastboot)))
            {
                serial = selected.Serial;
            }
        }

        var isGlobal = IsGlobalCommand(isAdb, arguments);
        var clientSerial = isGlobal ? null : serial;
        var displayArguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(clientSerial))
        {
            displayArguments.Add("-s");
            displayArguments.Add(clientSerial);
        }

        displayArguments.AddRange(arguments);
        var display = (isAdb ? "adb " : "fastboot ") + string.Join(' ', displayArguments.Select(Quote));
        onOutput(new ProcessOutputLine(ProcessOutputSource.StandardOutput, $"> {display}"));

        var timeout = IsFlashLike(isAdb, arguments)
            ? TimeSpan.FromMinutes(15)
            : TimeSpan.FromMinutes(2);

        var result = isAdb
            ? await _adb.ExecuteRawAsync(
                clientSerial,
                arguments,
                onOutput,
                cancellationToken,
                logToHistory: true,
                timeout: timeout).ConfigureAwait(false)
            : await _fastboot.ExecuteRawAsync(
                clientSerial,
                arguments,
                onOutput,
                cancellationToken,
                logToHistory: true,
                timeout: timeout).ConfigureAwait(false);

        if (result.Cancelled)
        {
            throw new OperationCanceledException("命令已取消");
        }

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.ToReadableError());
        }

        onOutput(new ProcessOutputLine(
            ProcessOutputSource.StandardOutput,
            $"[完成，退出码 {result.ExitCode}，用时 {result.Duration.TotalSeconds:0.0}s]"));
    }

    private static string? ExtractSerial(List<string> arguments)
    {
        for (var i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i] is "-s" or "--serial")
            {
                var serial = arguments[i + 1];
                arguments.RemoveRange(i, 2);
                return serial;
            }
        }

        return null;
    }

    private static bool IsGlobalCommand(bool isAdb, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return true;
        }

        var command = arguments[0].ToLowerInvariant();
        return isAdb
            ? command is "devices" or "version" or "start-server" or "kill-server" or "help" or "connect" or "disconnect" or "pair"
            : command is "devices" or "version" or "help" or "--version";
    }

    private static bool IsFlashLike(bool isAdb, IReadOnlyList<string> arguments)
    {
        if (isAdb)
        {
            return arguments.Any(a => a.Equals("push", StringComparison.OrdinalIgnoreCase) ||
                                      a.Equals("pull", StringComparison.OrdinalIgnoreCase) ||
                                      a.Equals("install", StringComparison.OrdinalIgnoreCase) ||
                                      a.Equals("sideload", StringComparison.OrdinalIgnoreCase));
        }

        return arguments.Any(a => a.Equals("flash", StringComparison.OrdinalIgnoreCase) ||
                                  a.Equals("erase", StringComparison.OrdinalIgnoreCase) ||
                                  a.Equals("update", StringComparison.OrdinalIgnoreCase));
    }

    private static string Quote(string value) =>
        value.Contains(' ') ? '"' + value + '"' : value;
}
