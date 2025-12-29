using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Makaretu.Dns;
using Serilog;
using Swarm.Core.Models;

namespace Swarm.Core.Services;

/// <summary>
/// Multicast DNS (mDNS/Zeroconf) peer discovery service.
/// Complements UDP broadcast for networks where broadcast is blocked (VLANs, corporate networks).
/// </summary>
public class MdnsDiscoveryService : IDisposable
{
    private const string ServiceType = "_swarm._tcp";
    private const string ServiceDomain = "local";
    private const string InstancePrefix = "swarm-";
    
    private readonly string _localId;
    private readonly CryptoService _cryptoService;
    private readonly Settings _settings;
    private readonly ConcurrentDictionary<string, Peer> _discoveredPeers = new();
    
    private MulticastService? _mdns;
    private ServiceDiscovery? _serviceDiscovery;
    private ServiceProfile? _localProfile;
    private CancellationTokenSource? _cts;
    private int _transferPort;
    
    public string LocalName { get; set; } = Environment.MachineName;
    public bool IsSyncEnabled { get; set; }
    
    public event Action<Peer>? PeerDiscovered;
    public event Action<Peer>? PeerLost;
    
    public MdnsDiscoveryService(string localId, CryptoService cryptoService, Settings settings)
    {
        _localId = localId;
        _cryptoService = cryptoService;
        _settings = settings;
    }
    
    public void Start(int transferPort)
    {
        _transferPort = transferPort;
        _cts = new CancellationTokenSource();
        
        try
        {
            _mdns = new MulticastService();
            _serviceDiscovery = new ServiceDiscovery(_mdns);
            
            // Create our service profile
            _localProfile = new ServiceProfile(
                instanceName: $"{InstancePrefix}{_localId}",
                serviceName: ServiceType,
                port: (ushort)transferPort
            );

            // Add TXT records with peer information
            var txtRecords = new Dictionary<string, string>
            {
                ["id"] = _localId,
                ["name"] = LocalName,
                ["port"] = transferPort.ToString(),
                ["sync"] = IsSyncEnabled ? "1" : "0",
                ["version"] = "2.0",
                ["pubkey"] = Convert.ToBase64String(_cryptoService.GetPublicKey())
            };

            foreach (var kvp in txtRecords)
            {
                _localProfile.AddProperty(kvp.Key, kvp.Value);
            }

            // Start listening for other services
            _serviceDiscovery.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
            _serviceDiscovery.ServiceInstanceShutdown += OnServiceInstanceShutdown;
            
            // Advertise ourselves
            _serviceDiscovery.Advertise(_localProfile);
            
            // Query for other Swarm instances
            _serviceDiscovery.QueryServiceInstances(ServiceType);
            
            _mdns.Start();
            
            Log.Debug("mDNS discovery started for {LocalId}", _localId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to start mDNS discovery");
            // mDNS is optional, don't crash if it fails
        }
    }
    
    public void UpdateAdvertisement()
    {
        if (_localProfile == null || _serviceDiscovery == null) return;
        
        try
        {
            // Re-advertise with updated info
            _serviceDiscovery.Unadvertise(_localProfile);
            
            _localProfile = new ServiceProfile(
                instanceName: $"{InstancePrefix}{_localId}",
                serviceName: ServiceType,
                port: (ushort)_transferPort
            );
            
            _localProfile.AddProperty("id", _localId);
            _localProfile.AddProperty("name", LocalName);
            _localProfile.AddProperty("port", _transferPort.ToString());
            _localProfile.AddProperty("sync", IsSyncEnabled ? "1" : "0");
            _localProfile.AddProperty("version", "2.0");
            _localProfile.AddProperty("pubkey", Convert.ToBase64String(_cryptoService.GetPublicKey()));
            
            _serviceDiscovery.Advertise(_localProfile);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to update mDNS advertisement");
        }
    }
    
    private void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        try
        {
            var instanceName = e.ServiceInstanceName.Labels.FirstOrDefault();
            if (string.IsNullOrEmpty(instanceName) || !instanceName.StartsWith(InstancePrefix))
                return;
            
            // Query for more details
            _mdns?.SendQuery(e.ServiceInstanceName, type: DnsType.TXT);
            _mdns?.SendQuery(e.ServiceInstanceName, type: DnsType.SRV);
            _mdns?.SendQuery(e.ServiceInstanceName, type: DnsType.A);
            
            // Handle the answer when received
            if (_mdns != null)
            {
                _mdns.AnswerReceived += (s, args) => ProcessAnswers(args.Message, e.ServiceInstanceName);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "mDNS discovery error");
        }
    }
    
    private void ProcessAnswers(Makaretu.Dns.Message message, DomainName serviceInstanceName)
    {
        try
        {
            string? peerId = null;
            string? peerName = null;
            int port = 0;
            bool syncEnabled = false;
            string? publicKey = null;
            string? ipAddress = null;
            
            // Extract TXT records
            foreach (var record in message.Answers.OfType<TXTRecord>())
            {
                foreach (var txt in record.Strings)
                {
                    var parts = txt.Split('=', 2);
                    if (parts.Length != 2) continue;
                    
                    switch (parts[0])
                    {
                        case "id":
                            peerId = parts[1];
                            break;
                        case "name":
                            peerName = parts[1];
                            break;
                        case "port":
                            int.TryParse(parts[1], out port);
                            break;
                        case "sync":
                            syncEnabled = parts[1] == "1";
                            break;
                        case "pubkey":
                            publicKey = parts[1];
                            break;
                    }
                }
            }
            
            // Extract A records for IP address
            foreach (var record in message.Answers.OfType<ARecord>())
            {
                ipAddress = record.Address.ToString();
                break;
            }
            
            // Extract SRV records for port if not in TXT
            if (port == 0)
            {
                foreach (var record in message.Answers.OfType<SRVRecord>())
                {
                    port = record.Port;
                    break;
                }
            }
            
            // Validate and add peer
            if (string.IsNullOrEmpty(peerId) || peerId == _localId)
                return;
                
            if (string.IsNullOrEmpty(peerName))
                peerName = peerId;
                
            if (string.IsNullOrEmpty(ipAddress))
                return;
            
            // Check trust status
            bool isTrusted = !string.IsNullOrEmpty(publicKey) &&
                            _settings.TrustedPeerPublicKeys.TryGetValue(peerId, out var storedKey) &&
                            storedKey == publicKey;
            
            var peer = new Peer
            {
                Id = peerId,
                Name = peerName,
                IpAddress = ipAddress,
                Port = port > 0 ? port : ProtocolConstants.DISCOVERY_PORT,
                LastSeen = DateTime.Now,
                IsSyncEnabled = syncEnabled,
                PublicKeyBase64 = publicKey,
                IsTrusted = isTrusted
            };
            
            if (_discoveredPeers.TryAdd(peerId, peer))
            {
                Log.Debug("mDNS discovered peer: {PeerName} ({IpAddress})", peerName, ipAddress);
                PeerDiscovered?.Invoke(peer);
            }
            else if (_discoveredPeers.TryGetValue(peerId, out var existing))
            {
                // Update existing peer
                existing.LastSeen = DateTime.Now;
                existing.IpAddress = ipAddress;
                existing.Port = peer.Port;
                existing.IsSyncEnabled = syncEnabled;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error processing mDNS answers");
        }
    }
    
    private void OnServiceInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        try
        {
            var instanceName = e.ServiceInstanceName.Labels.FirstOrDefault();
            if (string.IsNullOrEmpty(instanceName) || !instanceName.StartsWith(InstancePrefix))
                return;
                
            var peerId = instanceName.Replace(InstancePrefix, "");
            
            if (_discoveredPeers.TryRemove(peerId, out var peer))
            {
                Log.Debug("mDNS peer left: {PeerName}", peer.Name);
                PeerLost?.Invoke(peer);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "mDNS shutdown handler error");
        }
    }
    
    public IEnumerable<Peer> GetDiscoveredPeers() => _discoveredPeers.Values;
    
    public void Dispose()
    {
        _cts?.Cancel();
        
        if (_localProfile != null && _serviceDiscovery != null)
        {
            try
            {
                _serviceDiscovery.Unadvertise(_localProfile);
            }
            catch { /* Best effort cleanup */ }
        }
        
        _serviceDiscovery?.Dispose();
        _mdns?.Stop();
        _mdns?.Dispose();
        _cts?.Dispose();
        
        Log.Debug("mDNS discovery stopped");
    }
}

