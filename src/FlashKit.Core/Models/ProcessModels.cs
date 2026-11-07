namespace FlashKit.Core.Models;

public enum ProcessOutputSource
{
    StandardOutput,
    StandardError
}

public sealed record ProcessOutputLine(ProcessOutputSource Source, string Text);

/// <summary>
/// 所有外部进程调用的统一参数。禁止调用方自行拼接一串带引号的命令：
/// ArgumentList 由 ProcessRunner 负责，既避免注入，也能正确处理空格路径。
/// </summary>
public sealed class ProcessRequest
{
    public required string ExecutablePath { get; init; }
    public required string WorkingDirectory { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    public bool LogToHistory { get; init; } = true;
    public string Category { get; init; } = "Process";
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    /// <summary>仅用于日志展示的可选说明，例如“读取设备信息”。</summary>
    public string? Purpose { get; init; }
}

public sealed record ProcessResult
{
    public required string ExecutablePath { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public string WorkingDirectory { get; init; } = string.Empty;
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public bool TimedOut { get; init; }
    public bool Cancelled { get; init; }
    public TimeSpan Duration { get; init; }
    public string? StartError { get; init; }
    public string? Purpose { get; init; }

    public bool IsSuccess => ExitCode == 0 && !TimedOut && !Cancelled && StartError is null;

    public string CommandLine
    {
        get
        {
            var args = string.Join(' ', Arguments.Select(Quote));
            return $"{Quote(ExecutablePath)} {args}".TrimEnd();
        }
    }

    public string CombinedOutput
    {
        get
        {
            if (string.IsNullOrWhiteSpace(StandardError))
            {
                return StandardOutput;
            }

            if (string.IsNullOrWhiteSpace(StandardOutput))
            {
                return StandardError;
            }

            return StandardOutput.TrimEnd() + Environment.NewLine + StandardError;
        }
    }

    public string ToReadableError()
    {
        if (!string.IsNullOrWhiteSpace(StartError))
        {
            return StartError;
        }

        if (TimedOut)
        {
            return "命令执行超时";
        }

        if (Cancelled)
        {
            return "命令已取消";
        }

        var error = StandardError.Trim();
        if (!string.IsNullOrEmpty(error))
        {
            return error;
        }

        var output = StandardOutput.Trim();
        return string.IsNullOrEmpty(output) ? $"命令退出码 {ExitCode}" : output;
    }

    private static string Quote(string value) =>
        value.Contains(' ') || value.Contains('"')
            ? '"' + value.Replace("\"", "\\\"") + '"'
            : value;
}
