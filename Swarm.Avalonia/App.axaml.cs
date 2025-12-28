using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Swarm.Avalonia.ViewModels;
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

            var mainWindow = new MainWindow();
            var mainViewModel = new MainViewModel();

            // Wire up Toast Service
            mainViewModel.ToastService.SetNotificationManager(mainWindow);

            mainWindow.DataContext = mainViewModel;
            desktop.MainWindow = mainWindow;

            // Initialize Tray View Model
            this.DataContext = new AppTrayViewModel(desktop);

            desktop.Exit += (sender, e) =>
            {
                Log.Information("Swarm Avalonia application exiting.");
                Log.CloseAndFlush();
                mainViewModel.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}

public class AppTrayViewModel
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;

    public AppTrayViewModel(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        ShowWindowCommand = new RelayCommand(ShowWindow);
        ExitCommand = new RelayCommand(Exit);
    }

    public RelayCommand ShowWindowCommand { get; }
    public RelayCommand ExitCommand { get; }

    private void ShowWindow()
    {
        if (_desktop.MainWindow != null)
        {
            _desktop.MainWindow.Show();
            _desktop.MainWindow.WindowState = WindowState.Normal;
            _desktop.MainWindow.Activate();
        }
    }

    private void Exit()
    {
        _desktop.Shutdown();
    }
}
