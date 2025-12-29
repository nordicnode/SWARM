using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Swarm.Avalonia.ViewModels;
using Swarm.Core.ViewModels;
using System.Windows.Input;
using Serilog;
using Microsoft.Extensions.DependencyInjection;
using Swarm.Core.Services;
using Swarm.Core.Abstractions;
using Swarm.Avalonia.Services;
using Swarm.Core.Models;

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

            var services = ConfigureServices();
            var mainWindow = new MainWindow();
            var mainViewModel = services.GetRequiredService<MainViewModel>();

            // Wire up Toast Service
            mainViewModel.ToastService.SetNotificationManager(mainWindow);

            mainWindow.DataContext = mainViewModel;
            desktop.MainWindow = mainWindow;

            // Initialize Tray View Model
            this.DataContext = new AppTrayViewModel(desktop, mainViewModel);

            desktop.Exit += (sender, e) =>
            {
                Log.Information("Swarm Avalonia application exiting.");
                Log.CloseAndFlush();
                mainViewModel.Dispose();
                if (services is IDisposable disposableServices)
                {
                    disposableServices.Dispose();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // Core Services
        services.AddSingleton<Settings>(sp => Settings.Load());
        services.AddSingleton<CryptoService>();
        services.AddSingleton<IHashingService, HashingService>();
        
        // Avalonia Services (must be registered before consumers like DiscoveryService)
        services.AddSingleton<AvaloniaDispatcher>();
        services.AddSingleton<Swarm.Core.Abstractions.IDispatcher>(sp => sp.GetRequiredService<AvaloniaDispatcher>());
        services.AddSingleton<AvaloniaPowerService>();
        services.AddSingleton<AvaloniaToastService>();

        // Services with dependencies
        services.AddSingleton<IDiscoveryService>(sp => {
            var settings = sp.GetRequiredService<Settings>();
            var crypto = sp.GetRequiredService<CryptoService>();
            var dispatcher = sp.GetRequiredService<Swarm.Core.Abstractions.IDispatcher>();
            return new DiscoveryService(settings.LocalId, crypto, settings, dispatcher);
        });
        services.AddSingleton<ITransferService, TransferService>();
        services.AddSingleton<VersioningService>();
        services.AddSingleton<ActivityLogService>();
        services.AddSingleton<FileStateCacheService>();
        
        // Complex Services
        services.AddSingleton<SyncService>();
        services.AddSingleton<IntegrityService>();
        services.AddSingleton<RescanService>();
        services.AddSingleton<ConflictResolutionService>();
        services.AddSingleton<ShareLinkService>();
        services.AddSingleton<PairingService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }
}

public class AppTrayViewModel
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MainViewModel _mainViewModel;

    public AppTrayViewModel(IClassicDesktopStyleApplicationLifetime desktop, MainViewModel mainViewModel)
    {
        _desktop = desktop;
        _mainViewModel = mainViewModel;
        
        ShowWindowCommand = new RelayCommand(ShowWindow);
        ExitCommand = new RelayCommand(Exit);
        OpenSyncFolderCommand = new RelayCommand(OpenSyncFolder);
        ToggleSyncCommand = _mainViewModel.ToggleSyncCommand;
        OpenSettingsCommand = new RelayCommand(OpenSettings);
    }

    public ICommand ShowWindowCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand OpenSyncFolderCommand { get; }
    public ICommand ToggleSyncCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    private void ShowWindow()
    {
        if (_desktop.MainWindow != null)
        {
            _desktop.MainWindow.Show();
            _desktop.MainWindow.WindowState = WindowState.Normal;
            _desktop.MainWindow.Activate();
        }
    }
    
    private void OpenSettings()
    {
        ShowWindow();
        if (_mainViewModel.NavigateToSettingsCommand.CanExecute(null))
        {
            _mainViewModel.NavigateToSettingsCommand.Execute(null);
        }
    }

    private void OpenSyncFolder()
    {
        var path = _mainViewModel.Settings.SyncFolderPath;
        if (Directory.Exists(path))
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    System.Diagnostics.Process.Start("explorer.exe", path);
                else if (OperatingSystem.IsMacOS())
                    System.Diagnostics.Process.Start("open", path);
                else if (OperatingSystem.IsLinux())
                    System.Diagnostics.Process.Start("xdg-open", path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to open sync folder");
            }
        }
    }

    private void Exit()
    {
        _desktop.Shutdown();
    }
}
