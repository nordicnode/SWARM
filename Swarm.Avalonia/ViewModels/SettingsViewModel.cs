using System.Windows.Input;

namespace Swarm.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the Settings view.
/// </summary>
public class SettingsViewModel : ViewModelBase
{
    private string _deviceName = "";
    private string _syncFolderPath = "";
    private bool _startWithSystem;
    private bool _pauseOnBattery;
    private bool _showNotifications = true;

    public SettingsViewModel()
    {
        // Initialize commands
        BrowseSyncFolderCommand = new RelayCommand(BrowseSyncFolder);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        
        // TODO: Load actual settings from Swarm.Core
        DeviceName = Environment.MachineName;
        SyncFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
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
        // TODO: Implement folder browser dialog using Avalonia's StorageProvider
    }

    private void SaveSettings()
    {
        // TODO: Save settings using Swarm.Core Settings class
    }

    #endregion
}
