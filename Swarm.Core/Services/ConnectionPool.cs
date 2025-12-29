using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swarm.Core.Models;

namespace Swarm.Core.Services;

/// <summary>
/// Manages a pool of TCP connections to a single peer for parallel transfers.
/// </summary>
public class ConnectionPool : IDisposable
{
    private readonly List<PeerConnection> _connections = new();
    private readonly SemaphoreSlim _poolLock = new(1, 1);
    private readonly Peer _peer;
    private readonly Func<Peer, PeerConnection, CancellationToken, Task> _performHandshakeAsync;
    private readonly Func<PeerConnection, CancellationToken, Task<int>> _measureRttAsync;
    private readonly ILogger<ConnectionPool> _logger;
    private bool _disposed;

    public string PeerKey { get; }
    public int MaxConnections { get; set; } = ProtocolConstants.MAX_PARALLEL_CONNECTIONS;

    public ConnectionPool(
        Peer peer,
        Func<Peer, PeerConnection, CancellationToken, Task> performHandshakeAsync,
        Func<PeerConnection, CancellationToken, Task<int>> measureRttAsync,
        ILogger<ConnectionPool>? logger = null)
    {
        _peer = peer;
        _performHandshakeAsync = performHandshakeAsync;
        _measureRttAsync = measureRttAsync;
        _logger = logger ?? NullLogger<ConnectionPool>.Instance;
        PeerKey = $"{peer.IpAddress}:{peer.Port}";
    }

    /// <summary>
    /// Acquires an available connection from the pool, creating new ones if needed.
    /// </summary>
    public async Task<PeerConnection> AcquireAsync(CancellationToken ct)
    {
        await _poolLock.WaitAsync(ct);
        try
        {
            // First, try to find an available healthy connection
            foreach (var conn in _connections.ToList())
            {
                if (!conn.IsHealthy())
                {
                    conn.Dispose();
                    _connections.Remove(conn);
                    continue;
                }

                // Try to acquire the connection's lock without waiting
                if (await conn.Lock.WaitAsync(0, ct))
                {
                    conn.LastActivity = DateTime.UtcNow;
                    return conn;
                }
            }

            // No available connections - can we create a new one?
            if (_connections.Count < MaxConnections)
            {
                var newConn = await CreateNewConnectionAsync(ct);
                if (newConn != null)
                {
                    _connections.Add(newConn);
                    await newConn.Lock.WaitAsync(ct); // Acquire lock before returning
                    return newConn;
                }
            }

            // All connections busy and at max capacity - wait for first available
            // Release pool lock while waiting to allow other operations
        }
        finally
        {
            _poolLock.Release();
        }

        // Wait for any connection to become available
        while (!ct.IsCancellationRequested)
        {
            await _poolLock.WaitAsync(ct);
            try
            {
                foreach (var conn in _connections)
                {
                    if (conn.IsHealthy() && await conn.Lock.WaitAsync(0, ct))
                    {
                        conn.LastActivity = DateTime.UtcNow;
                        return conn;
                    }
                }
            }
            finally
            {
                _poolLock.Release();
            }

            // Small delay before retrying
            await Task.Delay(10, ct);
        }

        throw new OperationCanceledException(ct);
    }

    /// <summary>
    /// Gets the primary connection (first in pool) for operations that need a consistent connection.
    /// Creates one if pool is empty.
    /// </summary>
    public async Task<PeerConnection> GetPrimaryConnectionAsync(CancellationToken ct)
    {
        await _poolLock.WaitAsync(ct);
        try
        {
            // Clean up unhealthy connections
            for (int i = _connections.Count - 1; i >= 0; i--)
            {
                if (!_connections[i].IsHealthy())
                {
                    _connections[i].Dispose();
                    _connections.RemoveAt(i);
                }
            }

            // Return first healthy connection or create new one
            if (_connections.Count > 0)
            {
                var conn = _connections[0];
                await conn.Lock.WaitAsync(ct);
                conn.LastActivity = DateTime.UtcNow;
                return conn;
            }

            // Create first connection
            var newConn = await CreateNewConnectionAsync(ct);
            if (newConn != null)
            {
                _connections.Add(newConn);
                await newConn.Lock.WaitAsync(ct);
                return newConn;
            }

            throw new InvalidOperationException($"Failed to create connection to {PeerKey}");
        }
        finally
        {
            _poolLock.Release();
        }
    }

    private async Task<PeerConnection?> CreateNewConnectionAsync(CancellationToken ct)
    {
        Exception? lastException = null;
        for (int attempt = 1; attempt <= ProtocolConstants.MAX_RETRY_ATTEMPTS; attempt++)
        {
            try
            {
                var client = new TcpClient();
                ConfigureTcpClient(client);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(ProtocolConstants.CONNECTION_TIMEOUT_MS);

                await client.ConnectAsync(_peer.IpAddress, _peer.Port, timeoutCts.Token);

                var connection = new PeerConnection(client);

                // Perform secure handshake
                try
                {
                    await _performHandshakeAsync(_peer, connection, ct);
                    _logger.LogDebug($"Secure handshake completed for pool connection to {_peer.Name}");
                }
                catch (Exception handshakeEx)
                {
                    _logger.LogWarning(handshakeEx, $"Secure handshake failed for pool connection to {_peer.Name}: {handshakeEx.Message}");
                }

                // Measure RTT
                try
                {
                    connection.RttMs = await _measureRttAsync(connection, ct);
                }
                catch { }

                _logger.LogDebug($"Created pool connection #{_connections.Count + 1} to {_peer.Name}");
                return connection;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt < ProtocolConstants.MAX_RETRY_ATTEMPTS)
                {
                    var delay = ProtocolConstants.RETRY_BASE_DELAY_MS * (int)Math.Pow(2, attempt - 1);
                    await Task.Delay(delay, ct);
                }
            }
        }

        _logger.LogWarning(lastException, $"Failed to create pool connection to {_peer.Name}: {lastException?.Message}");
        return null;
    }

    /// <summary>
    /// Configures TCP socket options for better connection management.
    /// </summary>
    private void ConfigureTcpClient(TcpClient client)
    {
        try
        {
            var socket = client.Client;
            
            // Enable TCP keepalive to detect half-open connections
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            
            // Set send/receive timeouts
            client.SendTimeout = ProtocolConstants.CONNECTION_TIMEOUT_MS;
            client.ReceiveTimeout = ProtocolConstants.CONNECTION_TIMEOUT_MS;
            
            // Disable Nagle's algorithm for lower latency
            client.NoDelay = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to configure TCP options: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var conn in _connections)
        {
            try { conn.Dispose(); } catch { }
        }
        _connections.Clear();
        _poolLock.Dispose();
    }
}
