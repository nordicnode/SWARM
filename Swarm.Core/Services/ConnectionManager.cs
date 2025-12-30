using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swarm.Core.Abstractions;
using Swarm.Core.Models;

namespace Swarm.Core.Services;

/// <summary>
/// Manages peer connections including pooling and RTT measurement.
/// Extracted from TransferService for better separation of concerns.
/// </summary>
public class ConnectionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, ConnectionPool> _connectionPools = new();
    private readonly Settings _settings;
    private readonly CryptoService _cryptoService;
    private readonly ILogger<ConnectionManager> _logger;

    /// <summary>
    /// Delegate for performing secure handshake when establishing a connection.
    /// </summary>
    public Func<PeerConnection, Peer, CancellationToken, Task>? HandshakeHandler { get; set; }

    public ConnectionManager(Settings settings, CryptoService cryptoService, ILogger<ConnectionManager>? logger = null)
    {
        _settings = settings;
        _cryptoService = cryptoService;
        _logger = logger ?? NullLogger<ConnectionManager>.Instance;
    }

    /// <summary>
    /// Gets or creates a connection pool for the specified peer.
    /// </summary>
    public ConnectionPool GetOrCreateConnectionPool(Peer peer)
    {
        var key = $"{peer.IpAddress}:{peer.Port}";
        return _connectionPools.GetOrAdd(key, _ => new ConnectionPool(
            peer,
            async (p, conn, ct) =>
            {
                if (HandshakeHandler != null)
                {
                    await HandshakeHandler(conn, p, ct);
                }
            },
            MeasureRttAsync));
    }

    /// <summary>
    /// Gets a connection to a peer (uses primary connection from pool for backward compatibility).
    /// The caller must release the connection lock when done.
    /// </summary>
    public async Task<PeerConnection> GetOrCreatePeerConnection(Peer peer, CancellationToken ct)
    {
        var pool = GetOrCreateConnectionPool(peer);
        return await pool.GetPrimaryConnectionAsync(ct);
    }

    /// <summary>
    /// Acquires a connection from the pool for parallel transfers.
    /// The caller must release the connection lock when done.
    /// </summary>
    public async Task<PeerConnection> AcquirePooledConnection(Peer peer, CancellationToken ct)
    {
        var pool = GetOrCreateConnectionPool(peer);
        return await pool.AcquireAsync(ct);
    }

    /// <summary>
    /// Removes a connection pool when it's no longer healthy.
    /// </summary>
    public void RemoveConnectionPool(Peer peer)
    {
        var key = $"{peer.IpAddress}:{peer.Port}";
        if (_connectionPools.TryRemove(key, out var pool))
        {
            pool.Dispose();
        }
    }

    /// <summary>
    /// Measures round-trip time to a peer for adaptive buffer sizing.
    /// </summary>
    public Task<int> MeasureRttAsync(PeerConnection connection, CancellationToken ct)
    {
        try
        {
            var socket = connection.Client.Client;
            if (socket == null) return Task.FromResult(-1);

            // Check if local network (private IP ranges typically have low latency)
            var remoteEndpoint = socket.RemoteEndPoint as IPEndPoint;
            if (remoteEndpoint != null)
            {
                if (IsPrivateNetwork(remoteEndpoint.Address))
                {
                    // Likely LAN - assume fast
                    return Task.FromResult(2);
                }
            }

            // Default to medium speed assumption
            return Task.FromResult(25);
        }
        catch
        {
            // If we can't measure, assume medium speed
            return Task.FromResult(25);
        }
    }

    /// <summary>
    /// Checks if an IP address is in a private/local network range.
    /// </summary>
    private static bool IsPrivateNetwork(IPAddress address)
    {
        var ip = address.ToString();
        
        // IPv4 private ranges
        if (ip.StartsWith("127.")) return true;           // Loopback
        if (ip.StartsWith("10.")) return true;            // 10.0.0.0/8
        if (ip.StartsWith("192.168.")) return true;       // 192.168.0.0/16
        
        // 172.16.0.0/12 range (172.16.x.x - 172.31.x.x)
        if (ip.StartsWith("172."))
        {
            var parts = ip.Split('.');
            if (parts.Length >= 2 && int.TryParse(parts[1], out var secondOctet))
            {
                if (secondOctet >= 16 && secondOctet <= 31) return true;
            }
        }
        
        // IPv6 link-local (fe80::/10)
        if (ip.StartsWith("fe80:", StringComparison.OrdinalIgnoreCase)) return true;
        
        // IPv6 loopback
        if (ip == "::1") return true;
        
        return false;
    }

    /// <summary>
    /// Gets whether a connection pool exists for the given peer.
    /// </summary>
    public bool HasConnectionPool(Peer peer)
    {
        var key = $"{peer.IpAddress}:{peer.Port}";
        return _connectionPools.ContainsKey(key);
    }

    /// <summary>
    /// Gets the count of active connection pools.
    /// </summary>
    public int ActivePoolCount => _connectionPools.Count;

    public void Dispose()
    {
        foreach (var pool in _connectionPools.Values)
        {
            try
            {
                pool.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error disposing connection pool: {ex.Message}");
            }
        }
        _connectionPools.Clear();
    }
}
