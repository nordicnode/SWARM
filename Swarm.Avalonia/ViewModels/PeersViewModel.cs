using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swarm.Core.Models;
using Swarm.Core.Services;

namespace Swarm.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the Peers view.
/// </summary>
public class PeersViewModel : ViewModelBase
{
    private readonly DiscoveryService _discoveryService = null!;
    private readonly TransferService _transferService = null!;
    private readonly Settings _settings = null!;
    private readonly ILogger<PeersViewModel> _logger;

    private ObservableCollection<PeerItemViewModel> _peers = new();
    private PeerItemViewModel? _selectedPeer;

    public PeersViewModel()
    {
        // Design-time constructor
        _logger = NullLogger<PeersViewModel>.Instance;
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
            OnPropertyChanged(nameof(IsEmpty));
        });
    }

    public ObservableCollection<PeerItemViewModel> Peers
    {
        get => _peers;
        set => SetProperty(ref _peers, value);
    }

    public bool IsEmpty => _peers.Count == 0;

    public PeerItemViewModel? SelectedPeer
    {
        get => _selectedPeer;
        set => SetProperty(ref _selectedPeer, value);
    }

    public async Task SendFiles(PeerItemViewModel peerItem, System.Collections.Generic.IEnumerable<string> files)
    {
        var peer = _discoveryService.Peers.FirstOrDefault(p => p.Id == peerItem.Id);
        if (peer == null) return;

        foreach (var file in files)
        {
            try 
            {
                await _transferService.SendFile(peer, file);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send file {File} to peer {Peer}", file, peer.Name);
            }
        }
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
