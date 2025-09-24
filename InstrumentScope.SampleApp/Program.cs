using System;
using Avalonia;
using Avalonia.ReactiveUI;

namespace InstruMental.SampleApp;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppContext.SetSwitch("Avalonia.Diagnostics.Diagnostic.IsEnabled", true);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .UseReactiveUI();
}

