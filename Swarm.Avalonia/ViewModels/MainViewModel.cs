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
using Swarm.Core.ViewModels;
using Avalonia.Controls.Notifications;
using Swarm.Core.Abstractions;
using Avalonia.Media;

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
    private readonly IntegrityVerificationService _integrityVerificationService;
    private readonly ActivityLogService _activityLogService;
    private readonly ConflictResolutionService _conflictResolutionService;
    private readonly ShareLinkService _shareLinkService;
    private readonly PairingService _pairingService;
    private readonly BandwidthTrackingService _bandwidthTrackingService;
    private readonly FolderEncryptionService _folderEncryptionService;
    private readonly SyncStatisticsService _syncStatisticsService;

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
    private bool _isBandwidthSelected;
    private bool _isStatsSelected;
    private bool _isSettingsSelected;

    // Sub-ViewModels
    public OverviewViewModel OverviewVM { get; }
    public FilesViewModel FilesVM { get; }
    public PeersViewModel PeersVM { get; }
    public BandwidthViewModel BandwidthVM { get; }
    public StatsViewModel StatsVM { get; }
    public SettingsViewModel SettingsVM { get; }

    public MainViewModel(
        CoreServiceFacade coreServices,
        AvaloniaDispatcher dispatcher,
        AvaloniaPowerService powerService,
        AvaloniaToastService toastService)
    {
        _settings = coreServices.Settings;
        _cryptoService = coreServices.CryptoService;
        _discoveryService = coreServices.DiscoveryService;
        _transferService = coreServices.TransferService;
        _versioningService = coreServices.VersioningService;
        _syncService = coreServices.SyncService;
        _integrityService = coreServices.IntegrityService;
        _rescanService = coreServices.RescanService;
        _integrityVerificationService = coreServices.IntegrityVerificationService;
        _activityLogService = coreServices.ActivityLogService;
        _conflictResolutionService = coreServices.ConflictResolutionService;
        _shareLinkService = coreServices.ShareLinkService;
        _pairingService = coreServices.PairingService;
        _bandwidthTrackingService = coreServices.BandwidthTrackingService;
        _folderEncryptionService = coreServices.FolderEncryptionService;
        _syncStatisticsService = coreServices.SyncStatisticsService;
        
        // Avalonia services
        _dispatcher = dispatcher;
        _powerService = powerService;
        ToastService = toastService;

        // Register power service
        Settings.RegisterPowerService(_powerService);

        // Initialize Sub-ViewModels
        OverviewVM = new OverviewViewModel(_syncService, _discoveryService, _settings, _activityLogService);
        FilesVM = new FilesViewModel(_settings, _syncService, _versioningService, _folderEncryptionService);
        PeersVM = new PeersViewModel(_discoveryService, _transferService, _settings);
        SettingsVM = new SettingsViewModel(
            _settings,
            _syncService,
            _discoveryService,
            _integrityService,
            _rescanService,
            _activityLogService,
            _versioningService,
            toastService
        );
        BandwidthVM = new BandwidthViewModel(_bandwidthTrackingService);
        StatsVM = new StatsViewModel(_syncStatisticsService);

        SettingsVM.SettingsChanged += ApplySettings;

        // Initialize commands
        NavigateToOverviewCommand = new RelayCommand(NavigateToOverview);
        NavigateToFilesCommand = new RelayCommand(NavigateToFiles);
        NavigateToPeersCommand = new RelayCommand(NavigateToPeers);
        NavigateToBandwidthCommand = new RelayCommand(NavigateToBandwidth);
        NavigateToStatsCommand = new RelayCommand(NavigateToStats);
        NavigateToSettingsCommand = new RelayCommand(NavigateToSettings);
        ToggleSyncCommand = new RelayCommand(ToggleSync);
        PauseSyncCommand = new RelayCommand(PauseSyncWithReason);
        OpenActivityLogCommand = new RelayCommand(OpenActivityLog);
        OpenConflictHistoryCommand = new RelayCommand(OpenConflictHistory);
        OpenTransferQueueCommand = new RelayCommand(OpenTransferQueue);
        RefreshOverviewCommand = new RelayCommand(RefreshOverview);
        FocusSearchCommand = new RelayCommand(() => FocusSearchRequested?.Invoke());
        
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
            _integrityVerificationService.Start(); // Periodic tree verification
        }

        // Subscribe to events
        _syncService.SyncStatusChanged += OnSyncStatusChanged;
        _syncService.TimeTravelDetected += OnTimeTravelDetected;
        _syncService.FileConflictDetected += OnFileConflictDetected;
        _transferService.TransferProgress += OnTransferProgress;
        _transferService.IncomingFileRequest += OnIncomingFileRequest;
        _discoveryService.UntrustedPeerDiscovered += OnUntrustedPeerDiscovered;
    }

    private void OnIncomingFileRequest(string fileName, string senderName, long size, Action<bool> callback)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                // Get the main window as the owner for the dialog
                var mainWindow = global::Avalonia.Application.Current?.ApplicationLifetime is 
                    global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow
                    : null;

                if (mainWindow == null)
                {
                    // Fallback: auto-accept if no window available
                    callback(true);
                    return;
                }

                // Show the file transfer dialog
                var dialog = new Views.FileTransferDialog(fileName, senderName, size);
                var result = await dialog.ShowDialog<bool?>(mainWindow);

                if (result == true)
                {
                    ToastService.Show("Receiving File", 
                        $"Receiving '{fileName}' ({FileHelpers.FormatBytes(size)}) from {senderName}", 
                        NotificationType.Information);
                    callback(true);
                }
                else
                {
                    ToastService.Show("Transfer Declined", 
                        $"Declined '{fileName}' from {senderName}", 
                        NotificationType.Warning);
                    callback(false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"File transfer dialog error: {ex.Message}");
                // Fallback: auto-accept on error
                callback(true);
            }
        });
    }

    private void OnSyncStatusChanged(string status)
    {
        Dispatcher.UIThread.Post(() => StatusText = status);
    }

    private void OnTimeTravelDetected(string fileName, DateTime futureTime)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ToastService.Show("Time Travel Detected", 
                $"File '{fileName}' is from the future ({futureTime}). Check peer clocks!", 
                NotificationType.Warning);
        });
    }

    private void OnFileConflictDetected(string localPath, string? backupPath)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var fileName = System.IO.Path.GetFileName(localPath);
            if (backupPath != null)
            {
                ToastService.Show("File Conflict Resolved", 
                    $"Conflict on '{fileName}' - both versions saved.", 
                    NotificationType.Information);
            }
            else
            {
                ToastService.Show("File Conflict Resolved", 
                    $"Conflict on '{fileName}' - backup in version history.", 
                    NotificationType.Information);
            }
        });
    }

    private void OnUntrustedPeerDiscovered(Peer peer)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                // Get the main window as the owner for the dialog
                var mainWindow = global::Avalonia.Application.Current?.ApplicationLifetime is 
                    global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow
                    : null;

                if (mainWindow == null) return;

                var dialog = new Views.PairingDialog(peer, _pairingService);
                var result = await dialog.ShowDialog<bool?>(mainWindow);

                if (result == true)
                {
                    // User chose to trust this peer
                    _settings.TrustPeer(peer);
                    _settings.Save();
                    
                    ToastService.Show("Device Trusted", 
                        $"'{peer.Name}' is now a trusted device.", 
                        NotificationType.Success);
                }
                else
                {
                    ToastService.Show("Device Rejected", 
                        $"'{peer.Name}' was not trusted.", 
                        NotificationType.Information);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Pairing dialog error: {ex.Message}");
            }
        });
    }

    private void OnTransferProgress(FileTransfer transfer)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Simple status update for now
            var active = _transferService.Transfers.Where(t => t.Status == TransferStatus.InProgress).ToList();
            IsSyncing = active.Count > 0 || _syncService.IsRunning;
            SyncingFileCountText = active.Count > 0 ? $"{active.Count} active transfers" : "";

            if (active.Count > 0)
            {
                long totalBytes = active.Sum(t => t.FileSize);
                long transferred = active.Sum(t => t.BytesTransferred);
                SyncProgress = totalBytes > 0 ? (double)transferred / totalBytes * 100 : 0;
            }
            else
            {
                SyncProgress = 0;
            }
        });
    }

    private void ToggleSync()
    {
        _settings.IsSyncEnabled = !_settings.IsSyncEnabled;
        _settings.Save();
        ApplySettings(_settings);
        // Refresh UI properties
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseIconGeometry));
    }

    private async void PauseSyncWithReason()
    {
        try
        {
            var mainWindow = global::Avalonia.Application.Current?.ApplicationLifetime is 
                global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (mainWindow == null) return;

            var dialog = new Dialogs.PauseSyncDialog();
            var result = await dialog.ShowDialog<Dialogs.PauseSyncResult?>(mainWindow);

            if (result != null)
            {
                // Handle indefinite pause (0 minutes = until resume)
                var duration = result.DurationMinutes > 0 
                    ? TimeSpan.FromMinutes(result.DurationMinutes)
                    : TimeSpan.FromDays(365); // Effectively indefinite

                _settings.PauseSyncFor(duration, result.Reason);
                
                // Log the pause event
                var logMessage = result.DurationMinutes > 0
                    ? $"Sync paused for {result.DurationMinutes} minutes"
                    : "Sync paused indefinitely";
                    
                if (!string.IsNullOrEmpty(result.Reason))
                {
                    logMessage += $": {result.Reason}";
                }
                
                _activityLogService.LogInfo(logMessage);
                
                // Notify sync service to update status subscribers (dashboard, footer)
                _syncService.NotifyStatusChanged();
                
                // Refresh pause-related UI properties
                RefreshPauseState();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Pause dialog error: {ex.Message}");
        }
    }

    /// <summary>
    /// Resumes sync by clearing the pause timer.
    /// </summary>
    public void ResumeSync()
    {
        _settings.ResumeSync();
        
        // Notify sync service to update status in dashboard and footer
        _syncService.NotifyStatusChanged();
        
        // Refresh pause-related UI properties
        RefreshPauseState();
        
        // Log resume event
        _activityLogService.LogInfo("Sync resumed manually");
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

    public Settings Settings => _settings;

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

    private double _syncProgress;
    public double SyncProgress
    {
        get => _syncProgress;
        set => SetProperty(ref _syncProgress, value);
    }

    public bool IsPaused => !_settings.IsSyncEnabled;
    
    public Geometry HelpIconGeometry => StreamGeometry.Parse("M11,18H13V16H11V18M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M12,20C7.59,20 4,16.41 4,12C4,7.59 7.59,4 12,4C16.41,4 20,7.59 20,12C20,16.41 16.41,20 12,20M12,6A4,4 0 0,0 8,10H10A2,2 0 0,1 12,8A2,2 0 0,1 14,10C14,12 11,11.75 11,15H11.5L13,15C13,12.75 16,12.5 16,10A4,4 0 0,0 12,6Z");
    
    public Geometry ActivityLogIconGeometry => StreamGeometry.Parse("M19,3H14.82C14.4,1.84 13.3,1 12,1C10.7,1 9.6,1.84 9.18,3H5A2,2 0 0,0 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5A2,2 0 0,0 19,3M12,3A1,1 0 0,1 13,4A1,1 0 0,1 12,5A1,1 0 0,1 11,4A1,1 0 0,1 12,3M7,7H17V5H19V19H5V5H7V7Z");

    public Geometry PauseIconGeometry => StreamGeometry.Parse(IsPaused 
        ? "M8,5V19L19,12L8,5Z" // Play
        : "M14,19H18V5H14M6,19H10V5H6V19Z"); // Pause

    /// <summary>
    /// Gets the pause status text including remaining time and reason.
    /// </summary>
    public string? PauseStatusText
    {
        get
        {
            if (!_settings.IsSyncCurrentlyPaused) return null;
            
            var text = _settings.PauseRemainingDisplay;
            if (!string.IsNullOrEmpty(_settings.SyncPauseReason))
            {
                text = string.IsNullOrEmpty(text) 
                    ? $"Paused: {_settings.SyncPauseReason}"
                    : $"{text} • {_settings.SyncPauseReason}";
            }
            return text;
        }
    }

    /// <summary>
    /// Whether there is an active pause with a reason or time.
    /// </summary>
    public bool HasPauseStatus => !string.IsNullOrEmpty(PauseStatusText);

    /// <summary>
    /// Refreshes all pause-related UI properties (called when pause state changes).
    /// </summary>
    public void RefreshPauseState()
    {
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PauseIconGeometry));
        OnPropertyChanged(nameof(PauseStatusText));
        OnPropertyChanged(nameof(HasPauseStatus));
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

    public bool IsBandwidthSelected
    {
        get => _isBandwidthSelected;
        set => SetProperty(ref _isBandwidthSelected, value);
    }

    public bool IsStatsSelected
    {
        get => _isStatsSelected;
        set => SetProperty(ref _isStatsSelected, value);
    }

    /// <summary>
    /// Whether sync is currently paused due to schedule restrictions.
    /// </summary>
    public bool IsSyncPausedBySchedule => _settings.SyncSchedule.IsEnabled && !_settings.SyncSchedule.IsSyncAllowedNow;

    /// <summary>
    /// Display text for the current schedule status.
    /// </summary>
    public string ScheduleStatusText => _settings.SyncSchedule.StatusDisplay;

    /// <summary>
    /// The next time the schedule status will change.
    /// </summary>
    public DateTime? NextScheduleChange => _settings.SyncSchedule.NextChangeTime;

    /// <summary>
    /// Formatted text for when sync will resume/pause.
    /// </summary>
    public string? NextScheduleChangeText
    {
        get
        {
            var next = NextScheduleChange;
            if (next == null) return null;
            
            var diff = next.Value - DateTime.Now;
            if (diff.TotalMinutes < 60)
                return $"in {(int)diff.TotalMinutes} min";
            if (diff.TotalHours < 24)
                return $"in {(int)diff.TotalHours}h {diff.Minutes}m";
            return $"at {next.Value:ddd HH:mm}";
        }
    }

    #endregion

    #region Commands

    public ICommand NavigateToOverviewCommand { get; }
    public ICommand NavigateToFilesCommand { get; }
    public ICommand NavigateToPeersCommand { get; }
    public ICommand NavigateToBandwidthCommand { get; }
    public ICommand NavigateToStatsCommand { get; }
    public ICommand NavigateToSettingsCommand { get; }
    public ICommand ToggleSyncCommand { get; }
    public ICommand PauseSyncCommand { get; }
    public ICommand OpenActivityLogCommand { get; }
    public ICommand OpenConflictHistoryCommand { get; }
    public ICommand OpenTransferQueueCommand { get; }
    public ICommand RefreshOverviewCommand { get; }
    public ICommand FocusSearchCommand { get; }

    /// <summary>
    /// Event raised when Ctrl+F is pressed to focus the search box.
    /// </summary>
    public event Action? FocusSearchRequested;

    #endregion

    #region Navigation Methods

    private void ClearNavSelection()
    {
        IsOverviewSelected = false;
        IsFilesSelected = false;
        IsPeersSelected = false;
        IsBandwidthSelected = false;
        IsStatsSelected = false;
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

    private void NavigateToBandwidth()
    {
        ClearNavSelection();
        IsBandwidthSelected = true;
        CurrentView = BandwidthVM;
    }

    private void NavigateToStats()
    {
        ClearNavSelection();
        IsStatsSelected = true;
        CurrentView = StatsVM;
    }

    private void NavigateToSettings()
    {
        ClearNavSelection();
        IsSettingsSelected = true;
        IsSettingsSelected = true;
        CurrentView = SettingsVM;
    }

    private void RefreshOverview()
    {
        // Trigger OverviewVM to recalculate stats
        OverviewVM?.RequestRefresh();
    }

    private async void OpenActivityLog()
    {
        try
        {
            var mainWindow = global::Avalonia.Application.Current?.ApplicationLifetime is 
                global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (mainWindow == null) return;

            var activityDialog = new Swarm.Avalonia.Dialogs.ActivityLogDialog(_activityLogService);
            await activityDialog.ShowDialog(mainWindow);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open activity log: {ex.Message}");
        }
    }

    private async void OpenConflictHistory()
    {
        try
        {
            var mainWindow = global::Avalonia.Application.Current?.ApplicationLifetime is 
                global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (mainWindow == null) return;

            var conflictDialog = new Swarm.Avalonia.Dialogs.ConflictHistoryDialog(_conflictResolutionService);
            await conflictDialog.ShowDialog(mainWindow);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open conflict history: {ex.Message}");
        }
    }

    private async void OpenTransferQueue()
    {
        try
        {
            var mainWindow = global::Avalonia.Application.Current?.ApplicationLifetime is 
                global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (mainWindow == null) return;

            var transferDialog = new Swarm.Avalonia.Dialogs.TransferQueueDialog(_transferService);
            await transferDialog.ShowDialog(mainWindow);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open transfer queue: {ex.Message}");
        }
    }

    #endregion

    public void Dispose()
    {
        OverviewVM.Dispose();
        FilesVM.Dispose();
        _rescanService.Dispose();
        _activityLogService.Dispose();
        _syncService.Dispose();
        _versioningService.Dispose();
        _discoveryService.Dispose();
        _transferService.Dispose();
        _cryptoService.Dispose();
    }
}
