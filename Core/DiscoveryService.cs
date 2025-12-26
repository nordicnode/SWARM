using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Swarm.Models;

namespace Swarm.Core;

/// <summary>
/// UDP-based peer discovery service for finding other Swarm instances on the LAN.
/// </summary>
public class DiscoveryService : IDisposable
{
    private const int DISCOVERY_PORT = 37420;
    private const string PROTOCOL_HEADER = "SWARM:1.0";
    private const int BROADCAST_INTERVAL_MS = 3000;
    private const int PEER_TIMEOUT_SECONDS = 15;

    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private readonly object _peersLock = new();
    
    public ObservableCollection<Peer> Peers { get; } = [];
    public string LocalId { get; } = Guid.NewGuid().ToString()[..8];
    public string LocalName { get; set; } = Environment.MachineName;
    public int TransferPort { get; private set; }

    public event Action<Peer>? PeerDiscovered;
    public event Action<Peer>? PeerLost;

    /// <summary>
    /// Whether this local instance has sync enabled. Set by SyncService.
    /// </summary>
    public bool IsSyncEnabled { get; set; }

    public void Start(int transferPort)
    {
        TransferPort = transferPort;
        _cts = new CancellationTokenSource();

        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DISCOVERY_PORT));
            _udpClient.EnableBroadcast = true;
        }
        catch (SocketException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Discovery socket error: {ex.Message}");
            // Port might be in use, try a different one
            _udpClient = new UdpClient(0);
            _udpClient.EnableBroadcast = true;
        }

        // Start broadcasting
        Task.Run(() => BroadcastLoop(_cts.Token));
        
        // Start listening
        Task.Run(() => ListenLoop(_cts.Token));
        
        // Start cleanup
        Task.Run(() => CleanupLoop(_cts.Token));
    }

    private async Task BroadcastLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await BroadcastPresence();
                await Task.Delay(BROADCAST_INTERVAL_MS, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Broadcast error: {ex.Message}");
            }
        }
    }

    private async Task BroadcastPresence()
    {
        if (_udpClient == null) return;

        // Format: SWARM:1.0|ID|NAME|TRANSFER_PORT|SYNC_ENABLED
        var syncEnabled = IsSyncEnabled ? "1" : "0";
        var message = $"{PROTOCOL_HEADER}|{LocalId}|{LocalName}|{TransferPort}|{syncEnabled}";
        var data = Encoding.UTF8.GetBytes(message);

        // Broadcast to all network adapters
        var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DISCOVERY_PORT);
        await _udpClient.SendAsync(data, data.Length, broadcastEndpoint);

        // Also send to common subnet broadcasts
        foreach (var ip in GetLocalIpAddresses())
        {
            try
            {
                var parts = ip.Split('.');
                if (parts.Length == 4)
                {
                    var subnetBroadcast = $"{parts[0]}.{parts[1]}.{parts[2]}.255";
                    var endpoint = new IPEndPoint(IPAddress.Parse(subnetBroadcast), DISCOVERY_PORT);
                    await _udpClient.SendAsync(data, data.Length, endpoint);
                }
            }
            catch { /* ignore individual failures */ }
        }
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_udpClient == null) break;
                
                var result = await _udpClient.ReceiveAsync(ct);
                var message = Encoding.UTF8.GetString(result.Buffer);
                
                ProcessDiscoveryMessage(message, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Listen error: {ex.Message}");
            }
        }
    }

    private void ProcessDiscoveryMessage(string message, IPEndPoint remoteEndPoint)
    {
        // Format: SWARM:1.0|ID|NAME|TRANSFER_PORT|SYNC_ENABLED
        var parts = message.Split('|');
        if (parts.Length < 4 || parts[0] != PROTOCOL_HEADER) return;

        var peerId = parts[1];
        var peerName = parts[2];
        if (!int.TryParse(parts[3], out var transferPort)) return;
        
        // Parse sync enabled flag (optional for backwards compatibility)
        var isSyncEnabled = parts.Length >= 5 && parts[4] == "1";

        // Ignore our own broadcasts
        if (peerId == LocalId) return;

        Peer? newPeer = null;

        lock (_peersLock)
        {
            var existingPeer = Peers.FirstOrDefault(p => p.Id == peerId);
            if (existingPeer != null)
            {
                existingPeer.LastSeen = DateTime.Now;
                existingPeer.IpAddress = remoteEndPoint.Address.ToString();
                existingPeer.Port = transferPort;
                existingPeer.IsSyncEnabled = isSyncEnabled;
            }
            else
            {
                newPeer = new Peer
                {
                    Id = peerId,
                    Name = peerName,
                    IpAddress = remoteEndPoint.Address.ToString(),
                    Port = transferPort,
                    LastSeen = DateTime.Now,
                    IsSyncEnabled = isSyncEnabled
                };
            }
        }

        // Add new peer outside the lock to prevent deadlocks with UI thread
        if (newPeer != null)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                Peers.Add(newPeer);
                PeerDiscovered?.Invoke(newPeer);
            });
        }
    }

    private async Task CleanupLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, ct);
                CleanupStalePeers();
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private void CleanupStalePeers()
    {
        List<Peer> stalePeers;

        lock (_peersLock)
        {
            stalePeers = Peers
                .Where(p => (DateTime.Now - p.LastSeen).TotalSeconds > PEER_TIMEOUT_SECONDS)
                .ToList();
        }

        // Remove stale peers outside the lock to prevent deadlocks with UI thread
        foreach (var peer in stalePeers)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                Peers.Remove(peer);
                PeerLost?.Invoke(peer);
            });
        }
    }

    private static IEnumerable<string> GetLocalIpAddresses()
    {
        var host = Dns.GetHostEntry(Dns.GetHostName());
        return host.AddressList
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
            .Select(ip => ip.ToString());
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udpClient?.Close();
        _udpClient?.Dispose();
        _cts?.Dispose();
    }
}
