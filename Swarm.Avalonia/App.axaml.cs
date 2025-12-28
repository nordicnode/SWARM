using System;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Serilog;

namespace Swarm.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine("logs", "swarm-.txt"), 
                rollingInterval: RollingInterval.Day, 
                retainedFileCountLimit: 7)
            .CreateLogger();

        Log.Information("Swarm Avalonia application started.");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Set up global exception handling
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    Log.Fatal(ex, "Fatal unhandled exception");
                }
            };

            desktop.MainWindow = new MainWindow();

            desktop.Exit += (sender, e) =>
            {
                Log.Information("Swarm Avalonia application exiting.");
                Log.CloseAndFlush();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
