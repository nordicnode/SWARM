using System;
using System.Windows.Input;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Swarm.Core.Services;

namespace Swarm.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the Settings view.
/// </summary>
public class SettingsViewModel : ViewModelBase
{
    private readonly Settings _settings;
    private readonly SyncService _syncService;
    private readonly DiscoveryService _discoveryService;
    private readonly IntegrityService _integrityService;
    private readonly RescanService _rescanService;
    private readonly ActivityLogService _activityLogService;
    private readonly VersioningService _versioningService;

    public event Action<Settings>? SettingsChanged;

    private string _deviceName = "";
    private string _syncFolderPath = "";
    private bool _startWithSystem;
    private bool _pauseOnBattery;
    private bool _showNotifications;

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

        // Load settings
        DeviceName = _settings.DeviceName;
        SyncFolderPath = _settings.SyncFolderPath;
        StartWithSystem = _settings.StartMinimized; // Assuming mapping
        PauseOnBattery = _settings.PauseOnBattery;
        ShowNotifications = _settings.NotificationsEnabled;

        // Initialize commands
        BrowseSyncFolderCommand = new RelayCommand(BrowseSyncFolder);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
    }

    #region Properties

    public string DeviceName
    {
        get => _deviceName;
        set => SetProperty(ref _deviceName, value);
    }

    public string SyncFolderPath
    {
        get => _syncFolderPath;
        set => SetProperty(ref _syncFolderPath, value);
    }

    public bool StartWithSystem
    {
        get => _startWithSystem;
        set => SetProperty(ref _startWithSystem, value);
    }

    public bool PauseOnBattery
    {
        get => _pauseOnBattery;
        set => SetProperty(ref _pauseOnBattery, value);
    }

    public bool ShowNotifications
    {
        get => _showNotifications;
        set => SetProperty(ref _showNotifications, value);
    }

    #endregion

    #region Commands

    public ICommand BrowseSyncFolderCommand { get; }
    public ICommand SaveSettingsCommand { get; }

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

    private void SaveSettings()
    {
        _settings.DeviceName = DeviceName;

        // If sync folder changed
        if (_settings.SyncFolderPath != SyncFolderPath)
        {
            _syncService.SetSyncFolderPath(SyncFolderPath);
        }

        _settings.StartMinimized = StartWithSystem;
        _settings.PauseOnBattery = PauseOnBattery;
        _settings.NotificationsEnabled = ShowNotifications;

        _settings.Save();

        SettingsChanged?.Invoke(_settings);
    }

    private Avalonia.Controls.TopLevel? GetTopLevel()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow);
        }
        return null;
    }

    #endregion
}
