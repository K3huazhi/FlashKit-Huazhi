using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;

namespace FlashKit.Services.Adb;

public sealed class AdbClient : IAdbClient
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(30);
    private readonly IProcessRunner _runner;
    private readonly IToolchainService _toolchain;
    private readonly ILoggingService _logging;

    public AdbClient(IProcessRunner runner, IToolchainService toolchain, ILoggingService logging)
    {
        _runner = runner;
        _toolchain = toolchain;
        _logging = logging;
    }

    public async Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(
        CancellationToken cancellationToken = default,
        bool logToHistory = false)
    {
        var result = await ExecuteRawAsync(
            null,
            ["devices", "-l"],
            cancellationToken: cancellationToken,
            logToHistory: logToHistory,
            timeout: ShortTimeout).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            _logging.Warn($"adb devices 失败：{result.ToReadableError()}", "ADB");
            return Array.Empty<AdbDevice>();
        }

        var devices = new List<AdbDevice>();
        foreach (var rawLine in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 ||
                line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("* daemon", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("adb server", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var serial = parts[0];
            var state = parts[1];
            string? product = null;
            string? model = null;
            string? device = null;
            string? transportId = null;

            foreach (var token in parts.Skip(2))
            {
                var separator = token.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                var key = token[..separator];
                var value = token[(separator + 1)..];
                switch (key.ToLowerInvariant())
                {
                    case "product":
                        product = value.Replace('_', ' ');
                        break;
                    case "model":
                        model = value.Replace('_', ' ');
                        break;
                    case "device":
                        device = value.Replace('_', ' ');
                        break;
                    case "transport_id":
                        transportId = value;
                        break;
                }
            }

            devices.Add(new AdbDevice
            {
                Serial = serial,
                State = state,
                Product = product,
                Model = model,
                Device = device,
                TransportId = transportId
            });
        }

        return devices;
    }

    public async Task<string> GetStateAsync(
        string serial,
        bool logToHistory = false,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteRawAsync(
            serial,
            ["get-state"],
            cancellationToken: cancellationToken,
            logToHistory: logToHistory,
            timeout: TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        return result.IsSuccess ? result.StandardOutput.Trim() : "unknown";
    }

    public async Task<string> ShellAsync(
        string serial,
        string command,
        CancellationToken cancellationToken = default,
        bool logToHistory = true)
    {
        var result = await ExecuteRawAsync(
            serial,
            ["shell", command],
            cancellationToken: cancellationToken,
            logToHistory: logToHistory,
            timeout: ShortTimeout).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"adb shell 执行失败：{result.ToReadableError()}");
        }

        return result.CombinedOutput;
    }

    public async Task<string?> GetPropAsync(
        string serial,
        string propertyName,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecuteRawAsync(
            serial,
            ["shell", "getprop", propertyName],
            cancellationToken: cancellationToken,
            logToHistory: false,
            timeout: TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return null;
        }

        var value = result.StandardOutput.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public Task<ProcessResult> PushAsync(
        string serial,
        string localPath,
        string remotePath,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default) =>
        ExecuteRawAsync(
            serial,
            ["push", localPath, remotePath],
            onOutput,
            cancellationToken,
            logToHistory: true,
            timeout: TransferTimeout);

    public Task<ProcessResult> PullAsync(
        string serial,
        string remotePath,
        string localPath,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default) =>
        ExecuteRawAsync(
            serial,
            ["pull", remotePath, localPath],
            onOutput,
            cancellationToken,
            logToHistory: true,
            timeout: TransferTimeout);

    public Task<ProcessResult> ExecOutToFileAsync(
        string serial,
        string command,
        string localPath,
        Action<long>? onBytesWritten = null,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "-s", serial, "exec-out", command };
        return _runner.RunToFileAsync(
            new ProcessRequest
            {
                ExecutablePath = _toolchain.AdbPath,
                WorkingDirectory = _toolchain.ToolsDirectory,
                Arguments = arguments,
                Timeout = TimeSpan.FromMinutes(60),
                LogToHistory = true,
                Category = "ADB",
                Purpose = "exec-out 回传分区"
            },
            localPath,
            onBytesWritten,
            onOutput,
            cancellationToken);
    }

    public Task<ProcessResult> RebootAsync(string serial, string? target = null, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(target))
        {
            arguments.Add(target);
        }

        return ExecuteRawAsync(
            serial,
            ["reboot", .. arguments],
            cancellationToken: cancellationToken,
            logToHistory: true,
            timeout: TimeSpan.FromSeconds(15));
    }

    public Task<ProcessResult> StartServerAsync(CancellationToken cancellationToken = default) =>
        ExecuteRawAsync(
            null,
            ["start-server"],
            cancellationToken: cancellationToken,
            logToHistory: false,
            timeout: TimeSpan.FromSeconds(20));

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
                ExecutablePath = _toolchain.AdbPath,
                WorkingDirectory = _toolchain.ToolsDirectory,
                Arguments = finalArguments,
                Timeout = timeout ?? ShortTimeout,
                LogToHistory = logToHistory,
                Category = "ADB",
                Purpose = string.Join(' ', arguments.Take(2))
            },
            onOutput,
            cancellationToken);
    }
}
