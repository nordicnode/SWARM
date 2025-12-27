using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Swarm.Models;

namespace Swarm.Core;

/// <summary>
/// UDP-based peer discovery service for finding other Swarm instances on the LAN.
/// </summary>
public class DiscoveryService : IDisposable
{
    private const int DISCOVERY_PORT = ProtocolConstants.DISCOVERY_PORT;
    private const string PROTOCOL_HEADER = ProtocolConstants.DISCOVERY_HEADER;
    private const int BROADCAST_INTERVAL_MS = ProtocolConstants.BROADCAST_INTERVAL_MS;
    private const int PEER_TIMEOUT_SECONDS = ProtocolConstants.PEER_TIMEOUT_SECONDS;

    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private readonly object _peersLock = new();
    private readonly CryptoService _cryptoService;
    private readonly Settings _settings;
    
    public ObservableCollection<Peer> Peers { get; } = [];
    public string LocalId { get; }
    public string LocalName { get; set; } = Environment.MachineName;
    public int TransferPort { get; private set; }

    public event Action<Peer>? PeerDiscovered;
    public event Action<Peer>? PeerLost;
    public event Action? BindingFailed;
    
    /// <summary>
    /// Event raised when an untrusted peer is discovered. UI should prompt for trust confirmation.
    /// </summary>
    public event Action<Peer>? UntrustedPeerDiscovered;

    /// <summary>
    /// Whether this local instance has sync enabled. Set by SyncService.
    /// </summary>
    public bool IsSyncEnabled { get; set; }

    public DiscoveryService(string localId, CryptoService cryptoService, Settings settings)
    {
        LocalId = localId;
        _cryptoService = cryptoService;
        _settings = settings;
    }

    public void Start(int transferPort)
    {
        TransferPort = transferPort;
        _cts = new CancellationTokenSource();

        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.ExclusiveAddressUse = false;
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DISCOVERY_PORT));
            _udpClient.EnableBroadcast = true;
        }
        catch (SocketException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Discovery socket error: {ex.Message}");
            BindingFailed?.Invoke();
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

        // Create structured JSON discovery message
        var discoveryMessage = new DiscoveryMessage
        {
            Protocol = ProtocolConstants.DISCOVERY_PROTOCOL_ID,
            Version = "2.0", // Secure protocol version
            PeerId = LocalId,
            PeerName = LocalName,
            TransferPort = TransferPort,
            SyncEnabled = IsSyncEnabled,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PublicKey = Convert.ToBase64String(_cryptoService.GetPublicKey())
        };

        // Sign the message payload
        var signature = _cryptoService.Sign(discoveryMessage.GetSignablePayload());
        discoveryMessage.Signature = Convert.ToBase64String(signature);

        var json = JsonSerializer.Serialize(discoveryMessage);
        var data = Encoding.UTF8.GetBytes(json);

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
        string peerId;
        string peerName;
        int transferPort;
        bool isSyncEnabled;
        string? publicKeyBase64 = null;
        bool signatureValid = false;

        // Try JSON format first (v1.1+)
        if (message.TrimStart().StartsWith("{"))
        {
            try
            {
                var discoveryMessage = JsonSerializer.Deserialize<DiscoveryMessage>(message);
                if (discoveryMessage == null || discoveryMessage.Protocol != ProtocolConstants.DISCOVERY_PROTOCOL_ID)
                    return;

                peerId = discoveryMessage.PeerId;
                peerName = discoveryMessage.PeerName;
                transferPort = discoveryMessage.TransferPort;
                isSyncEnabled = discoveryMessage.SyncEnabled;
                publicKeyBase64 = discoveryMessage.PublicKey;

                // Verify signature if present (v2.0)
                if (!string.IsNullOrEmpty(discoveryMessage.PublicKey) && !string.IsNullOrEmpty(discoveryMessage.Signature))
                {
                    try
                    {
                        var publicKey = Convert.FromBase64String(discoveryMessage.PublicKey);
                        var signature = Convert.FromBase64String(discoveryMessage.Signature);
                        signatureValid = CryptoService.Verify(discoveryMessage.GetSignablePayload(), signature, publicKey);
                        
                        if (!signatureValid)
                        {
                            System.Diagnostics.Debug.WriteLine($"Invalid signature from peer {peerId}");
                            return; // Reject messages with invalid signatures
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Signature verification error: {ex.Message}");
                        return;
                    }
                }
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"JSON parse error: {ex.Message}");
                return;
            }
        }
        else
        {
            // Fallback: Legacy pipe-delimited format (v1.0)
            // Format: SWARM:1.0|ID|NAME|TRANSFER_PORT|SYNC_ENABLED
            var parts = message.Split('|');
            if (parts.Length < 4 || parts[0] != PROTOCOL_HEADER) return;

            peerId = parts[1];
            peerName = parts[2];
            if (!int.TryParse(parts[3], out transferPort)) return;
            
            // Parse sync enabled flag (optional for backwards compatibility)
            isSyncEnabled = parts.Length >= 5 && parts[4] == "1";
        }

        // Ignore our own broadcasts
        if (peerId == LocalId) return;

        // Check if this peer is trusted
        bool isTrusted = !string.IsNullOrEmpty(publicKeyBase64) && 
                         _settings.TrustedPeerPublicKeys.TryGetValue(peerId, out var storedKey) && 
                         storedKey == publicKeyBase64;

        Peer? newPeer = null;
        bool isNewUntrustedPeer = false;

        lock (_peersLock)
        {
            var existingPeer = Peers.FirstOrDefault(p => p.Id == peerId);
            if (existingPeer != null)
            {
                existingPeer.LastSeen = DateTime.Now;
                existingPeer.IpAddress = remoteEndPoint.Address.ToString();
                existingPeer.Port = transferPort;
                existingPeer.IsSyncEnabled = isSyncEnabled;
                existingPeer.PublicKeyBase64 = publicKeyBase64;
                existingPeer.IsTrusted = isTrusted;
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
                    IsSyncEnabled = isSyncEnabled,
                    PublicKeyBase64 = publicKeyBase64,
                    IsTrusted = isTrusted
                };
                isNewUntrustedPeer = !isTrusted && signatureValid;
            }
        }

        // Add new peer outside the lock to prevent deadlocks with UI thread
        if (newPeer != null)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                Peers.Add(newPeer);
                PeerDiscovered?.Invoke(newPeer);
                
                // Notify about untrusted peer for TOFU prompt
                if (isNewUntrustedPeer)
                {
                    UntrustedPeerDiscovered?.Invoke(newPeer);
                }
            });
        }
    }

    private async Task CleanupLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ProtocolConstants.CLEANUP_INTERVAL_MS, ct);
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
