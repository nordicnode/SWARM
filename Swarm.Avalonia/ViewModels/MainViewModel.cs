using System.Windows.Input;

namespace Swarm.Avalonia.ViewModels;

/// <summary>
/// Main ViewModel for the Swarm Avalonia application.
/// Handles navigation and shared state.
/// </summary>
public class MainViewModel : ViewModelBase
{
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

    public MainViewModel()
    {
        // Initialize commands
        NavigateToOverviewCommand = new RelayCommand(NavigateToOverview);
        NavigateToFilesCommand = new RelayCommand(NavigateToFiles);
        NavigateToPeersCommand = new RelayCommand(NavigateToPeers);
        NavigateToSettingsCommand = new RelayCommand(NavigateToSettings);
        
        // Set default view
        CurrentView = new OverviewViewModel();
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
        CurrentView = new OverviewViewModel();
    }

    private void NavigateToFiles()
    {
        ClearNavSelection();
        IsFilesSelected = true;
        CurrentView = new FilesViewModel();
    }

    private void NavigateToPeers()
    {
        ClearNavSelection();
        IsPeersSelected = true;
        CurrentView = new PeersViewModel();
    }

    private void NavigateToSettings()
    {
        ClearNavSelection();
        IsSettingsSelected = true;
        CurrentView = new SettingsViewModel();
    }

    #endregion
}
