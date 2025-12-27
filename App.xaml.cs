using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using Serilog;

namespace Swarm;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug()
            .WriteTo.File(Path.Combine("logs", "swarm-.txt"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();

        Log.Information("Swarm application started.");

        // Global exception handling
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("Swarm application exiting.");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "UI Thread Error");
        ShowErrorAndExit(e.Exception, "UI Thread Error");
        e.Handled = true; // Prevent default crash behavior if possible, though we might still exit
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Log.Fatal(ex, "Fatal System Error");
            ShowErrorAndExit(ex, "Fatal System Error");
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Background Task Error");
        ShowErrorAndExit(e.Exception, "Background Task Error");
        e.SetObserved();
    }

    private void ShowErrorAndExit(Exception ex, string title)
    {
        var message = $"A fatal error occurred:\n\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}";
        
        // Use standard MessageBox
        System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        
        // Ensure we exit cleanly
        System.Windows.Application.Current.Shutdown(1);
    }
}

