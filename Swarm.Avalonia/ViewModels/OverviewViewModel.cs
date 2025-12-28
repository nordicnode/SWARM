namespace Swarm.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the Overview/Dashboard view.
/// </summary>
public class OverviewViewModel : ViewModelBase
{
    private string _syncFolderPath = "";
    private int _totalFiles;
    private string _totalSize = "0 KB";
    private int _connectedPeers;

    public OverviewViewModel()
    {
        // TODO: Initialize with actual data from Swarm.Core services
        SyncFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        TotalFiles = 0;
        TotalSize = "0 KB";
        ConnectedPeers = 0;
    }

    public string SyncFolderPath
    {
        get => _syncFolderPath;
        set => SetProperty(ref _syncFolderPath, value);
    }

    public int TotalFiles
    {
        get => _totalFiles;
        set => SetProperty(ref _totalFiles, value);
    }

    public string TotalSize
    {
        get => _totalSize;
        set => SetProperty(ref _totalSize, value);
    }

    public int ConnectedPeers
    {
        get => _connectedPeers;
        set => SetProperty(ref _connectedPeers, value);
    }
}
