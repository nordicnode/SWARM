using System.Windows.Input;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Swarm.Core.ViewModels;

namespace Swarm.ViewModels;

public class SettingsViewModel : BaseViewModel
{
    private readonly Settings _settings;
    private readonly SyncService _syncService;
    private readonly DiscoveryService _discoveryService;

    public Settings Settings => _settings;
    public SyncService SyncService => _syncService;
    public DiscoveryService DiscoveryService => _discoveryService;
    
    // Additional services for SettingsView code-behind
    public IntegrityService? IntegrityService { get; }
    public RescanService? RescanService { get; }
    public ActivityLogService? ActivityLogService { get; }
    public VersioningService? VersioningService { get; }
    
    public event Action<Settings>? SettingsChanged;
    public void NotifySettingsChanged(Settings settings) => SettingsChanged?.Invoke(settings);

    public ICommand SaveCommand { get; }
    public ICommand ResetCommand { get; }

    public SettingsViewModel(
        Settings settings, 
        SyncService syncService, 
        DiscoveryService discoveryService,
        IntegrityService? integrityService = null,
        RescanService? rescanService = null,
        ActivityLogService? activityLogService = null,
        VersioningService? versioningService = null)
    {
        _settings = settings;
        _syncService = syncService;
        _discoveryService = discoveryService;
        IntegrityService = integrityService;
        RescanService = rescanService;
        ActivityLogService = activityLogService;
        VersioningService = versioningService;

        SaveCommand = new RelayCommand(SaveSettings);
        ResetCommand = new RelayCommand(ResetSettings);
    }

    private void SaveSettings(object? parameter)
    {
        _settings.Save();
        // Trigger service updates
        _discoveryService.LocalName = _settings.DeviceName;
        _discoveryService.IsSyncEnabled = _settings.IsSyncEnabled;
        
        if (_settings.IsSyncEnabled && !_syncService.IsRunning)
            _syncService.Start();
        else if (!_settings.IsSyncEnabled && _syncService.IsRunning)
            _syncService.Stop();
    }

    private void ResetSettings(object? parameter)
    {
        // Logic to reset settings if needed
    }
}
