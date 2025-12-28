using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Swarm.Core;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Swarm.Core.Helpers;
using Swarm.Core.ViewModels;
using Swarm.Services;
using MessageBox = System.Windows.MessageBox;

namespace Swarm.ViewModels;

/// <summary>
/// Main ViewModel for the Swarm application.
/// Handles all UI state and command logic, making MainWindow.xaml.cs purely view code.
/// </summary>
public class MainViewModel : BaseViewModel, IDisposable
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
    private readonly WpfToastService _toastService;
    private readonly WpfDispatcher _wpfDispatcher;
    private readonly WpfPowerService _powerService;
    private readonly Dispatcher _uiDispatcher;

    private Peer? _selectedPeer;
    private string _peerCountText = "Scanning for peers...";
    private string _statusText = "Scanning for peers...";
    private string _syncStatusText = "Sync disabled";
    private string _selectedPeerText = "";
    private string _syncFolderDisplayPath = "";
    private double _syncProgressValue;
    private Visibility _syncProgressVisibility = Visibility.Collapsed;
    private Visibility _emptyPeersVisibility = Visibility.Visible;
    private Visibility _emptyTransfersVisibility = Visibility.Visible;
    private bool _isSyncEnabled;
    
    // Transfer speed tracking
    private string _uploadSpeedText = "↑ 0 B/s";
    private string _downloadSpeedText = "↓ 0 B/s";
    private string _transferSpeedText = "";
    private int _syncingFileCount = 0;
    private bool _isSyncing = false;
    private bool _isActivityFlyoutOpen = false;
    private long _lastBytesUploaded = 0;
    private long _lastBytesDownloaded = 0;
    private DateTime _lastSpeedCheck = DateTime.Now;
    private readonly System.Timers.Timer _speedTimer;

    public MainViewModel(Dispatcher dispatcher)
    {
        _uiDispatcher = dispatcher;
        _wpfDispatcher = new WpfDispatcher();
        _powerService = new WpfPowerService();
        _toastService = new WpfToastService();

        // Register power service and load settings
        Settings.RegisterPowerService(_powerService);
        _settings = Settings.Load();

        // Initialize services with dispatcher abstraction
        _cryptoService = new CryptoService();
        _discoveryService = new DiscoveryService(_settings.LocalId, _cryptoService, _settings, _wpfDispatcher);
        _transferService = new TransferService(_settings, _cryptoService, _wpfDispatcher);
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
        SendFilesCommand = new AsyncRelayCommand(SendFilesAsync, CanSendFiles);
        ToggleSyncCommand = new RelayCommand(ToggleSync);
        ChangeSyncFolderCommand = new RelayCommand(ChangeSyncFolder);
        OpenSyncFolderCommand = new RelayCommand(OpenSyncFolder);
        ForceSyncCommand = new AsyncRelayCommand(ForceSync);
        OpenVersionHistoryCommand = new RelayCommand(OpenVersionHistory);
        TogglePauseSyncCommand = new RelayCommand(TogglePauseSync);
        OpenActivityLogCommand = new RelayCommand(OpenActivityLog);
        ToggleActivityFlyoutCommand = new RelayCommand(_ => IsActivityFlyoutOpen = !IsActivityFlyoutOpen);
        
        // Navigation Commands
        NavigateToOverviewCommand = new RelayCommand(_ => CurrentView = OverviewVM);
        NavigateToFilesCommand = new RelayCommand(_ => CurrentView = FilesVM);
        NavigateToPeersCommand = new RelayCommand(_ => CurrentView = PeersVM);
        NavigateToSettingsCommand = new RelayCommand(_ => CurrentView = SettingsVM);
        
        // Initialize speed timer (1 second interval for real-time updates)
        _speedTimer = new System.Timers.Timer(1000);
        _speedTimer.AutoReset = true;
        
        // Hook core RelayCommand to WPF CommandManager
        RelayCommand.StaticCanExecuteChanged += (s, e) => CommandManager.InvalidateRequerySuggested();

        _speedTimer.Elapsed += (s, e) => _uiDispatcher.Invoke(UpdateTransferSpeeds);
        _speedTimer.Start();

        // Wire up service events
        SubscribeToEvents();

        // Initialize UI state
        UpdateSyncUI();
        
        // Set default view
        CurrentView = OverviewVM;
    }

    private bool CanSendFiles(object? _) => SelectedPeer != null || Peers.Count > 0;

    #region Properties

    // Sub-ViewModels
    public OverviewViewModel OverviewVM { get; }
    public FilesViewModel FilesVM { get; }
    public PeersViewModel PeersVM { get; }
    public SettingsViewModel SettingsVM { get; }

    private object? _currentView;
    public object? CurrentView
    {
        get => _currentView;
        set => SetProperty(ref _currentView, value);
    }

    public ObservableCollection<Peer> Peers => _discoveryService.Peers;
    public ObservableCollection<FileTransfer> Transfers => _transferService.Transfers;
    public Settings Settings => _settings;
    public CryptoService CryptoService => _cryptoService;
    public DiscoveryService DiscoveryService => _discoveryService;
    public TransferService TransferService => _transferService;
    public SyncService SyncService => _syncService;
    public VersioningService VersioningService => _versioningService;
    public IntegrityService IntegrityService => _integrityService;
    public RescanService RescanService => _rescanService;
    public ActivityLogService ActivityLogService => _activityLogService;
    public ConflictResolutionService ConflictResolutionService => _conflictResolutionService;
    public ShareLinkService ShareLinkService => _shareLinkService;
    public WpfToastService ToastService => _toastService;

    public Peer? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            if (SetProperty(ref _selectedPeer, value))
            {
                SelectedPeerText = value != null ? $"Sending to: {value.Name}" : "";
                OnPropertyChanged(nameof(HasSelectedPeer));
                RelayCommand.RaiseGlobalCanExecuteChanged();
            }
        }
    }

    public string PeerCountText
    {
        get => _peerCountText;
        private set => SetProperty(ref _peerCountText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string SyncStatusText
    {
        get => _syncStatusText;
        private set => SetProperty(ref _syncStatusText, value);
    }

    public string SelectedPeerText
    {
        get => _selectedPeerText;
        private set => SetProperty(ref _selectedPeerText, value);
    }

    public string SyncFolderDisplayPath
    {
        get => _syncFolderDisplayPath;
        private set => SetProperty(ref _syncFolderDisplayPath, value);
    }

    public double SyncProgressValue
    {
        get => _syncProgressValue;
        private set => SetProperty(ref _syncProgressValue, value);
    }

    public Visibility SyncProgressVisibility
    {
        get => _syncProgressVisibility;
        private set => SetProperty(ref _syncProgressVisibility, value);
    }

    public Visibility EmptyPeersVisibility
    {
        get => _emptyPeersVisibility;
        private set => SetProperty(ref _emptyPeersVisibility, value);
    }

    public Visibility EmptyTransfersVisibility
    {
        get => _emptyTransfersVisibility;
        private set => SetProperty(ref _emptyTransfersVisibility, value);
    }

    public bool IsSyncEnabled
    {
        get => _isSyncEnabled;
        private set => SetProperty(ref _isSyncEnabled, value);
    }

    public string SyncToggleButtonText => IsSyncEnabled ? "Disable Sync" : "Enable Sync";

    // Transfer speed properties for title bar
    public string UploadSpeedText
    {
        get => _uploadSpeedText;
        private set => SetProperty(ref _uploadSpeedText, value);
    }
    
    public string DownloadSpeedText
    {
        get => _downloadSpeedText;
        private set => SetProperty(ref _downloadSpeedText, value);
    }
    
    public string TransferSpeedText
    {
        get => _transferSpeedText;
        private set => SetProperty(ref _transferSpeedText, value);
    }
    
    public int SyncingFileCount
    {
        get => _syncingFileCount;
        private set
        {
            if (SetProperty(ref _syncingFileCount, value))
            {
                OnPropertyChanged(nameof(SyncingFileCountText));
                OnPropertyChanged(nameof(IsSyncingVisible));
            }
        }
    }
    
    public string SyncingFileCountText => _syncingFileCount > 0 ? _syncingFileCount.ToString() : "";
    public Visibility IsSyncingVisible => _isSyncing ? Visibility.Visible : Visibility.Collapsed;
    
    public bool IsSyncing
    {
        get => _isSyncing;
        private set
        {
            if (SetProperty(ref _isSyncing, value))
            {
                OnPropertyChanged(nameof(IsSyncingVisible));
            }
        }
    }
    
    public bool IsActivityFlyoutOpen
    {
        get => _isActivityFlyoutOpen;
        set => SetProperty(ref _isActivityFlyoutOpen, value);
    }

    // Computed properties for new layout
    public string PeerCountDisplay => Peers.Count == 0 ? "" : $"({Peers.Count})";
    public string SyncFolderName => Path.GetFileName(_settings.SyncFolderPath) ?? "Sync Folder";
    public string TransferCountDisplay => Transfers.Count == 0 ? "" : $"{Transfers.Count} active";
    public bool HasSelectedPeer => SelectedPeer != null;

    // Transfer speed tracking
    private string _uploadSpeedDisplay = "";
    private string _downloadSpeedDisplay = "";
    private bool _hasActiveTransfers;
    private int _pendingTransferCount;

    public string UploadSpeedDisplay
    {
        get => _uploadSpeedDisplay;
        private set => SetProperty(ref _uploadSpeedDisplay, value);
    }

    public string DownloadSpeedDisplay
    {
        get => _downloadSpeedDisplay;
        private set => SetProperty(ref _downloadSpeedDisplay, value);
    }

    public bool HasActiveTransfers
    {
        get => _hasActiveTransfers;
        private set => SetProperty(ref _hasActiveTransfers, value);
    }

    public int PendingTransferCount
    {
        get => _pendingTransferCount;
        private set => SetProperty(ref _pendingTransferCount, value);
    }

    public string TransferStatusDisplay
    {
        get
        {
            if (!HasActiveTransfers) return "";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(UploadSpeedDisplay)) parts.Add($"↑ {UploadSpeedDisplay}");
            if (!string.IsNullOrEmpty(DownloadSpeedDisplay)) parts.Add($"↓ {DownloadSpeedDisplay}");
            return string.Join(" | ", parts);
        }
    }

    #endregion

    #region Commands

    public ICommand SendFilesCommand { get; }
    public ICommand ToggleSyncCommand { get; }
    public ICommand ChangeSyncFolderCommand { get; }
    public ICommand OpenSyncFolderCommand { get; }
    public ICommand ForceSyncCommand { get; }
    public ICommand OpenVersionHistoryCommand { get; }
    public ICommand TogglePauseSyncCommand { get; }
    public ICommand OpenActivityLogCommand { get; }
    public ICommand ToggleActivityFlyoutCommand { get; }
    public ICommand NavigateToOverviewCommand { get; }
    public ICommand NavigateToFilesCommand { get; }
    public ICommand NavigateToPeersCommand { get; }
    public ICommand NavigateToSettingsCommand { get; }

    // Pause state properties
    public bool IsSyncPaused => _settings.IsSyncCurrentlyPaused;
    public string PauseButtonText => IsSyncPaused ? "Resume" : "Pause";
    public string PauseStatusDisplay => IsSyncPaused ? _settings.PauseRemainingDisplay : "";
    public Visibility PauseStatusVisibility => IsSyncPaused ? Visibility.Visible : Visibility.Collapsed;

    #endregion

    #region Public Methods

    public void StartServices(int transferPort)
    {
        _transferService.Start();
        _discoveryService.Start(transferPort);

        // Update discovery service settings
        _discoveryService.LocalName = _settings.DeviceName;
        _discoveryService.IsSyncEnabled = _settings.IsSyncEnabled;

        // Start sync if enabled
        if (_settings.IsSyncEnabled)
        {
            _syncService.Start();
            _rescanService.Start();
        }

        UpdatePeerUI();
        UpdateSyncUI();
    }

    public void Initialize()
    {
        StartServices(_transferService.ListenPort);
    }

    public async Task SendFilesAsync(string[] filePaths)
    {
        var targetPeer = SelectedPeer;
        
        if (targetPeer == null)
        {
            if (Peers.Count == 0)
            {
                MessageBox.Show("No devices found. Make sure another device is running Swarm on the same network.",
                    "No Devices", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Auto-select first peer if only one
            if (Peers.Count == 1)
            {
                targetPeer = Peers[0];
            }
            else
            {
                MessageBox.Show("Please select a device to send to.", "Select Device", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        EmptyTransfersVisibility = Visibility.Collapsed;

        foreach (var filePath in filePaths)
        {
            if (File.Exists(filePath))
            {
                try
                {
                    await _transferService.SendFile(targetPeer, filePath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to send {Path.GetFileName(filePath)}:\n{ex.Message}",
                        "Transfer Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
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

        UpdateSyncUI();
    }

    #endregion

    #region Command Implementations

    private async Task SendFilesAsync(object? parameter)
    {
        if (parameter is string[] files)
        {
            await SendFilesAsync(files);
        }
    }

    private void ToggleSync(object? parameter)
    {
        var newState = !_syncService.IsEnabled;
        _syncService.SetEnabled(newState);
        _discoveryService.IsSyncEnabled = newState;
        UpdateSyncUI();
    }

    private void ChangeSyncFolder(object? parameter)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select Sync Folder",
            InitialDirectory = _settings.SyncFolderPath,
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _syncService.SetSyncFolderPath(dialog.SelectedPath);
            UpdateSyncUI();
        }
    }

    private void OpenSyncFolder(object? parameter)
    {
        try
        {
            _settings.EnsureSyncFolderExists();
            Process.Start(new ProcessStartInfo
            {
                FileName = _settings.SyncFolderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open folder:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ForceSync()
    {
        await _syncService.ForceSyncAsync();
    }

    private void OpenVersionHistory(object? parameter)
    {
        // Open version history dialog
        OpenVersionHistoryRequested?.Invoke();
    }

    private void TogglePauseSync(object? parameter)
    {
        if (IsSyncPaused)
        {
            _settings.ResumeSync();
        }
        else
        {
            // Default pause for 1 hour
            _settings.PauseSyncFor(TimeSpan.FromHours(1));
        }
        
        // Notify UI of changes
        OnPropertyChanged(nameof(IsSyncPaused));
        OnPropertyChanged(nameof(PauseButtonText));
        OnPropertyChanged(nameof(PauseStatusDisplay));
        OnPropertyChanged(nameof(PauseStatusVisibility));
    }

    private void OpenActivityLog(object? parameter)
    {
        OpenActivityLogRequested?.Invoke();
    }



    // Events for view to handle opening dialogs
    public event Action? OpenVersionHistoryRequested;
    public event Action? OpenActivityLogRequested;

    #endregion

    #region Event Handlers

    private void SubscribeToEvents()
    {
        _discoveryService.Peers.CollectionChanged += (s, e) => _uiDispatcher.Invoke(UpdatePeerUI);
        _discoveryService.BindingFailed += OnDiscoveryBindingFailed;
        _discoveryService.UntrustedPeerDiscovered += OnUntrustedPeerDiscovered;
        
        _transferService.IncomingFileRequest += OnIncomingFileRequest;
        _transferService.TransferProgress += OnTransferProgress;
        _transferService.TransferCompleted += OnTransferCompleted;

        _syncService.SyncStatusChanged += OnSyncStatusChanged;
        _syncService.FileChanged += OnSyncFileChanged;
        _syncService.IncomingSyncFile += OnIncomingSyncFile;
        _syncService.SyncProgressChanged += OnSyncProgressChanged;
        _syncService.FileConflictDetected += OnFileConflictDetected;
        
        // Wire RescanService to SyncService buffer overflow events
        _syncService.RescanRequested += OnRescanRequested;
        
        // Wire ConflictResolutionService to show dialog when needed
        _conflictResolutionService.ConflictNeedsResolution += OnConflictNeedsResolution;
    }

    private void OnRescanRequested()
    {
        // Trigger a deep rescan when FileSystemWatcher buffer overflows
        _ = Task.Run(async () =>
        {
            _activityLogService.LogWarning("FileSystemWatcher buffer overflow - triggering deep rescan");
            await _rescanService.RescanAsync(RescanMode.DeepWithHash);
        });
    }

    private async Task<ConflictChoice?> OnConflictNeedsResolution(FileConflict conflict)
    {
        ConflictChoice? result = null;
        
        await _uiDispatcher.InvokeAsync(() =>
        {
            var dialog = new UI.ConflictResolutionDialog(conflict);
            dialog.ShowDialog();
            result = dialog.Result;
        });
        
        return result;
    }

    private void UpdatePeerUI()
    {
        var count = Peers.Count;
        PeerCountText = count == 1 ? "1 device found" : $"{count} devices found";
        EmptyPeersVisibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText = count == 0 ? "Scanning for peers..." : $"Connected to {count} device(s)";
        OnPropertyChanged(nameof(PeerCountDisplay));
    }

    private void UpdateSyncUI()
    {
        IsSyncEnabled = _syncService.IsEnabled;
        SyncStatusText = IsSyncEnabled ? "Sync enabled" : "Sync disabled";
        OnPropertyChanged(nameof(SyncToggleButtonText));

        var docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        SyncFolderDisplayPath = _settings.SyncFolderPath.StartsWith(docPath)
            ? _settings.SyncFolderPath.Replace(docPath, "Documents")
            : _settings.SyncFolderPath;
    }

    private void OnSyncStatusChanged(string status)
    {
        _uiDispatcher.Invoke(() =>
        {
            if (SyncProgressVisibility == Visibility.Collapsed || status.Contains("complete") || status.Contains("disabled"))
            {
                SyncStatusText = status;
                if (status.Contains("complete") || status.Contains("disabled"))
                {
                    SyncProgressVisibility = Visibility.Collapsed;
                }
            }
        });
    }

    private void OnSyncProgressChanged(SyncProgress progress)
    {
        _uiDispatcher.Invoke(() =>
        {
            if (progress.TotalFiles > 0 && progress.CompletedFiles < progress.TotalFiles)
            {
                SyncProgressVisibility = Visibility.Visible;
                SyncProgressValue = progress.CurrentFilePercent;
                SyncStatusText = $"Syncing {progress.CompletedFiles + 1}/{progress.TotalFiles}: {Path.GetFileName(progress.CurrentFileName)}";
            }
            else
            {
                SyncProgressVisibility = Visibility.Collapsed;
                SyncStatusText = "Sync complete";
            }
        });
    }

    private void OnSyncFileChanged(SyncedFile file)
    {
        // File changed event - could add visual feedback here
        Debug.WriteLine($"File changed: {file.RelativePath}");
    }

    private void OnIncomingSyncFile(SyncedFile file)
    {
        Debug.WriteLine($"Synced: {file.RelativePath}");
    }

    // Event callbacks that need UI interaction - delegated back to View
    public event Action? DiscoveryBindingFailed;
    public event Action<Peer>? UntrustedPeerDiscoveredEvent;
    public event Action<string, string, long, Action<bool>>? IncomingFileRequestEvent;
    public event Action<FileTransfer>? TransferCompletedEvent;
    public event Action<string, string?>? FileConflictDetectedEvent;

    private void OnDiscoveryBindingFailed()
    {
        _uiDispatcher.Invoke(() => DiscoveryBindingFailed?.Invoke());
    }

    private void OnUntrustedPeerDiscovered(Peer peer)
    {
        _uiDispatcher.Invoke(() => UntrustedPeerDiscoveredEvent?.Invoke(peer));
    }

    private void OnIncomingFileRequest(string fileName, string senderName, long fileSize, Action<bool> callback)
    {
        _uiDispatcher.Invoke(() => IncomingFileRequestEvent?.Invoke(fileName, senderName, fileSize, callback));
    }

    private void OnTransferProgress(FileTransfer transfer)
    {
        _uiDispatcher.Invoke(() =>
        {
            EmptyTransfersVisibility = Visibility.Collapsed;
            UpdateTransferStats();
        });
    }

    private void UpdateTransferStats()
    {
        var activeTransfers = Transfers.Where(t => t.Status == TransferStatus.InProgress).ToList();
        HasActiveTransfers = activeTransfers.Any();
        PendingTransferCount = Transfers.Count(t => t.Status == TransferStatus.Pending);
        OnPropertyChanged(nameof(TransferCountDisplay));

        if (!HasActiveTransfers)
        {
            UploadSpeedDisplay = "";
            DownloadSpeedDisplay = "";
            OnPropertyChanged(nameof(TransferStatusDisplay));
            return;
        }

        // Calculate aggregate speeds
        double uploadBytesPerSec = 0;
        double downloadBytesPerSec = 0;

        foreach (var t in activeTransfers)
        {
            var elapsed = (DateTime.Now - t.StartTime).TotalSeconds;
            if (elapsed < 0.1) continue;
            var speed = t.BytesTransferred / elapsed;

            if (t.Direction == TransferDirection.Outgoing)
                uploadBytesPerSec += speed;
            else
                downloadBytesPerSec += speed;
        }

        UploadSpeedDisplay = uploadBytesPerSec > 0 ? FileHelpers.FormatBytes(uploadBytesPerSec) + "/s" : "";
        DownloadSpeedDisplay = downloadBytesPerSec > 0 ? FileHelpers.FormatBytes(downloadBytesPerSec) + "/s" : "";
        OnPropertyChanged(nameof(TransferStatusDisplay));
    }

    private void OnTransferCompleted(FileTransfer transfer)
    {
        _uiDispatcher.Invoke(() =>
        {
            TransferCompletedEvent?.Invoke(transfer);
            UpdateTransferStats();
            
            // Show empty state if no more transfers
            if (!Transfers.Any(t => t.Status == TransferStatus.InProgress || t.Status == TransferStatus.Pending))
            {
                EmptyTransfersVisibility = Visibility.Visible;
            }
        });
    }

    private void OnFileConflictDetected(string filePath, string? backupPath)
    {
        _uiDispatcher.Invoke(() => FileConflictDetectedEvent?.Invoke(filePath, backupPath));
    }

    #endregion



    #region IDisposable

    private void UpdateTransferSpeeds()
    {
        try
        {
            // Calculate speeds based on active transfers
            var activeTransfers = Transfers.Where(t => t.Status == TransferStatus.InProgress).ToList();
            
            long uploadBytes = 0;
            long downloadBytes = 0;
            
            foreach (var transfer in activeTransfers)
            {
                if (transfer.Direction == TransferDirection.Outgoing)
                    uploadBytes += transfer.BytesTransferred;
                else if (transfer.Direction == TransferDirection.Incoming)
                    downloadBytes += transfer.BytesTransferred;
            }
            
            // Calculate speed (bytes per second)
            var elapsed = (DateTime.Now - _lastSpeedCheck).TotalSeconds;
            if (elapsed > 0)
            {
                var uploadSpeed = (uploadBytes - _lastBytesUploaded) / elapsed;
                var downloadSpeed = (downloadBytes - _lastBytesDownloaded) / elapsed;
                
                // Always show speed values (0 B/s when idle)
                UploadSpeedText = uploadSpeed > 0 ? $"↑ {FileHelpers.FormatBytes((long)uploadSpeed)}/s" : "0 B/s";
                DownloadSpeedText = downloadSpeed > 0 ? $"↓ {FileHelpers.FormatBytes((long)downloadSpeed)}/s" : "0 B/s";
                
                // Combined display for title bar (only show if there's actual activity)
                var parts = new List<string>();
                if (uploadSpeed > 0) parts.Add($"↑ {FileHelpers.FormatBytes((long)uploadSpeed)}/s");
                if (downloadSpeed > 0) parts.Add($"↓ {FileHelpers.FormatBytes((long)downloadSpeed)}/s");
                TransferSpeedText = parts.Count > 0 ? string.Join(" | ", parts) : "";
            }
            
            _lastBytesUploaded = uploadBytes;
            _lastBytesDownloaded = downloadBytes;
            _lastSpeedCheck = DateTime.Now;
            
            // Update syncing status
            SyncingFileCount = activeTransfers.Count;
            IsSyncing = activeTransfers.Count > 0 || _syncService.IsRunning;
        }
        catch
        {
            // Ignore errors in speed calculation
        }
    }
    
    public void Dispose()
    {
        _speedTimer?.Stop();
        _speedTimer?.Dispose();
        _rescanService.Dispose();
        _activityLogService.Dispose();
        _syncService.Dispose();
        _versioningService.Dispose();
        _discoveryService.Dispose();
        _transferService.Dispose();
        _cryptoService.Dispose();
    }

    #endregion
}
