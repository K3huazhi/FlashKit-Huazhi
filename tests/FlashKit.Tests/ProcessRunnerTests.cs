using FlashKit.Core.Models;
using FlashKit.Services.Logging;
using FlashKit.Services.Process;
using Xunit;

namespace FlashKit.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task CapturesStdoutAndLogsCommand()
    {
        using var temp = new TempDirectory();
        var logging = new LoggingService();
        var runner = new ProcessRunner(logging);
        var result = await runner.RunAsync(new ProcessRequest
        {
            ExecutablePath = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            WorkingDirectory = temp.Path,
            Arguments = ["/c", "echo flashkit-process-test"],
            Timeout = TimeSpan.FromSeconds(10),
            LogToHistory = true,
            Category = "Test"
        });

        Assert.True(result.IsSuccess);
        Assert.Contains("flashkit-process-test", result.StandardOutput);
        Assert.Contains(logging.Snapshot(), e => e.Kind == LogKind.Command && e.ExitCode == 0);
    }

    [Fact]
    public async Task RunToFile_WritesBinaryBytesWithoutTextEncoding()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "binary.bin");
        var runner = new ProcessRunner(new LoggingService());
        var shell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var result = await runner.RunToFileAsync(
            new ProcessRequest
            {
                ExecutablePath = shell,
                WorkingDirectory = temp.Path,
                Arguments =
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "[Console]::OpenStandardOutput().Write([byte[]](0,1,2,255),0,4)"
                ],
                Timeout = TimeSpan.FromSeconds(15),
                LogToHistory = false
            },
            output);

        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task MissingExecutable_ReturnsFailure()
    {
        using var temp = new TempDirectory();
        var runner = new ProcessRunner(new LoggingService());
        var result = await runner.RunAsync(new ProcessRequest
        {
            ExecutablePath = Path.Combine(temp.Path, "does-not-exist.exe"),
            WorkingDirectory = temp.Path,
            Arguments = [],
            Timeout = TimeSpan.FromSeconds(1),
            LogToHistory = false
        });

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.StartError);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "flashkit-process-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
