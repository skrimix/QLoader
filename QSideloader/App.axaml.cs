using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using QSideloader.Helpers;
using QSideloader.ViewModels;
using QSideloader.Views;
using Serilog;
using Serilog.Exceptions;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Json;

namespace QSideloader;

public class App : Application
{
    public override void Initialize()
    {
        var exePath = Path.GetDirectoryName(AppContext.BaseDirectory);
        if (exePath is not null)
            Directory.SetCurrentDirectory(exePath);

        if (!Design.IsDesignMode)
            InitializeLogging();

        if (File.Exists("TrailersAddon.zip"))
        {
            Task.Run(() =>
            {
                Log.Information("Found trailers addon zip. Starting background install");
                ZipUtil.ExtractArchive("TrailersAddon.zip", Directory.GetCurrentDirectory());
                Log.Information("Installed trailers addon");
                File.Delete("TrailersAddon.zip");
            });
        }
        if (File.Exists(Path.Combine("..", "TrailersAddon.zip")))
        {
            Task.Run(() =>
            {
                Log.Information("Found trailers addon zip. Starting background install");
                ZipUtil.ExtractArchive(Path.Combine("..", "TrailersAddon.zip"), Directory.GetCurrentDirectory());
                Log.Information("Installed trailers addon");
                File.Delete(Path.Combine("..", "TrailersAddon.zip"));
            });
        }

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Globals.MainWindowViewModel = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = Globals.MainWindowViewModel
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void InitializeLogging()
    {
        const string humanReadableLogPath = "debug_log.txt";
        const string jsonLogPath = "debug_log.json";
        const string exceptionsLogPath = "debug_exceptions.txt";
        if (File.Exists(humanReadableLogPath) && new FileInfo(humanReadableLogPath).Length > 3000000)
            File.Delete(humanReadableLogPath);
        if (File.Exists(jsonLogPath) && new FileInfo(jsonLogPath).Length > 5000000)
            File.Delete(jsonLogPath);
        if (File.Exists(exceptionsLogPath))
        {
            File.Move(exceptionsLogPath, exceptionsLogPath + ".old", true);
            File.Delete(exceptionsLogPath);
        }


        var humanReadableLogger = new LoggerConfiguration().MinimumLevel.Verbose()
            .WriteTo.File(humanReadableLogPath, fileSizeLimitBytes: 3000000)
            .CreateLogger();

        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose()
            .Enrich.WithThreadId().Enrich.WithThreadName()
            .Enrich.WithExceptionDetails()
            .WriteTo.Logger(humanReadableLogger)
            .WriteTo.File(new JsonFormatter(renderMessage: true), jsonLogPath, fileSizeLimitBytes: 3000000)
            .CreateLogger();

        LogStartMessage(Log.Logger);

        // Log all exceptions
        var firstChanceExceptionLogger = new LoggerConfiguration().MinimumLevel.Error()
            .WriteTo.File(exceptionsLogPath)
            .CreateLogger();
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (!ShouldLogFirstChanceException(e)) return;
            firstChanceExceptionLogger.Error(e.Exception, "FirstChanceException");
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Globals.SideloaderSettings.EnableDebugConsole)
        {
            AllocConsole();
            Console.Title = $"{Assembly.GetExecutingAssembly().GetName().Name} debug console";
        }
#if DEBUG
        Console.Title = $"{Assembly.GetExecutingAssembly().GetName().Name} debug console";
#endif
        var consoleLogger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Console().CreateLogger();
        LogStartMessage(consoleLogger);
        Log.CloseAndFlush();
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose()
            .Enrich.WithThreadId().Enrich.WithThreadName()
            .Enrich.WithExceptionDetails()
            .WriteTo.Logger(humanReadableLogger)
            .WriteTo.File(new RenderedCompactJsonFormatter(), jsonLogPath, fileSizeLimitBytes: 3000000)
            .WriteTo.Logger(consoleLogger)
            .CreateLogger();
        
        if (Debugger.IsAttached)
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                if (!ShouldLogFirstChanceException(e)) return;
                consoleLogger.Error(e.Exception, "FirstChanceException");
            };
        
        bool ShouldLogFirstChanceException(FirstChanceExceptionEventArgs e)
        {
            return !(e.Exception.StackTrace is not null && e.Exception.StackTrace.Contains("GetRcloneDownloadStats")
                     || e.Exception.Message.Contains("127.0.0.1:5572")
                     || e.Exception.Message.Contains("does not contain a definition for 'bytes'")
                     || e.Exception.Message.Contains("does not contain a definition for 'speed'"));
        }
    }

    private static void LogStartMessage(ILogger logger)
    {
        logger.Information("----------------------------------------");
        logger.Information("Starting {ProgramName}...",
            Assembly.GetExecutingAssembly().GetName().Name);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
}