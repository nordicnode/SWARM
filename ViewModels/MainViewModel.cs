using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Swarm.Core;
using Swarm.Helpers;
using Swarm.Models;
using MessageBox = System.Windows.MessageBox;

namespace Swarm.ViewModels;

/// <summary>
/// Main ViewModel for the Swarm application.
/// Handles all UI state and command logic, making MainWindow.xaml.cs purely view code.
/// </summary>
public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Settings _settings;
    private readonly CryptoService _cryptoService;
    private readonly DiscoveryService _discoveryService;
    private readonly TransferService _transferService;
    private readonly VersioningService _versioningService;
    private readonly SyncService _syncService;
    private readonly IntegrityService _integrityService;
    private readonly Dispatcher _dispatcher;

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

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        // Load settings
        _settings = Settings.Load();

        // Initialize services
        _cryptoService = new CryptoService();
        _discoveryService = new DiscoveryService(_settings.LocalId, _cryptoService, _settings);
        _transferService = new TransferService(_settings, _cryptoService);
        _versioningService = new VersioningService(_settings);
        _syncService = new SyncService(_settings, _discoveryService, _transferService, _versioningService);
        _integrityService = new IntegrityService(_settings, _syncService);

        // Initialize commands
        SendFilesCommand = new AsyncRelayCommand(SendFilesAsync, CanSendFiles);
        ToggleSyncCommand = new RelayCommand(ToggleSync);
        ChangeSyncFolderCommand = new RelayCommand(ChangeSyncFolder);
        OpenSyncFolderCommand = new RelayCommand(OpenSyncFolder);
        ForceSyncCommand = new AsyncRelayCommand(ForceSync);
        OpenVersionHistoryCommand = new RelayCommand(OpenVersionHistory);

        // Wire up service events
        SubscribeToEvents();

        // Initialize UI state
        UpdateSyncUI();
    }

    private bool CanSendFiles(object? _) => SelectedPeer != null || Peers.Count > 0;

    #region Properties

    public ObservableCollection<Peer> Peers => _discoveryService.Peers;
    public ObservableCollection<FileTransfer> Transfers => _transferService.Transfers;
    public Settings Settings => _settings;
    public CryptoService CryptoService => _cryptoService;
    public DiscoveryService DiscoveryService => _discoveryService;
    public TransferService TransferService => _transferService;
    public SyncService SyncService => _syncService;
    public VersioningService VersioningService => _versioningService;
    public IntegrityService IntegrityService => _integrityService;

    public Peer? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            if (SetProperty(ref _selectedPeer, value))
            {
                SelectedPeerText = value != null ? $"Sending to: {value.Name}" : "";
                RelayCommand.RaiseCanExecuteChanged();
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

    #endregion

    #region Commands

    public ICommand SendFilesCommand { get; }
    public ICommand ToggleSyncCommand { get; }
    public ICommand ChangeSyncFolderCommand { get; }
    public ICommand OpenSyncFolderCommand { get; }
    public ICommand ForceSyncCommand { get; }
    public ICommand OpenVersionHistoryCommand { get; }

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

    // Event for view to handle opening version history dialog
    public event Action? OpenVersionHistoryRequested;

    #endregion

    #region Event Handlers

    private void SubscribeToEvents()
    {
        _discoveryService.Peers.CollectionChanged += (s, e) => _dispatcher.Invoke(UpdatePeerUI);
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
    }

    private void UpdatePeerUI()
    {
        var count = Peers.Count;
        PeerCountText = count == 1 ? "1 device found" : $"{count} devices found";
        EmptyPeersVisibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText = count == 0 ? "Scanning for peers..." : $"Connected to {count} device(s)";
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
        _dispatcher.Invoke(() =>
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
        _dispatcher.Invoke(() =>
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
        _dispatcher.Invoke(() => DiscoveryBindingFailed?.Invoke());
    }

    private void OnUntrustedPeerDiscovered(Peer peer)
    {
        _dispatcher.Invoke(() => UntrustedPeerDiscoveredEvent?.Invoke(peer));
    }

    private void OnIncomingFileRequest(string fileName, string senderName, long fileSize, Action<bool> callback)
    {
        _dispatcher.Invoke(() => IncomingFileRequestEvent?.Invoke(fileName, senderName, fileSize, callback));
    }

    private void OnTransferProgress(FileTransfer transfer)
    {
        _dispatcher.Invoke(() =>
        {
            EmptyTransfersVisibility = Visibility.Collapsed;
        });
    }

    private void OnTransferCompleted(FileTransfer transfer)
    {
        _dispatcher.Invoke(() => TransferCompletedEvent?.Invoke(transfer));
    }

    private void OnFileConflictDetected(string filePath, string? backupPath)
    {
        _dispatcher.Invoke(() => FileConflictDetectedEvent?.Invoke(filePath, backupPath));
    }

    #endregion

    #region INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        _syncService.Dispose();
        _versioningService.Dispose();
        _discoveryService.Dispose();
        _transferService.Dispose();
        _cryptoService.Dispose();
    }

    #endregion
}
