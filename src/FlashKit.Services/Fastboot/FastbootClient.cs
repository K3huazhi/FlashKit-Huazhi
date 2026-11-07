using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using FlashKit.Core.Parsing;

namespace FlashKit.Services.Fastboot;

public sealed class FastbootClient : IFastbootClient
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FlashTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan GetVarAllTimeout = TimeSpan.FromSeconds(60);
    private readonly IProcessRunner _runner;
    private readonly IToolchainService _toolchain;

    public FastbootClient(IProcessRunner runner, IToolchainService toolchain)
    {
        _runner = runner;
        _toolchain = toolchain;
    }

    public async Task<IReadOnlyList<FastbootDevice>> ListDevicesAsync(
        CancellationToken cancellationToken = default,
        bool logToHistory = false)
    {
        var result = await ExecuteRawAsync(
            null,
            ["devices"],
            cancellationToken: cancellationToken,
            logToHistory: logToHistory,
            timeout: ShortTimeout).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return Array.Empty<FastbootDevice>();
        }

        var devices = new List<FastbootDevice>();
        foreach (var rawLine in result.CombinedOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("< waiting", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1].Contains("fastboot", StringComparison.OrdinalIgnoreCase))
            {
                devices.Add(new FastbootDevice { Serial = parts[0], RawLine = line });
            }
        }

        return devices;
    }

    public Task<ProcessResult> ExecuteRawAsync(
        string? serial,
        IReadOnlyList<string> arguments,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default,
        bool logToHistory = true,
        TimeSpan? timeout = null)
    {
        var finalArguments = new List<string>(arguments.Count + 2);
        if (!string.IsNullOrWhiteSpace(serial))
        {
            finalArguments.Add("-s");
            finalArguments.Add(serial);
        }

        finalArguments.AddRange(arguments);
        return _runner.RunAsync(
            new ProcessRequest
            {
                ExecutablePath = _toolchain.FastbootPath,
                WorkingDirectory = _toolchain.ToolsDirectory,
                Arguments = finalArguments,
                Timeout = timeout ?? ShortTimeout,
                LogToHistory = logToHistory,
                Category = "Fastboot",
                Purpose = string.Join(' ', arguments.Take(2))
            },
            onOutput,
            cancellationToken);
    }

    public async Task<string?> GetVarAsync(string serial, string variableName, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteRawAsync(
            serial,
            ["getvar", variableName],
            cancellationToken: cancellationToken,
            logToHistory: false,
            timeout: TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        var vars = DeviceOutputParser.ParseFastbootGetVarAll(result.CombinedOutput);
        if (vars.TryGetValue(variableName, out var value))
        {
            return value;
        }

        // fastboot 有时输出 "variable: value" 而变量名大小写/前缀不同，做一次宽松匹配。
        var pair = vars.FirstOrDefault(kv => kv.Key.EndsWith(variableName, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(pair.Key) ? null : pair.Value;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetVarAllAsync(string serial, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteRawAsync(
            serial,
            ["getvar", "all"],
            cancellationToken: cancellationToken,
            logToHistory: true,
            timeout: GetVarAllTimeout).ConfigureAwait(false);

        return DeviceOutputParser.ParseFastbootGetVarAll(result.CombinedOutput);
    }

    public async Task<bool?> IsUserspaceAsync(string serial, CancellationToken cancellationToken = default)
    {
        var value = await GetVarAsync(serial, "is-userspace", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value == "1";
    }

    public Task<ProcessResult> FlashAsync(
        string serial,
        string partitionName,
        string imagePath,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default) =>
        ExecuteRawAsync(
            serial,
            ["flash", partitionName, imagePath],
            onOutput,
            cancellationToken,
            logToHistory: true,
            timeout: FlashTimeout);

    public Task<ProcessResult> RebootAsync(string serial, string? target = null, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "reboot" };
        if (!string.IsNullOrWhiteSpace(target))
        {
            arguments.Add(target);
        }

        return ExecuteRawAsync(
            serial,
            arguments,
            cancellationToken: cancellationToken,
            logToHistory: true,
            timeout: TimeSpan.FromSeconds(20));
    }

    public Task<ProcessResult> FlashLogicalPartitionAsync(
        string serial,
        string partition,
        string imagePath,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default) =>
        ExecuteRawAsync(
            serial,
            ["flash", partition, imagePath],
            onOutput,
            cancellationToken,
            logToHistory: true,
            timeout: FlashTimeout);

    public Task<ProcessResult> FetchAsync(
        string serial,
        string partitionName,
        string outputPath,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default) =>
        ExecuteRawAsync(
            serial,
            ["fetch", partitionName, outputPath],
            onOutput,
            cancellationToken,
            logToHistory: true,
            timeout: TimeSpan.FromMinutes(30));
}
