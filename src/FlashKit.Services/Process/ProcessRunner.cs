using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;

namespace FlashKit.Services.Process;

/// <summary>
/// 唯一的外部进程入口。关键行为：
/// 1) 使用 ArgumentList，调用方无需拼引号；
/// 2) stdout/stderr 并行流式读取，避免管道写满导致死锁；
/// 3) 超时/取消时杀掉整个进程树；
/// 4) 执行结果写入全局命令日志（轮询类命令可显式关闭）。
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private const int MaxCapturedChars = 2 * 1024 * 1024;
    private readonly ILoggingService _logging;

    public ProcessRunner(ILoggingService logging)
    {
        _logging = logging;
    }

    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var output = new System.Text.StringBuilder();
        var error = new System.Text.StringBuilder();
        var gate = new object();

        if (!File.Exists(request.ExecutablePath))
        {
            var missing = new ProcessResult
            {
                ExecutablePath = request.ExecutablePath,
                Arguments = request.Arguments,
                WorkingDirectory = request.WorkingDirectory,
                ExitCode = -1,
                StartError = $"找不到可执行文件：{request.ExecutablePath}",
                Duration = stopwatch.Elapsed,
                Purpose = request.Purpose
            };
            LogIfNeeded(request, missing);
            return missing;
        }

        if (!Directory.Exists(request.WorkingDirectory))
        {
            var noWorkdir = new ProcessResult
            {
                ExecutablePath = request.ExecutablePath,
                Arguments = request.Arguments,
                WorkingDirectory = request.WorkingDirectory,
                ExitCode = -1,
                StartError = $"工作目录不存在：{request.WorkingDirectory}",
                Duration = stopwatch.Elapsed,
                Purpose = request.Purpose
            };
            LogIfNeeded(request, noWorkdir);
            return noWorkdir;
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var pair in request.Environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var timedOut = false;
        var cancelled = false;

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("系统未能启动进程");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (request.Timeout > TimeSpan.Zero && request.Timeout != Timeout.InfiniteTimeSpan)
            {
                linked.CancelAfter(request.Timeout);
            }

            var stdoutTask = ReadStreamAsync(process.StandardOutput, ProcessOutputSource.StandardOutput, output, onOutput, gate);
            var stderrTask = ReadStreamAsync(process.StandardError, ProcessOutputSource.StandardError, error, onOutput, gate);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                cancelled = cancellationToken.IsCancellationRequested;
                TryKill(process);
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // 进程可能已经自然退出。
                }
            }

            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

            var result = new ProcessResult
            {
                ExecutablePath = request.ExecutablePath,
                Arguments = request.Arguments,
                WorkingDirectory = request.WorkingDirectory,
                ExitCode = process.HasExited ? process.ExitCode : -1,
                StandardOutput = output.ToString(),
                StandardError = error.ToString(),
                TimedOut = timedOut,
                Cancelled = cancelled,
                Duration = stopwatch.Elapsed,
                Purpose = request.Purpose
            };
            LogIfNeeded(request, result);
            return result;
        }
        catch (Exception ex)
        {
            TryKill(process);
            var failed = new ProcessResult
            {
                ExecutablePath = request.ExecutablePath,
                Arguments = request.Arguments,
                WorkingDirectory = request.WorkingDirectory,
                ExitCode = -1,
                StandardOutput = output.ToString(),
                StandardError = error.ToString(),
                StartError = $"进程调用失败：{ex.Message}",
                TimedOut = timedOut,
                Cancelled = cancelled,
                Duration = stopwatch.Elapsed,
                Purpose = request.Purpose
            };
            LogIfNeeded(request, failed);
            return failed;
        }
    }

    public async Task<ProcessResult> RunToFileAsync(
        ProcessRequest request,
        string outputPath,
        Action<long>? onBytesWritten = null,
        Action<ProcessOutputLine>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var error = new System.Text.StringBuilder();
        var gate = new object();

        if (!File.Exists(request.ExecutablePath))
        {
            var missing = new ProcessResult
            {
                ExecutablePath = request.ExecutablePath,
                Arguments = request.Arguments,
                WorkingDirectory = request.WorkingDirectory,
                ExitCode = -1,
                StartError = $"找不到可执行文件：{request.ExecutablePath}",
                Duration = stopwatch.Elapsed,
                Purpose = request.Purpose
            };
            LogIfNeeded(request, missing);
            return missing;
        }

        if (!Directory.Exists(request.WorkingDirectory))
        {
            var noWorkdir = new ProcessResult
            {
                ExecutablePath = request.ExecutablePath,
                Arguments = request.Arguments,
                WorkingDirectory = request.WorkingDirectory,
                ExitCode = -1,
                StartError = $"工作目录不存在：{request.WorkingDirectory}",
                Duration = stopwatch.Elapsed,
                Purpose = request.Purpose
            };
            LogIfNeeded(request, noWorkdir);
            return noWorkdir;
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var pair in request.Environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var timedOut = false;
        var cancelled = false;

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("系统未能启动进程");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (request.Timeout > TimeSpan.Zero && request.Timeout != Timeout.InfiniteTimeSpan)
            {
                linked.CancelAfter(request.Timeout);
            }

            await using var file = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var stderrTask = ReadStreamAsync(
                process.StandardError,
                ProcessOutputSource.StandardError,
                error,
                onOutput,
                gate);
            var copyTask = CopyBinaryAsync(
                process.StandardOutput.BaseStream,
                file,
                onBytesWritten,
                linked.Token);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                cancelled = cancellationToken.IsCancellationRequested;
                TryKill(process);
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // 进程可能已经自然退出。
                }
            }

            try
            {
                await copyTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消/超时后文件可能只写入一部分，由调用方清理。
            }
            catch (IOException) when (timedOut || cancelled)
            {
                // 进程被杀后管道关闭。
            }

            await stderrTask.ConfigureAwait(false);

            var result = new ProcessResult
            {
                ExecutablePath = request.ExecutablePath,
                Arguments = request.Arguments,
                WorkingDirectory = request.WorkingDirectory,
                ExitCode = process.HasExited ? process.ExitCode : -1,
                StandardOutput = string.Empty,
                StandardError = error.ToString(),
                TimedOut = timedOut,
                Cancelled = cancelled,
                Duration = stopwatch.Elapsed,
                Purpose = request.Purpose
            };
            LogIfNeeded(request, result);
            return result;
        }
        catch (Exception ex)
        {
            TryKill(process);
            var failed = new ProcessResult
            {
                ExecutablePath = request.ExecutablePath,
                Arguments = request.Arguments,
                WorkingDirectory = request.WorkingDirectory,
                ExitCode = -1,
                StandardOutput = string.Empty,
                StandardError = error.ToString(),
                StartError = $"进程调用失败：{ex.Message}",
                TimedOut = timedOut,
                Cancelled = cancelled,
                Duration = stopwatch.Elapsed,
                Purpose = request.Purpose
            };
            LogIfNeeded(request, failed);
            return failed;
        }
    }

    private static async Task CopyBinaryAsync(
        Stream source,
        Stream destination,
        Action<long>? onBytesWritten,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            onBytesWritten?.Invoke(total);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task OpenFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"目录不存在：{folderPath}");
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { folderPath },
                UseShellExecute = true
            });
            _logging.Info($"打开目录：{folderPath}", "Shell");
        }
        catch (Exception ex)
        {
            _logging.Error($"打开目录失败：{ex.Message}", "Shell");
            throw;
        }

        return Task.CompletedTask;
    }

    public Task OpenUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("只允许打开 http/https 链接", nameof(url));
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
            _logging.Info($"打开链接：{uri.AbsoluteUri}", "Shell");
        }
        catch (Exception ex)
        {
            _logging.Error($"打开链接失败：{ex.Message}", "Shell");
            throw;
        }

        return Task.CompletedTask;
    }

    private static async Task ReadStreamAsync(
        StreamReader reader,
        ProcessOutputSource source,
        System.Text.StringBuilder buffer,
        Action<ProcessOutputLine>? onOutput,
        object gate)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            lock (gate)
            {
                if (buffer.Length < MaxCapturedChars)
                {
                    var remaining = MaxCapturedChars - buffer.Length;
                    if (line.Length + Environment.NewLine.Length <= remaining)
                    {
                        buffer.AppendLine(line);
                    }
                    else
                    {
                        buffer.Append(line.AsSpan(0, Math.Max(0, remaining - 1)));
                        buffer.AppendLine();
                    }
                }
            }

            try
            {
                onOutput?.Invoke(new ProcessOutputLine(source, line));
            }
            catch
            {
                // 实时显示回调不应该让外部命令失败。
            }
        }
    }

    private void LogIfNeeded(ProcessRequest request, ProcessResult result)
    {
        if (!request.LogToHistory)
        {
            return;
        }

        _logging.Add(new LogEntry
        {
            Kind = LogKind.Command,
            Category = request.Category,
            CommandLine = result.CommandLine,
            ExitCode = result.ExitCode,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            Duration = result.Duration,
            TimedOut = result.TimedOut,
            Cancelled = result.Cancelled,
            Message = result.StartError
        });
    }

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程可能已经退出，或权限不足；继续等待管道关闭。
        }
    }
}
