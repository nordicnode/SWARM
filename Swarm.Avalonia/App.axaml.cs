using System;
using System.ComponentModel;
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
using Microsoft.Extensions.Logging;
using Swarm.Core.Services;
using Swarm.Core.Abstractions;
using Swarm.Avalonia.Services;
using Swarm.Core.Models;
using Swarm.Core.Security;

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

        // Logging - Use Serilog through ILogger<T>
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: true);
        });

        // Core Services
        services.AddSingleton<Settings>(sp => Settings.Load());
        
        // Platform-specific secure key storage
        var keysDirectory = CryptoService.GetKeysDirectory();
        
        #pragma warning disable CA1416 // Platform compatibility - guarded by OperatingSystem.Is* checks
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<ISecureKeyStorage>(sp => 
                new WindowsSecureKeyStorage(keysDirectory, sp.GetRequiredService<ILogger<WindowsSecureKeyStorage>>()));
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<ISecureKeyStorage>(sp => 
                new MacOSSecureKeyStorage(keysDirectory, sp.GetRequiredService<ILogger<MacOSSecureKeyStorage>>()));
        }
        else // Linux and others
        {
            services.AddSingleton<ISecureKeyStorage>(sp => 
                new LinuxSecureKeyStorage(keysDirectory, sp.GetRequiredService<ILogger<LinuxSecureKeyStorage>>()));
        }
        #pragma warning restore CA1416
        
        services.AddSingleton<CryptoService>(sp => 
            new CryptoService(
                sp.GetRequiredService<ILogger<CryptoService>>(),
                sp.GetRequiredService<ISecureKeyStorage>()));
        services.AddSingleton<IHashingService, HashingService>();
        
        // Avalonia Services (must be registered before consumers like DiscoveryService)
        services.AddSingleton<AvaloniaDispatcher>();
        services.AddSingleton<Swarm.Core.Abstractions.IDispatcher>(sp => sp.GetRequiredService<AvaloniaDispatcher>());
        services.AddSingleton<AvaloniaPowerService>();
        services.AddSingleton<AvaloniaToastService>();

        // Services with dependencies
        services.AddSingleton<DiscoveryService>(sp => {
            var settings = sp.GetRequiredService<Settings>();
            var crypto = sp.GetRequiredService<CryptoService>();
            var logger = sp.GetRequiredService<ILogger<DiscoveryService>>();
            var dispatcher = sp.GetRequiredService<Swarm.Core.Abstractions.IDispatcher>();
            return new DiscoveryService(settings.LocalId, crypto, settings, logger, dispatcher);
        });
        services.AddSingleton<IDiscoveryService>(sp => sp.GetRequiredService<DiscoveryService>());
        services.AddSingleton<TransferService>();
        services.AddSingleton<ITransferService>(sp => sp.GetRequiredService<TransferService>());
        services.AddSingleton<VersioningService>();
        services.AddSingleton<ActivityLogService>();
        services.AddSingleton<IFileStateRepository, SqliteFileStateRepository>();
        
        // Complex Services
        services.AddSingleton<SyncService>(sp => {
            return new SyncService(
                sp.GetRequiredService<Settings>(),
                sp.GetRequiredService<IDiscoveryService>(),
                sp.GetRequiredService<ITransferService>(),
                sp.GetRequiredService<VersioningService>(),
                sp.GetRequiredService<IHashingService>(),
                sp.GetRequiredService<IFileStateRepository>(),
                sp.GetRequiredService<FolderEncryptionService>(),
                sp.GetRequiredService<ILogger<SyncService>>(),
                sp.GetRequiredService<AvaloniaToastService>(),
                sp.GetRequiredService<ActivityLogService>(),
                sp.GetRequiredService<ConflictResolutionService>()
            );
        });
        services.AddSingleton<IntegrityService>();
        services.AddSingleton<RescanService>();
        services.AddSingleton<ConflictResolutionService>();
        services.AddSingleton<ShareLinkService>();
        services.AddSingleton<PairingService>();
        services.AddSingleton<BandwidthTrackingService>();
        services.AddSingleton<FolderEncryptionService>();

        // Facade
        services.AddSingleton<CoreServiceFacade>();

        // ViewModels
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }
}

public class AppTrayViewModel : INotifyPropertyChanged
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MainViewModel _mainViewModel;
    private string _statusText = "Swarm - Ready";
    private string _syncStatusText = "Idle";
    private int _connectedPeers;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppTrayViewModel(IClassicDesktopStyleApplicationLifetime desktop, MainViewModel mainViewModel)
    {
        _desktop = desktop;
        _mainViewModel = mainViewModel;
        
        ShowWindowCommand = new RelayCommand(ShowWindow);
        ExitCommand = new RelayCommand(Exit);
        OpenSyncFolderCommand = new RelayCommand(OpenSyncFolder);
        ToggleSyncCommand = _mainViewModel.ToggleSyncCommand;
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        OpenActivityLogCommand = new RelayCommand(OpenActivityLog);
        OpenConflictHistoryCommand = new RelayCommand(OpenConflictHistory);
        OpenBandwidthCommand = new RelayCommand(OpenBandwidth);

        // Subscribe to status updates
        _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
        UpdateStatus();
    }

    public ICommand ShowWindowCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand OpenSyncFolderCommand { get; }
    public ICommand ToggleSyncCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenActivityLogCommand { get; }
    public ICommand OpenConflictHistoryCommand { get; }
    public ICommand OpenBandwidthCommand { get; }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText))); }
    }

    public string SyncStatusText
    {
        get => _syncStatusText;
        set { _syncStatusText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SyncStatusText))); }
    }

    public int ConnectedPeers
    {
        get => _connectedPeers;
        set { _connectedPeers = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ConnectedPeers))); }
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSyncing) || 
            e.PropertyName == nameof(MainViewModel.StatusText) ||
            e.PropertyName == nameof(MainViewModel.SyncingFileCountText))
        {
            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        if (_mainViewModel.IsSyncing)
        {
            SyncStatusText = $"Syncing: {_mainViewModel.SyncingFileCountText}";
            StatusText = $"Swarm - {_mainViewModel.SyncingFileCountText}";
        }
        else
        {
            SyncStatusText = "Idle";
            StatusText = "Swarm - Ready";
        }

        ConnectedPeers = _mainViewModel.OverviewVM.ConnectedPeers;
    }

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

    private void OpenActivityLog()
    {
        ShowWindow();
        if (_mainViewModel.OpenActivityLogCommand.CanExecute(null))
        {
            _mainViewModel.OpenActivityLogCommand.Execute(null);
        }
    }

    private void OpenConflictHistory()
    {
        ShowWindow();
        if (_mainViewModel.OpenConflictHistoryCommand.CanExecute(null))
        {
            _mainViewModel.OpenConflictHistoryCommand.Execute(null);
        }
    }

    private void OpenBandwidth()
    {
        ShowWindow();
        if (_mainViewModel.NavigateToBandwidthCommand.CanExecute(null))
        {
            _mainViewModel.NavigateToBandwidthCommand.Execute(null);
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
