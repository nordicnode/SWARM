using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Swarm.Avalonia.Services;
using Swarm.Core.Helpers;
using Swarm.Core.Models;
using Swarm.Core.Services;

namespace Swarm.Avalonia.ViewModels;

/// <summary>
/// Main ViewModel for the Swarm Avalonia application.
/// Handles navigation and shared state, managing Core services.
/// </summary>
public class MainViewModel : ViewModelBase, IDisposable
{
    private readonly Settings _settings;
    private readonly CryptoService _cryptoService;
    private readonly DiscoveryService _discoveryService;
    private readonly TransferService _transferService;
    private readonly VersioningService _versioningService;
    private readonly SyncService _syncService;
    private readonly IntegrityService _integrityService;
    private readonly RescanService _rescanService;
    private readonly ActivityLogService _activityLogService;
    private readonly ConflictResolutionService _conflictResolutionService;
    private readonly ShareLinkService _shareLinkService;

    // Services specific to Avalonia
    private readonly AvaloniaDispatcher _dispatcher;
    private readonly AvaloniaPowerService _powerService;
    public AvaloniaToastService ToastService { get; }

    private object? _currentView;
    private string _statusText = "Ready";
    private bool _isSyncing;
    private string _syncingFileCountText = "0 files";
    private string? _transferSpeedText;
    
    // Navigation state
    private bool _isOverviewSelected = true;
    private bool _isFilesSelected;
    private bool _isPeersSelected;
    private bool _isSettingsSelected;

    // Sub-ViewModels
    public OverviewViewModel OverviewVM { get; }
    public FilesViewModel FilesVM { get; }
    public PeersViewModel PeersVM { get; }
    public SettingsViewModel SettingsVM { get; }

    public MainViewModel()
    {
        _dispatcher = new AvaloniaDispatcher();
        _powerService = new AvaloniaPowerService();
        ToastService = new AvaloniaToastService();

        // Register power service and load settings
        Settings.RegisterPowerService(_powerService);
        _settings = Settings.Load();

        // Initialize services
        _cryptoService = new CryptoService();
        _discoveryService = new DiscoveryService(_settings.LocalId, _cryptoService, _settings, _dispatcher);
        _transferService = new TransferService(_settings, _cryptoService, _dispatcher);
        _versioningService = new VersioningService(_settings);
        _activityLogService = new ActivityLogService(_settings);
        _syncService = new SyncService(_settings, _discoveryService, _transferService, _versioningService, _activityLogService);
        _integrityService = new IntegrityService(_settings, _syncService);
        _rescanService = new RescanService(_settings, _syncService);
        _conflictResolutionService = new ConflictResolutionService(_settings, _versioningService, _activityLogService);
        _shareLinkService = new ShareLinkService(_settings);

        // Initialize Sub-ViewModels
        OverviewVM = new OverviewViewModel(_syncService, _versioningService, _activityLogService, _discoveryService, _settings);
        FilesVM = new FilesViewModel(_settings, _shareLinkService);
        PeersVM = new PeersViewModel(_discoveryService, _transferService, _settings);
        SettingsVM = new SettingsViewModel(
            _settings,
            _syncService,
            _discoveryService,
            _integrityService,
            _rescanService,
            _activityLogService,
            _versioningService
        );

        SettingsVM.SettingsChanged += ApplySettings;

        // Initialize commands
        NavigateToOverviewCommand = new RelayCommand(NavigateToOverview);
        NavigateToFilesCommand = new RelayCommand(NavigateToFiles);
        NavigateToPeersCommand = new RelayCommand(NavigateToPeers);
        NavigateToSettingsCommand = new RelayCommand(NavigateToSettings);
        
        // Start services
        InitializeServices();

        // Set default view
        CurrentView = OverviewVM;
    }

    private void InitializeServices()
    {
        _transferService.Start();
        _discoveryService.Start(_transferService.ListenPort);

        _discoveryService.LocalName = _settings.DeviceName;
        _discoveryService.IsSyncEnabled = _settings.IsSyncEnabled;

        if (_settings.IsSyncEnabled)
        {
            _syncService.Start();
            _rescanService.Start();
        }

        // Subscribe to events
        _syncService.SyncStatusChanged += OnSyncStatusChanged;
        _transferService.TransferProgress += OnTransferProgress;
    }

    private void OnSyncStatusChanged(string status)
    {
        Dispatcher.UIThread.Post(() => StatusText = status);
    }

    private void OnTransferProgress(FileTransfer transfer)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Simple status update for now
            var active = _transferService.Transfers.Count(t => t.Status == TransferStatus.InProgress);
            IsSyncing = active > 0 || _syncService.IsRunning;
            SyncingFileCountText = active > 0 ? $"{active} active transfers" : "";
        });
    }

    public void ApplySettings(Settings settings)
    {
        _discoveryService.LocalName = settings.DeviceName;
        _discoveryService.IsSyncEnabled = settings.IsSyncEnabled;
        _transferService.SetDownloadPath(settings.DownloadPath);

        if (settings.IsSyncEnabled && !_syncService.IsRunning)
        {
            _syncService.Start();
        }
        else if (!settings.IsSyncEnabled && _syncService.IsRunning)
        {
            _syncService.Stop();
        }
    }

    #region Properties

    public object? CurrentView
    {
        get => _currentView;
        set => SetProperty(ref _currentView, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool IsSyncing
    {
        get => _isSyncing;
        set => SetProperty(ref _isSyncing, value);
    }

    public string SyncingFileCountText
    {
        get => _syncingFileCountText;
        set => SetProperty(ref _syncingFileCountText, value);
    }

    public string? TransferSpeedText
    {
        get => _transferSpeedText;
        set => SetProperty(ref _transferSpeedText, value);
    }

    // Navigation state properties
    public bool IsOverviewSelected
    {
        get => _isOverviewSelected;
        set => SetProperty(ref _isOverviewSelected, value);
    }

    public bool IsFilesSelected
    {
        get => _isFilesSelected;
        set => SetProperty(ref _isFilesSelected, value);
    }

    public bool IsPeersSelected
    {
        get => _isPeersSelected;
        set => SetProperty(ref _isPeersSelected, value);
    }

    public bool IsSettingsSelected
    {
        get => _isSettingsSelected;
        set => SetProperty(ref _isSettingsSelected, value);
    }

    #endregion

    #region Commands

    public ICommand NavigateToOverviewCommand { get; }
    public ICommand NavigateToFilesCommand { get; }
    public ICommand NavigateToPeersCommand { get; }
    public ICommand NavigateToSettingsCommand { get; }

    #endregion

    #region Navigation Methods

    private void ClearNavSelection()
    {
        IsOverviewSelected = false;
        IsFilesSelected = false;
        IsPeersSelected = false;
        IsSettingsSelected = false;
    }

    private void NavigateToOverview()
    {
        ClearNavSelection();
        IsOverviewSelected = true;
        CurrentView = OverviewVM;
    }

    private void NavigateToFiles()
    {
        ClearNavSelection();
        IsFilesSelected = true;
        CurrentView = FilesVM;
    }

    private void NavigateToPeers()
    {
        ClearNavSelection();
        IsPeersSelected = true;
        CurrentView = PeersVM;
    }

    private void NavigateToSettings()
    {
        ClearNavSelection();
        IsSettingsSelected = true;
        CurrentView = SettingsVM;
    }

    #endregion

    public void Dispose()
    {
        _rescanService.Dispose();
        _activityLogService.Dispose();
        _syncService.Dispose();
        _versioningService.Dispose();
        _discoveryService.Dispose();
        _transferService.Dispose();
        _cryptoService.Dispose();
    }
}
