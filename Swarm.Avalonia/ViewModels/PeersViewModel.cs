using System.Collections.ObjectModel;

namespace Swarm.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the Peers view.
/// </summary>
public class PeersViewModel : ViewModelBase
{
    private ObservableCollection<PeerItemViewModel> _peers = new();
    private PeerItemViewModel? _selectedPeer;

    public PeersViewModel()
    {
        // TODO: Initialize with actual data from Swarm.Core services
    }

    public ObservableCollection<PeerItemViewModel> Peers
    {
        get => _peers;
        set => SetProperty(ref _peers, value);
    }

    public PeerItemViewModel? SelectedPeer
    {
        get => _selectedPeer;
        set => SetProperty(ref _selectedPeer, value);
    }
}

/// <summary>
/// ViewModel for a single peer item.
/// </summary>
public class PeerItemViewModel : ViewModelBase
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string IpAddress { get; set; }
    public bool IsOnline { get; set; }
    public bool IsTrusted { get; set; }
    public bool IsSyncEnabled { get; set; }
}
