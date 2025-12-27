using System.Configuration;
using System.Data;
using System.Windows;

namespace Swarm;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global exception handling
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        ShowErrorAndExit(e.Exception, "UI Thread Error");
        e.Handled = true; // Prevent default crash behavior if possible, though we might still exit
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ShowErrorAndExit(ex, "Fatal System Error");
        }
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
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

