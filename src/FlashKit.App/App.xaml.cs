using FlashKit.App.Services;
using FlashKit.App.ViewModels;
using FlashKit.Core.Abstractions;
using FlashKit.Core.Models;
using FlashKit.Services.Adb;
using FlashKit.Services.Backup;
using FlashKit.Services.Devices;
using FlashKit.Services.Diagnostics;
using FlashKit.Services.Fastboot;
using FlashKit.Services.Files;
using FlashKit.Services.FileSystems;
using FlashKit.Services.Flash;
using FlashKit.Services.Images;
using FlashKit.Services.Logging;
using FlashKit.Services.Packages;
using FlashKit.Services.Partitions;
using FlashKit.Services.Process;
using FlashKit.Services.Tasks;
using FlashKit.Services.Terminal;
using FlashKit.Services.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace FlashKit.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        Services = ConfigureServices();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                WriteCrashLog(ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };
    }

    public static new App Current => (App)Application.Current;
    public static IServiceProvider Services { get; private set; } = null!;
    public static T GetService<T>() where T : notnull => Services.GetRequiredService<T>();

    public WindowManager WindowManager => GetService<WindowManager>();
    public ILoggingService Logging => GetService<ILoggingService>();

    public static IntPtr MainWindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(((App)Current)._window!);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
            _ = InitializeServicesAsync();
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            throw;
        }
    }

    public async Task ShutdownAsync()
    {
        WindowManager.CloseAll();
        try
        {
            await GetService<IDeviceManager>().StopAsync();
        }
        catch
        {
            // 关闭阶段不再抛出。
        }

        if (Services is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public static void ReportException(Exception ex, string category = "App")
    {
        try
        {
            GetService<ILoggingService>().Error(ex.ToString(), category);
        }
        catch
        {
            // 日志服务不可用时仍写入崩溃日志文件。
        }

        WriteCrashLog(ex);
    }

    private async Task InitializeServicesAsync()
    {
        var logging = GetService<ILoggingService>();
        logging.Info($"启动 {AppInfo.AppName} v{AppInfo.Version}", "App");
        try
        {
            var toolchain = GetService<IToolchainService>();
            var report = await toolchain.CheckAsync().ConfigureAwait(true);
            foreach (var tool in report.Tools)
            {
                if (tool.Exists && tool.Error is null)
                {
                    logging.Info($"{tool.Name}: {tool.Version}", "Toolchain");
                }
                else
                {
                    logging.Error($"{tool.Name} 自检失败：{tool.Error ?? "未知错误"}", "Toolchain");
                }
            }

            if (report.AllReady)
            {
                logging.Info(report.Summary, "Toolchain");
            }

            await GetService<IDeviceManager>().StartAsync().ConfigureAwait(true);
            await GetService<MainViewModel>().InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logging.Error($"服务初始化失败：{ex.Message}", "App");
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        try
        {
            GetService<ILoggingService>().Error(e.Exception?.ToString() ?? e.Message, "Unhandled");
        }
        catch
        {
            // 忽略日志失败。
        }
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FlashKit",
                "Logs");
            Directory.CreateDirectory(folder);
            var details = new System.Text.StringBuilder();
            details.AppendLine($"[{DateTimeOffset.Now:O}] {ex.GetType().FullName} HResult=0x{ex.HResult:X8}");
            details.AppendLine(ex.ToString());
            if (ex.InnerException is not null)
            {
                details.AppendLine("--- inner ---");
                details.AppendLine(ex.InnerException.ToString());
            }

            if (ex.Data.Count > 0)
            {
                details.AppendLine("--- data ---");
                foreach (System.Collections.DictionaryEntry entry in ex.Data)
                {
                    details.AppendLine($"{entry.Key}: {entry.Value}");
                }
            }

            details.AppendLine();
            File.AppendAllText(Path.Combine(folder, "crash.log"), details.ToString());
        }
        catch
        {
            // 崩溃日志写入失败不应递归崩溃。
        }
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggingService, LoggingService>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IToolchainService, ToolchainService>();
        services.AddSingleton<IAdbClient, AdbClient>();
        services.AddSingleton<IFastbootClient, FastbootClient>();
        services.AddSingleton<IDeviceManager, DeviceManager>();
        services.AddSingleton<IDeviceInfoProvider, DeviceInfoProvider>();
        services.AddSingleton<IPartitionCatalogService, PartitionCatalogService>();
        services.AddSingleton<IFlashValidationService, FlashValidationService>();
        services.AddSingleton<IFlashService, FlashService>();
        services.AddSingleton<IBackupEnvironmentService, BackupEnvironmentService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IRestoreService, RestoreService>();
        services.AddSingleton<ITaskQueueService, TaskQueueService>();
        services.AddSingleton<IImageToolService, ImageToolService>();
        services.AddSingleton<IDeviceFileService, DeviceFileService>();
        services.AddSingleton<IFileSystemService, FileSystemService>();
        services.AddSingleton<IPackageExtractor, PackageExtractor>();
        services.AddSingleton<IPackagePacker, PackagePacker>();
        services.AddSingleton<IDriverDiagnosticsService, DriverDiagnosticsService>();
        services.AddSingleton<ITerminalCommandService, TerminalCommandService>();
        services.AddSingleton<AppSessionState>();
        services.AddSingleton<WindowManager>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<TaskQueueViewModel>();
        services.AddSingleton<ImageToolsViewModel>();
        services.AddSingleton<RestoreViewModel>();
        services.AddSingleton<FileManagerViewModel>();
        services.AddSingleton<FileSystemViewModel>();
        services.AddSingleton<DeviceViewModel>();
        services.AddSingleton<BackupViewModel>();
        services.AddSingleton<FlashViewModel>();
        services.AddSingleton<PackageViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<TerminalViewModel>();
        services.AddSingleton<LogViewModel>();
        return services.BuildServiceProvider();
    }
}
