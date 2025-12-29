using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Swarm.Core.ViewModels;

namespace Swarm.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the Settings view.
/// </summary>
public class SettingsViewModel : ViewModelBase
{
    private readonly Settings _settings = null!;
    private readonly SyncService _syncService = null!;
    private readonly DiscoveryService _discoveryService = null!;
    private readonly IntegrityService _integrityService = null!;
    private readonly RescanService _rescanService = null!;
    private readonly ActivityLogService _activityLogService = null!;
    private readonly VersioningService _versioningService = null!;

    public event Action<Settings>? SettingsChanged;

    // General
    private string _deviceName = "";
    private bool _startMinimized;
    private bool _closeToTray = true;
    private bool _showNotifications = true;
    private bool _showTransferComplete = true;

    // Sync
    private bool _syncEnabled = true;
    private string _syncFolderPath = "";
    private bool _pauseOnBattery;

    // Transfers
    private string _downloadPath = "";
    private bool _autoAcceptFromTrusted;
    private long _maxDownloadSpeedKBps;
    private long _maxUploadSpeedKBps;

    // Versioning
    private bool _versioningEnabled = true;
    private int _maxVersionsPerFile = 10;
    private int _maxVersionAgeDays = 30;

    // Trusted Peers
    private ObservableCollection<TrustedPeer> _trustedPeers = new();
    private TrustedPeer? _selectedPeer;

    // Selective Sync - Excluded Folders
    private ObservableCollection<string> _excludedFolders = new();
    private string? _selectedExcludedFolder;
    private string? _excludedFolderError;

    // Change tracking
    private bool _hasUnsavedChanges;

    public SettingsViewModel() {
        // Design-time
    }

    public SettingsViewModel(
        Settings settings,
        SyncService syncService,
        DiscoveryService discoveryService,
        IntegrityService integrityService,
        RescanService rescanService,
        ActivityLogService activityLogService,
        VersioningService versioningService)
    {
        _settings = settings;
        _syncService = syncService;
        _discoveryService = discoveryService;
        _integrityService = integrityService;
        _rescanService = rescanService;
        _activityLogService = activityLogService;
        _versioningService = versioningService;

        LoadSettings();

        // Initialize commands
        BrowseSyncFolderCommand = new RelayCommand(BrowseSyncFolder);
        BrowseDownloadPathCommand = new RelayCommand(BrowseDownloadPath);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        ResetSettingsCommand = new RelayCommand(ResetSettings);
        RemoveTrustedPeerCommand = new RelayCommand(RemoveSelectedPeer, CanRemoveSelectedPeer);
        AddExcludedFolderCommand = new RelayCommand(AddExcludedFolder);
        RemoveExcludedFolderCommand = new RelayCommand(RemoveExcludedFolder, CanRemoveExcludedFolder);
    }

    private void LoadSettings()
    {
        // General
        DeviceName = _settings.DeviceName;
        StartMinimized = _settings.StartMinimized;
        CloseToTray = _settings.CloseToTray;
        ShowNotifications = _settings.NotificationsEnabled;
        ShowTransferComplete = _settings.ShowTransferComplete;

        // Sync
        SyncEnabled = _settings.IsSyncEnabled;
        SyncFolderPath = _settings.SyncFolderPath;
        PauseOnBattery = _settings.PauseOnBattery;

        // Transfers
        DownloadPath = _settings.DownloadPath;
        AutoAcceptFromTrusted = _settings.AutoAcceptFromTrusted;
        MaxDownloadSpeedKBps = _settings.MaxDownloadSpeedKBps;
        MaxUploadSpeedKBps = _settings.MaxUploadSpeedKBps;

        // Versioning
        VersioningEnabled = _settings.VersioningEnabled;
        MaxVersionsPerFile = _settings.MaxVersionsPerFile;
        MaxVersionAgeDays = _settings.MaxVersionAgeDays;

        // Trusted Peers
        TrustedPeers = new ObservableCollection<TrustedPeer>(_settings.TrustedPeers);

        // Excluded Folders
        ExcludedFolders = new ObservableCollection<string>(_settings.ExcludedFolders);
    }

    #region General Properties

    public string DeviceName
    {
        get => _deviceName;
        set => SetProperty(ref _deviceName, value);
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set => SetProperty(ref _startMinimized, value);
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set => SetProperty(ref _closeToTray, value);
    }

    public bool ShowNotifications
    {
        get => _showNotifications;
        set => SetProperty(ref _showNotifications, value);
    }

    public bool ShowTransferComplete
    {
        get => _showTransferComplete;
        set => SetProperty(ref _showTransferComplete, value);
    }

    #endregion

    #region Sync Properties

    public bool SyncEnabled
    {
        get => _syncEnabled;
        set => SetProperty(ref _syncEnabled, value);
    }

    public string SyncFolderPath
    {
        get => _syncFolderPath;
        set => SetProperty(ref _syncFolderPath, value);
    }

    public bool PauseOnBattery
    {
        get => _pauseOnBattery;
        set => SetProperty(ref _pauseOnBattery, value);
    }

    #endregion

    #region Transfer Properties

    public string DownloadPath
    {
        get => _downloadPath;
        set => SetProperty(ref _downloadPath, value);
    }

    public bool AutoAcceptFromTrusted
    {
        get => _autoAcceptFromTrusted;
        set => SetProperty(ref _autoAcceptFromTrusted, value);
    }

    public long MaxDownloadSpeedKBps
    {
        get => _maxDownloadSpeedKBps;
        set => SetProperty(ref _maxDownloadSpeedKBps, value);
    }

    public long MaxUploadSpeedKBps
    {
        get => _maxUploadSpeedKBps;
        set => SetProperty(ref _maxUploadSpeedKBps, value);
    }

    public string MaxDownloadSpeedDisplay => MaxDownloadSpeedKBps == 0 ? "Unlimited" : FormatSpeed(MaxDownloadSpeedKBps);
    public string MaxUploadSpeedDisplay => MaxUploadSpeedKBps == 0 ? "Unlimited" : FormatSpeed(MaxUploadSpeedKBps);

    private static string FormatSpeed(long kbps) => kbps >= 1024 ? $"{kbps / 1024} MB/s" : $"{kbps} KB/s";

    #endregion

    #region Versioning Properties

    public bool VersioningEnabled
    {
        get => _versioningEnabled;
        set => SetProperty(ref _versioningEnabled, value);
    }

    public int MaxVersionsPerFile
    {
        get => _maxVersionsPerFile;
        set => SetProperty(ref _maxVersionsPerFile, value);
    }

    public int MaxVersionAgeDays
    {
        get => _maxVersionAgeDays;
        set => SetProperty(ref _maxVersionAgeDays, value);
    }

    public string MaxVersionAgeDisplay => MaxVersionAgeDays == 0 ? "Forever" : $"{MaxVersionAgeDays} days";

    #endregion

    #region Trusted Peers Properties

    public ObservableCollection<TrustedPeer> TrustedPeers
    {
        get => _trustedPeers;
        set => SetProperty(ref _trustedPeers, value);
    }

    public TrustedPeer? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            if (SetProperty(ref _selectedPeer, value))
            {
                RelayCommand.RaiseGlobalCanExecuteChanged();
            }
        }
    }

    #endregion

    #region Excluded Folders Properties

    public ObservableCollection<string> ExcludedFolders
    {
        get => _excludedFolders;
        set => SetProperty(ref _excludedFolders, value);
    }

    public string? SelectedExcludedFolder
    {
        get => _selectedExcludedFolder;
        set
        {
            if (SetProperty(ref _selectedExcludedFolder, value))
            {
                RelayCommand.RaiseGlobalCanExecuteChanged();
            }
        }
    }

    public string? ExcludedFolderError
    {
        get => _excludedFolderError;
        set => SetProperty(ref _excludedFolderError, value);
    }

    #endregion

    #region Commands

    public ICommand BrowseSyncFolderCommand { get; } = null!;
    public ICommand BrowseDownloadPathCommand { get; } = null!;
    public ICommand SaveSettingsCommand { get; } = null!;
    public ICommand ResetSettingsCommand { get; } = null!;
    public ICommand RemoveTrustedPeerCommand { get; } = null!;
    public ICommand AddExcludedFolderCommand { get; } = null!;
    public ICommand RemoveExcludedFolderCommand { get; } = null!;

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => SetProperty(ref _hasUnsavedChanges, value);
    }

    #endregion

    #region Methods

    private async void BrowseSyncFolder()
    {
        var topLevel = GetTopLevel();
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Sync Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            SyncFolderPath = folders[0].Path.LocalPath;
        }
    }

    private async void BrowseDownloadPath()
    {
        var topLevel = GetTopLevel();
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Download Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            DownloadPath = folders[0].Path.LocalPath;
        }
    }

    private bool CanRemoveSelectedPeer() => SelectedPeer != null;

    private void RemoveSelectedPeer()
    {
        if (SelectedPeer != null)
        {
            TrustedPeers.Remove(SelectedPeer);
            SelectedPeer = null;
        }
    }

    private void SaveSettings()
    {
        // General
        _settings.DeviceName = DeviceName;
        _settings.StartMinimized = StartMinimized;
        _settings.CloseToTray = CloseToTray;
        _settings.NotificationsEnabled = ShowNotifications;
        _settings.ShowTransferComplete = ShowTransferComplete;

        // Sync
        _settings.IsSyncEnabled = SyncEnabled;
        if (_settings.SyncFolderPath != SyncFolderPath)
        {
            _syncService.SetSyncFolderPath(SyncFolderPath);
        }
        _settings.PauseOnBattery = PauseOnBattery;

        // Transfers
        _settings.DownloadPath = DownloadPath;
        _settings.AutoAcceptFromTrusted = AutoAcceptFromTrusted;
        _settings.MaxDownloadSpeedKBps = MaxDownloadSpeedKBps;
        _settings.MaxUploadSpeedKBps = MaxUploadSpeedKBps;

        // Versioning
        _settings.VersioningEnabled = VersioningEnabled;
        _settings.MaxVersionsPerFile = MaxVersionsPerFile;
        _settings.MaxVersionAgeDays = MaxVersionAgeDays;

        // Trusted Peers
        _settings.TrustedPeers.Clear();
        foreach (var peer in TrustedPeers)
        {
            _settings.TrustedPeers.Add(new TrustedPeer { Id = peer.Id, Name = peer.Name });
        }

        // Excluded Folders
        _settings.ExcludedFolders.Clear();
        foreach (var folder in ExcludedFolders)
        {
            _settings.ExcludedFolders.Add(folder);
        }

        _settings.Save();

        HasUnsavedChanges = false;
        SettingsChanged?.Invoke(_settings);
    }

    /// <summary>
    /// Resets all settings to their last saved values (discards changes).
    /// </summary>
    private void ResetSettings()
    {
        LoadSettings();
        HasUnsavedChanges = false;
    }

    private async void AddExcludedFolder()
    {
        ExcludedFolderError = null; // Clear previous error
        
        var topLevel = GetTopLevel();
        if (topLevel == null) return;

        // Validate sync folder is set
        if (string.IsNullOrEmpty(_settings.SyncFolderPath))
        {
            ExcludedFolderError = "Please set a sync folder first.";
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Folder to Exclude",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            var path = folders[0].Path.LocalPath;
            
            // Validate: folder must be inside sync folder
            if (!path.StartsWith(_settings.SyncFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                ExcludedFolderError = "Folder must be inside the sync folder.";
                return;
            }
            
            var relativePath = path.Substring(_settings.SyncFolderPath.Length)
                .TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            
            // Validate: not empty (can't exclude root)
            if (string.IsNullOrEmpty(relativePath))
            {
                ExcludedFolderError = "Cannot exclude the root sync folder.";
                return;
            }
            
            // Validate: not already excluded
            if (ExcludedFolders.Contains(relativePath))
            {
                ExcludedFolderError = "This folder is already excluded.";
                return;
            }
            
            ExcludedFolders.Add(relativePath);
        }
    }

    private bool CanRemoveExcludedFolder() => !string.IsNullOrEmpty(SelectedExcludedFolder);

    private void RemoveExcludedFolder()
    {
        if (SelectedExcludedFolder != null)
        {
            ExcludedFolders.Remove(SelectedExcludedFolder);
            SelectedExcludedFolder = null;
        }
    }

    private global::Avalonia.Controls.TopLevel? GetTopLevel()
    {
        if (global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return global::Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow);
        }
        return null;
    }

    #endregion
}
