﻿using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.ReactiveUI;

namespace QSideloader;

internal static class Program
{
    private static bool _disableGpuRendering;

    public static string Name => "QLoader";
    public static string DataDirectory { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        _disableGpuRendering = args.Contains("--disable-gpu");
        // Use portable mode if requested or settings.json exists (backward compatibility)
        if (args.Contains("--portable") || File.Exists(Path.Combine(DataDirectory, "settings.json")))
        {
            DataDirectory = AppContext.BaseDirectory;
        }
        Directory.CreateDirectory(DataDirectory);
        
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    private static AppBuilder BuildAvaloniaApp()
    {
        var win32Options = new Win32PlatformOptions();
        var x11Options = new X11PlatformOptions();
        var avaloniaNativeOptions = new AvaloniaNativePlatformOptions();
        if (_disableGpuRendering)
        {
            win32Options.RenderingMode = new[] {Win32RenderingMode.Software};
            x11Options.RenderingMode = new[] {X11RenderingMode.Software};
            avaloniaNativeOptions.RenderingMode = new[] {AvaloniaNativeRenderingMode.Software};
        }

        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseReactiveUI()
            //.UseSkia()
            .With(win32Options)
            .With(x11Options)
            .With(avaloniaNativeOptions);

        return builder;
    }
}