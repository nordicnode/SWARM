using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using Swarm.Core.Models;
using Swarm.Core.Services;

namespace Swarm.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the Peers view.
/// </summary>
public class PeersViewModel : ViewModelBase
{
    private readonly DiscoveryService _discoveryService;
    private readonly TransferService _transferService;
    private readonly Settings _settings;

    private ObservableCollection<PeerItemViewModel> _peers = new();
    private PeerItemViewModel? _selectedPeer;

    public PeersViewModel()
    {
        // Design-time constructor
    }

    public PeersViewModel(DiscoveryService discoveryService, TransferService transferService, Settings settings)
    {
        _discoveryService = discoveryService;
        _transferService = transferService;
        _settings = settings;

        _discoveryService.Peers.CollectionChanged += (s, e) => UpdatePeersList();
        UpdatePeersList();
    }

    private void UpdatePeersList()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var viewModels = _discoveryService.Peers.Select(p => new PeerItemViewModel
            {
                Id = p.Id,
                Name = p.Name,
                IpAddress = p.IpAddress,
                IsOnline = true, // If in discovery list, it's presumably online
                IsTrusted = p.IsTrusted,
                IsSyncEnabled = p.IsSyncEnabled
            });

            Peers = new ObservableCollection<PeerItemViewModel>(viewModels);
        });
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
