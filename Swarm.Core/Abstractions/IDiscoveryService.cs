using System;
using System.Collections.ObjectModel;
using Swarm.Core.Models;

namespace Swarm.Core.Abstractions;

public interface IDiscoveryService : IDisposable
{
    ObservableCollection<Peer> Peers { get; }
    string LocalId { get; }
    string LocalName { get; set; }
    bool IsSyncEnabled { get; set; }
    int TransferPort { get; }

    event Action<Peer>? PeerDiscovered;
    event Action<Peer>? PeerLost;
    event Action<Peer>? UntrustedPeerDiscovered;
    event Action? BindingFailed;

    void Start(int transferPort);
}
