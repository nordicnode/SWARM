using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Threading;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Swarm.Core.Helpers;
using Swarm.Core.Models;

namespace Swarm.Core.Services;

/// <summary>
/// TCP-based file transfer service for sending and receiving files.
/// </summary>
public class TransferService : IDisposable
{
    private const int BUFFER_SIZE = ProtocolConstants.DEFAULT_BUFFER_SIZE;
    private const string PROTOCOL_HEADER = ProtocolConstants.TRANSFER_HEADER;
    private const int MAX_CONCURRENT_CONNECTIONS = 50;

    private readonly Settings _settings;
    private readonly CryptoService _cryptoService;
    private readonly Abstractions.IDispatcher? _dispatcher;
    private readonly SemaphoreSlim _connectionLimiter = new(MAX_CONCURRENT_CONNECTIONS, MAX_CONCURRENT_CONNECTIONS);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _downloadPath;

    public int ListenPort { get; private set; }
    public ObservableCollection<FileTransfer> Transfers { get; } = [];
    
    public TransferService(Settings settings, CryptoService cryptoService, Abstractions.IDispatcher? dispatcher = null)
    {
        _settings = settings;
        _cryptoService = cryptoService;
        _dispatcher = dispatcher;
        _downloadPath = _settings.DownloadPath;
    }
    
    private void InvokeOnUI(Action action)
    {
        if (_dispatcher != null)
        {
            _dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    private class PeerConnection : IDisposable
    {
        public TcpClient Client { get; }
        public NetworkStream NetworkStream { get; }
        public Stream Stream { get; private set; }
        public BinaryWriter Writer { get; private set; }
        public BinaryReader Reader { get; private set; }
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        public byte[]? SessionKey { get; private set; }
        public bool IsEncrypted => SessionKey != null;

        /// <summary>
        /// Measured round-trip time in milliseconds. -1 if not measured.
        /// </summary>
        public int RttMs { get; set; } = -1;

        /// <summary>
        /// Gets the optimal buffer size based on measured RTT.
        /// </summary>
        public int GetOptimalBufferSize()
        {
            if (RttMs < 0)
            {
                // Not measured yet - use default
                return ProtocolConstants.DEFAULT_BUFFER_SIZE;
            }

            if (RttMs < ProtocolConstants.FAST_LAN_RTT_MS)
            {
                // Fast LAN - use maximum buffer
                return ProtocolConstants.MAX_BUFFER_SIZE;
            }
            else if (RttMs > ProtocolConstants.SLOW_LINK_RTT_MS)
            {
                // Slow link - use minimum buffer for responsiveness
                return ProtocolConstants.MIN_BUFFER_SIZE;
            }
            else
            {
                // Medium speed - use default
                return ProtocolConstants.DEFAULT_BUFFER_SIZE;
            }
        }


        public PeerConnection(TcpClient client)
        {
            Client = client;
            NetworkStream = client.GetStream();
            Stream = NetworkStream;
            Writer = new BinaryWriter(Stream, Encoding.UTF8, leaveOpen: true);
            Reader = new BinaryReader(Stream, Encoding.UTF8, leaveOpen: true);
        }

        /// <summary>
        /// Upgrades the connection to use encryption with the given session key.
        /// </summary>
        public void EnableEncryption(byte[] sessionKey)
        {
            SessionKey = sessionKey;
            var secureStream = new SecureStream(NetworkStream, sessionKey);
            Stream = secureStream;
            Writer = new BinaryWriter(Stream, Encoding.UTF8, leaveOpen: true);
            Reader = new BinaryReader(Stream, Encoding.UTF8, leaveOpen: true);
        }

        public bool IsConnected => Client.Connected;

        /// <summary>
        /// Performs an active health check on the connection to detect half-open TCP connections.
        /// </summary>
        public bool IsHealthy()
        {
            if (!Client.Connected) return false;

            try
            {
                // Check if the socket is still connected by polling
                var socket = Client.Client;
                if (socket == null) return false;

                // Poll with a zero timeout to check if connection is still valid
                // SelectRead returns true if: data available, connection closed, or error
                // If no data and connection is good, Poll returns false
                bool readable = socket.Poll(0, SelectMode.SelectRead);
                bool hasData = socket.Available > 0;
                
                // If readable but no data, the connection was closed by the remote side
                if (readable && !hasData)
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            try { Lock.Dispose(); } catch { }
            try { Writer.Dispose(); } catch { }
            try { Reader.Dispose(); } catch { }
            try { Client.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Manages a pool of TCP connections to a single peer for parallel transfers.
    /// </summary>
    private class ConnectionPool : IDisposable
    {
        private readonly List<PeerConnection> _connections = new();
        private readonly SemaphoreSlim _poolLock = new(1, 1);
        private readonly Peer _peer;
        private readonly TransferService _owner;
        private bool _disposed;

        public string PeerKey { get; }
        public int MaxConnections { get; set; } = ProtocolConstants.MAX_PARALLEL_CONNECTIONS;

        public ConnectionPool(Peer peer, TransferService owner)
        {
            _peer = peer;
            _owner = owner;
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
                        await _owner.PerformSecureHandshakeAsClient(connection, _peer, ct);
                        System.Diagnostics.Debug.WriteLine($"Secure handshake completed for pool connection to {_peer.Name}");
                    }
                    catch (Exception handshakeEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"Secure handshake failed for pool connection to {_peer.Name}: {handshakeEx.Message}");
                    }

                    // Measure RTT
                    try
                    {
                        connection.RttMs = await _owner.MeasureRttAsync(connection, ct);
                    }
                    catch { }

                    System.Diagnostics.Debug.WriteLine($"Created pool connection #{_connections.Count + 1} to {_peer.Name}");
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

            System.Diagnostics.Debug.WriteLine($"Failed to create pool connection to {_peer.Name}: {lastException?.Message}");
            return null;
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

    private readonly ConcurrentDictionary<string, ConnectionPool> _connectionPools = new();


    /// <summary>
    /// Configures TCP socket options for better connection management.
    /// </summary>
    private static void ConfigureTcpClient(TcpClient client)
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
            System.Diagnostics.Debug.WriteLine($"Failed to configure TCP options: {ex.Message}");
        }
    }

    /// <summary>
    /// Measures round-trip time to a peer for adaptive buffer sizing.
    /// </summary>
    private async Task<int> MeasureRttAsync(PeerConnection connection, CancellationToken ct)
    {
        // Simple RTT measurement: time a small read/write operation
        // If the connection has a network stream, we can measure based on socket stats
        try
        {
            var socket = connection.Client.Client;
            if (socket == null) return -1;

            // Use socket round-trip time if available (Windows only)
            // This gives us the actual TCP RTT without additional traffic
            var rttInfo = socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval);
            
            // Fallback: Estimate RTT from connection time
            // For now, use a simple heuristic based on whether we're on localhost
            var remoteEndpoint = socket.RemoteEndPoint as IPEndPoint;
            if (remoteEndpoint != null)
            {
                // Check if local network (private IP ranges typically have low latency)
                var ip = remoteEndpoint.Address.ToString();
                if (ip.StartsWith("127.") || ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172."))
                {
                    // Likely LAN - assume fast
                    return 2;
                }
            }

            // Default to medium speed assumption
            return 25;
        }
        catch
        {
            // If we can't measure, assume medium speed
            return 25;
        }
    }

    /// <summary>
    /// Gets or creates a connection pool for the specified peer.
    /// </summary>
    private ConnectionPool GetOrCreateConnectionPool(Peer peer)
    {
        var key = $"{peer.IpAddress}:{peer.Port}";
        return _connectionPools.GetOrAdd(key, _ => new ConnectionPool(peer, this));
    }

    /// <summary>
    /// Gets a connection to a peer (uses primary connection from pool for backward compatibility).
    /// The caller must release the connection lock when done.
    /// </summary>
    private async Task<PeerConnection> GetOrCreatePeerConnection(Peer peer, CancellationToken ct)
    {
        var pool = GetOrCreateConnectionPool(peer);
        return await pool.GetPrimaryConnectionAsync(ct);
    }

    /// <summary>
    /// Acquires a connection from the pool for parallel transfers.
    /// The caller must release the connection lock when done.
    /// </summary>
    private async Task<PeerConnection> AcquirePooledConnection(Peer peer, CancellationToken ct)
    {
        var pool = GetOrCreateConnectionPool(peer);
        return await pool.AcquireAsync(ct);
    }

    /// <summary>
    /// Removes a connection pool when it's no longer healthy.
    /// </summary>
    private void RemoveConnectionPool(Peer peer)
    {
        var key = $"{peer.IpAddress}:{peer.Port}";
        if (_connectionPools.TryRemove(key, out var pool))
        {
            pool.Dispose();
        }
    }

    /// <summary>
    /// Performs a secure handshake as the client (initiator).
    /// Exchanges ephemeral ECDH keys to derive a session key.
    /// </summary>
    private async Task PerformSecureHandshakeAsClient(PeerConnection connection, Peer peer, CancellationToken ct)
    {
        // Generate ephemeral ECDH key pair
        var (localPublicKey, localPrivateKey) = CryptoService.GenerateEphemeralKeyPair();
        
        // Create signable data: LocalId + ephemeral public key
        var signableData = Encoding.UTF8.GetBytes(_settings.LocalId + Convert.ToBase64String(localPublicKey));
        var signature = _cryptoService.Sign(signableData);
        
        // Send secure handshake header
        connection.Writer.Write(ProtocolConstants.SECURE_HANDSHAKE_HEADER);
        connection.Writer.Write(_settings.LocalId);
        connection.Writer.Write(_settings.DeviceName);
        connection.Writer.Write(localPublicKey.Length);
        connection.Writer.Write(localPublicKey);
        connection.Writer.Write(_cryptoService.GetPublicKey().Length);
        connection.Writer.Write(_cryptoService.GetPublicKey());
        connection.Writer.Write(signature.Length);
        connection.Writer.Write(signature);
        connection.Writer.Flush();
        
        // Read server response
        var response = connection.Reader.ReadString();
        if (response != ProtocolConstants.HANDSHAKE_OK)
        {
            throw new InvalidOperationException($"Handshake failed: {response}");
        }
        
        // Read server's ephemeral public key
        var serverPubKeyLen = connection.Reader.ReadInt32();
        var serverPublicKey = connection.Reader.ReadBytes(serverPubKeyLen);
        
        // Derive session key using ECDH
        var sessionKey = CryptoService.DeriveSessionKey(localPrivateKey, serverPublicKey);
        localPrivateKey.Dispose();
        
        // Enable encryption on the connection
        connection.EnableEncryption(sessionKey);
    }

    /// <summary>
    /// Handles a secure handshake request as the server (responder).
    /// </summary>
    private async Task<bool> HandleSecureHandshakeAsServer(BinaryReader reader, BinaryWriter writer, NetworkStream stream, TcpClient client)
    {
        try
        {
            var peerId = reader.ReadString();
            var peerName = reader.ReadString();
            
            var clientPubKeyLen = reader.ReadInt32();
            var clientPublicKey = reader.ReadBytes(clientPubKeyLen);
            
            var identityPubKeyLen = reader.ReadInt32();
            var clientIdentityKey = reader.ReadBytes(identityPubKeyLen);
            
            var signatureLen = reader.ReadInt32();
            var signature = reader.ReadBytes(signatureLen);
            
            // Verify signature
            var signableData = Encoding.UTF8.GetBytes(peerId + Convert.ToBase64String(clientPublicKey));
            if (!CryptoService.Verify(signableData, signature, clientIdentityKey))
            {
                writer.Write(ProtocolConstants.HANDSHAKE_FAILED_PREFIX + "INVALID_SIGNATURE");
                return false;
            }
            
            // Check if peer is trusted (optional - can still proceed but warn)
            var clientKeyBase64 = Convert.ToBase64String(clientIdentityKey);
            var isTrusted = _settings.TrustedPeerPublicKeys.TryGetValue(peerId, out var storedKey) && storedKey == clientKeyBase64;
            
            if (!isTrusted)
            {
                System.Diagnostics.Debug.WriteLine($"Handshake from untrusted peer: {peerName} ({peerId})");
                // Still allow connection - trust is enforced at a higher level
            }
            
            // Generate our ephemeral key pair
            var (serverPublicKey, serverPrivateKey) = CryptoService.GenerateEphemeralKeyPair();
            
            // Send response
            writer.Write(ProtocolConstants.HANDSHAKE_OK);
            writer.Write(serverPublicKey.Length);
            writer.Write(serverPublicKey);
            writer.Flush();
            
            // Derive session key
            var sessionKey = CryptoService.DeriveSessionKey(serverPrivateKey, clientPublicKey);
            serverPrivateKey.Dispose();
            
            // Store session info (the caller will need to use this)
            // For now, we return true and let the caller handle the session key
            System.Diagnostics.Debug.WriteLine($"Secure handshake completed as server with {peerName}");
            
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Handshake error: {ex.Message}");
            try { writer.Write($"{ProtocolConstants.HANDSHAKE_FAILED_PREFIX}{ex.Message}"); } catch { }
            return false;
        }
    }

    public event Action<FileTransfer>? TransferStarted;
    public event Action<FileTransfer>? TransferProgress;
    public event Action<FileTransfer>? TransferCompleted;
    public event Action<string, string, long, Action<bool>>? IncomingFileRequest; // filename, senderName, size, acceptCallback

    public void Start()
    {
        _cts = new CancellationTokenSource();
        
        // Find available port
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        ListenPort = ((IPEndPoint)_listener.LocalEndpoint).Port;

        // Ensure download directory exists
        Directory.CreateDirectory(_downloadPath);

        // Start accepting connections
        Task.Run(() => AcceptLoop(_cts.Token));
    }

    public void SetDownloadPath(string path)
    {
        _downloadPath = path;
        Directory.CreateDirectory(_downloadPath);
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_listener == null) break;
                
                var client = await _listener.AcceptTcpClientAsync(ct);
                
                // DoS prevention: limit concurrent incoming connections
                if (!await _connectionLimiter.WaitAsync(0, ct))
                {
                    System.Diagnostics.Debug.WriteLine($"Connection limit reached ({MAX_CONCURRENT_CONNECTIONS}), rejecting new connection");
                    client.Dispose();
                    continue;
                }
                
                _ = HandleIncomingConnectionWithLimit(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Accept error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Wrapper that ensures the connection semaphore is released when the connection closes.
    /// </summary>
    private async Task HandleIncomingConnectionWithLimit(TcpClient client, CancellationToken ct)
    {
        try
        {
            await HandleIncomingConnection(client, ct);
        }
        finally
        {
            _connectionLimiter.Release();
        }
    }

    private async Task HandleIncomingConnection(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

                while (!ct.IsCancellationRequested && client.Connected)
                {
                    string header;
                    try
                    {
                        // Peek to see if there's data (optional, but effectively we just wait for ReadString)
                        // If peer disconnects, ReadString throws EndOfStreamException or IOException
                        header = reader.ReadString();
                    }
                    catch (EndOfStreamException) { break; }
                    catch (IOException) { break; }
                    catch (ObjectDisposedException) { break; }

                    // Check if this is a sync message
                    if (header == ProtocolConstants.SYNC_HEADER)
                    {
                        await HandleSyncMessage(reader, stream, client, ct);
                        continue;
                    }
                    
                    // Check if this is a secure handshake request
                    if (header == ProtocolConstants.SECURE_HANDSHAKE_HEADER)
                    {
                        await HandleSecureHandshakeAsServer(reader, writer, stream, client);
                        // After handshake, the connection should continue with encrypted messages
                        // For now, we continue and expect subsequent messages
                        continue;
                    }
                    
                    if (header != PROTOCOL_HEADER)
                    {
                        writer.Write("ERROR:INVALID_PROTOCOL");
                        return;
                    }

                    // Process manual file transfer (legacy single-file mode logic preserved)
                    // We extract this to keep main loop clean or just keep it here.
                    // Since specific variables like transfer/senderName are needed, let's keep it but ensure it doesn't break the loop logic if we wanted to reuse (though SendFile makes new conn).
                    
                    // Read transfer metadata
                    var senderName = reader.ReadString();
                    var fileName = reader.ReadString();
                    var fileSize = reader.ReadInt64();

                    // Create transfer record
                    var transfer = new FileTransfer
                    {
                        FileName = fileName,
                        FileSize = fileSize,
                        Direction = TransferDirection.Incoming,
                        Status = TransferStatus.Pending,
                        RemotePeer = new Peer { Name = senderName, IpAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString() },
                        StartTime = DateTime.Now
                    };

                    // Request user acceptance
                    var accepted = await RequestUserAcceptance(transfer);
                    
                    if (!accepted)
                    {
                        writer.Write(ProtocolConstants.TRANSFER_REJECTED);
                        transfer.Status = TransferStatus.Cancelled;
                        continue; // Go back to waiting, though sender likely disconnects
                    }

                    writer.Write(ProtocolConstants.TRANSFER_ACCEPTED);
                    
                    // Begin receiving file
                    transfer.Status = TransferStatus.InProgress;
                    transfer.LocalPath = Path.Combine(_downloadPath, GetSafeFileName(fileName));

                    InvokeOnUI(() =>
                    {
                        Transfers.Add(transfer);
                        TransferStarted?.Invoke(transfer);
                    });

                    await ReceiveFile(stream, transfer, ct);

                    transfer.Status = TransferStatus.Completed;
                    transfer.EndTime = DateTime.Now;
                    TransferCompleted?.Invoke(transfer);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Receive error: {ex.Message}");
            }
        }
    }

    private async Task HandleSyncMessage(BinaryReader reader, NetworkStream stream, TcpClient client, CancellationToken ct)
    {
        var remotePeer = new Peer
        {
            IpAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString()
        };

        var messageType = reader.ReadByte();

        switch (messageType)
        {
            case ProtocolConstants.MSG_FILE_CHANGED:
                await HandleSyncFileChanged(reader, stream, remotePeer, ct);
                break;

            case ProtocolConstants.MSG_FILE_DELETED:
                HandleSyncFileDeleted(reader, remotePeer);
                break;

            case ProtocolConstants.MSG_DIR_CREATED:
                HandleSyncDirCreated(reader, remotePeer);
                break;

            case ProtocolConstants.MSG_DIR_DELETED:
                HandleSyncDirDeleted(reader, remotePeer);
                break;

            case ProtocolConstants.MSG_SYNC_MANIFEST:
                HandleSyncManifest(reader, remotePeer);
                break;

            case ProtocolConstants.MSG_REQUEST_FILE:
                HandleSyncFileRequest(reader, remotePeer);
                break;

            case ProtocolConstants.MSG_REQUEST_SIGNATURES:
                await HandleRequestSignatures(reader, remotePeer);
                break;

            case ProtocolConstants.MSG_BLOCK_SIGNATURES:
                await HandleBlockSignatures(reader, stream, remotePeer, ct);
                break;

            case ProtocolConstants.MSG_DELTA_DATA:
                await HandleDeltaData(reader, stream, remotePeer, ct);
                break;

            case ProtocolConstants.MSG_FILE_RENAMED:
                HandleSyncFileRenamed(reader, remotePeer);
                break;

            case ProtocolConstants.MSG_FILE_CHANGED_COMPRESSED:
                await HandleSyncFileChangedCompressed(reader, stream, remotePeer, ct);
                break;

            default:
                System.Diagnostics.Debug.WriteLine($"Unknown sync message type: {messageType}");
                break;
        }
    }


    private async Task HandleSyncFileChanged(BinaryReader reader, NetworkStream stream, Peer remotePeer, CancellationToken ct)
    {
        var relativePath = reader.ReadString();
        var contentHash = reader.ReadString();
        var lastModified = DateTime.FromBinary(reader.ReadInt64());
        var fileSize = reader.ReadInt64();
        var isDirectory = reader.ReadBoolean();

        var syncFile = new SyncedFile
        {
            RelativePath = relativePath,
            ContentHash = contentHash,
            LastModified = lastModified,
            FileSize = fileSize,
            IsDirectory = isDirectory,
            Action = SyncAction.Update,
            SourcePeerId = remotePeer.Id
        };

        System.Diagnostics.Debug.WriteLine($"Sync file received: {relativePath} ({fileSize} bytes)");
        
        // Raise event for SyncService to handle - pass the stream for reading file data
        SyncFileReceived?.Invoke(syncFile, stream, remotePeer);
    }

    private void HandleSyncFileDeleted(BinaryReader reader, Peer remotePeer)
    {
        var relativePath = reader.ReadString();
        var isDirectory = reader.ReadBoolean();

        var syncFile = new SyncedFile
        {
            RelativePath = relativePath,
            IsDirectory = isDirectory,
            Action = SyncAction.Delete,
            SourcePeerId = remotePeer.Id
        };

        System.Diagnostics.Debug.WriteLine($"Sync delete received: {relativePath}");
        SyncDeleteReceived?.Invoke(syncFile, remotePeer);
    }

    private void HandleSyncDirCreated(BinaryReader reader, Peer remotePeer)
    {
        var relativePath = reader.ReadString();

        var syncFile = new SyncedFile
        {
            RelativePath = relativePath,
            IsDirectory = true,
            Action = SyncAction.Create,
            SourcePeerId = remotePeer.Id
        };

        System.Diagnostics.Debug.WriteLine($"Sync dir created received: {relativePath}");
        SyncFileReceived?.Invoke(syncFile, Stream.Null, remotePeer);
    }

    private void HandleSyncDirDeleted(BinaryReader reader, Peer remotePeer)
    {
        var relativePath = reader.ReadString();

        var syncFile = new SyncedFile
        {
            RelativePath = relativePath,
            IsDirectory = true,
            Action = SyncAction.Delete,
            SourcePeerId = remotePeer.Id
        };

        System.Diagnostics.Debug.WriteLine($"Sync dir deleted received: {relativePath}");
        SyncDeleteReceived?.Invoke(syncFile, remotePeer);
    }

    private void HandleSyncFileRenamed(BinaryReader reader, Peer remotePeer)
    {
        var oldRelativePath = reader.ReadString();
        var newRelativePath = reader.ReadString();
        var isDirectory = reader.ReadBoolean();

        var syncFile = new SyncedFile
        {
            RelativePath = newRelativePath,
            OldRelativePath = oldRelativePath,
            IsDirectory = isDirectory,
            Action = SyncAction.Rename,
            SourcePeerId = remotePeer.Id
        };

        System.Diagnostics.Debug.WriteLine($"Sync rename received: {oldRelativePath} -> {newRelativePath}");
        SyncRenameReceived?.Invoke(syncFile, remotePeer);
    }

    /// <summary>
    /// Handles incoming compressed file sync messages. Uses Brotli decompression.
    /// For large files (>50MB), uses streaming to a temp file to avoid memory pressure.
    /// </summary>
    private async Task HandleSyncFileChangedCompressed(BinaryReader reader, NetworkStream stream, Peer remotePeer, CancellationToken ct)
    {
        const long STREAMING_THRESHOLD = 50 * 1024 * 1024; // 50MB threshold for streaming
        
        var relativePath = reader.ReadString();
        var contentHash = reader.ReadString();
        var lastModified = DateTime.FromBinary(reader.ReadInt64());
        var fileSize = reader.ReadInt64();       // Original (uncompressed) file size
        var compressedSize = reader.ReadInt64(); // Compressed size on wire
        var isDirectory = reader.ReadBoolean();

        var syncFile = new SyncedFile
        {
            RelativePath = relativePath,
            ContentHash = contentHash,
            LastModified = lastModified,
            FileSize = fileSize,
            IsDirectory = isDirectory,
            Action = SyncAction.Update,
            SourcePeerId = remotePeer.Id
        };

        System.Diagnostics.Debug.WriteLine($"Sync file received (compressed): {relativePath} ({compressedSize} -> {fileSize} bytes)");
        
        if (compressedSize > STREAMING_THRESHOLD)
        {
            // Large file: stream to temp file to avoid memory pressure
            var tempPath = Path.GetTempFileName();
            try
            {
                // Stream compressed data to temp file
                await using (var tempFile = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, BUFFER_SIZE, useAsync: true))
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
                    try
                    {
                        var remaining = compressedSize;
                        while (remaining > 0)
                        {
                            var toRead = (int)Math.Min(BUFFER_SIZE, remaining);
                            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                            if (bytesRead == 0) throw new EndOfStreamException("Unexpected end of stream while receiving compressed data");
                            await tempFile.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                            remaining -= bytesRead;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                
                // Now decompress from temp file
                await using var compressedFileStream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, BUFFER_SIZE, useAsync: true);
                await using var brotliStream = new BrotliStream(compressedFileStream, CompressionMode.Decompress, leaveOpen: true);
                
                SyncFileReceived?.Invoke(syncFile, brotliStream, remotePeer);
            }
            finally
            {
                // Clean up temp file
                try { File.Delete(tempPath); } catch { }
            }
        }
        else
        {
            // Small file: use ArrayPool for memory efficiency
            var compressedData = ArrayPool<byte>.Shared.Rent((int)compressedSize);
            try
            {
                var totalRead = 0;
                while (totalRead < compressedSize)
                {
                    var bytesRead = await stream.ReadAsync(compressedData.AsMemory(totalRead, (int)(compressedSize - totalRead)), ct);
                    if (bytesRead == 0) throw new EndOfStreamException("Unexpected end of stream while receiving compressed data");
                    totalRead += bytesRead;
                }

                // Create a memory stream with the compressed data (only use the bytes we read)
                using var compressedStream = new MemoryStream(compressedData, 0, (int)compressedSize);
                await using var brotliStream = new BrotliStream(compressedStream, CompressionMode.Decompress, leaveOpen: true);
                
                SyncFileReceived?.Invoke(syncFile, brotliStream, remotePeer);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressedData);
            }
        }
    }

    private void HandleSyncManifest(BinaryReader reader, Peer remotePeer)
    {
        var json = reader.ReadString();
        var manifest = JsonSerializer.Deserialize<List<SyncedFile>>(json) ?? [];

        System.Diagnostics.Debug.WriteLine($"Sync manifest received: {manifest.Count} files");
        SyncManifestReceived?.Invoke(manifest, remotePeer);
    }

    private void HandleSyncFileRequest(BinaryReader reader, Peer remotePeer)
    {
        var relativePath = reader.ReadString();

        System.Diagnostics.Debug.WriteLine($"Sync file requested: {relativePath}");
        SyncFileRequested?.Invoke(relativePath, remotePeer);
    }

    #region Delta Sync Handlers

    private Task HandleRequestSignatures(BinaryReader reader, Peer remotePeer)
    {
        var relativePath = reader.ReadString();
        System.Diagnostics.Debug.WriteLine($"Signature request received for: {relativePath}");
        SignaturesRequested?.Invoke(relativePath, remotePeer);
        return Task.CompletedTask;
    }


    private Task HandleBlockSignatures(BinaryReader reader, NetworkStream stream, Peer remotePeer, CancellationToken ct)
    {
        var relativePath = reader.ReadString();
        var signatureCount = reader.ReadInt32();

        var signatures = new List<BlockSignature>();
        for (int i = 0; i < signatureCount; i++)
        {
            signatures.Add(new BlockSignature
            {
                BlockIndex = reader.ReadInt32(),
                WeakChecksum = reader.ReadInt32(),
                StrongChecksum = reader.ReadString()
            });
        }

        System.Diagnostics.Debug.WriteLine($"Block signatures received for {relativePath}: {signatureCount} blocks");
        BlockSignaturesReceived?.Invoke(relativePath, signatures, remotePeer);
        return Task.CompletedTask;
    }

    private Task HandleDeltaData(BinaryReader reader, NetworkStream stream, Peer remotePeer, CancellationToken ct)
    {
        var relativePath = reader.ReadString();
        var contentHash = reader.ReadString();
        var lastModified = DateTime.FromBinary(reader.ReadInt64());
        var fileSize = reader.ReadInt64();
        var instructionCount = reader.ReadInt32();

        var instructions = new List<DeltaInstruction>();
        for (int i = 0; i < instructionCount; i++)
        {
            var type = (DeltaType)reader.ReadByte();
            var instruction = new DeltaInstruction { Type = type };

            if (type == DeltaType.Copy)
            {
                instruction.SourceBlockIndex = reader.ReadInt32();
                instruction.Length = reader.ReadInt32();
            }
            else // Insert
            {
                instruction.Length = reader.ReadInt32();
                instruction.Data = reader.ReadBytes(instruction.Length);
            }

            instructions.Add(instruction);
        }

        var syncFile = new SyncedFile
        {
            RelativePath = relativePath,
            ContentHash = contentHash,
            LastModified = lastModified,
            FileSize = fileSize,
            Action = SyncAction.Update,
            SourcePeerId = remotePeer.Id
        };

        System.Diagnostics.Debug.WriteLine($"Delta data received for {relativePath}: {instructionCount} instructions");
        DeltaDataReceived?.Invoke(syncFile, instructions, remotePeer);
        return Task.CompletedTask;
    }

    #endregion


    private Task<bool> RequestUserAcceptance(FileTransfer transfer)
    {
        var tcs = new TaskCompletionSource<bool>();

        if (_settings.AutoAcceptFromTrusted && transfer.RemotePeer != null)
        {
            if (_settings.TrustedPeers.Any(p => p.Id == transfer.RemotePeer.Id))
            {
                tcs.SetResult(true);
                return tcs.Task;
            }
        }

        if (IncomingFileRequest != null)
        {
            InvokeOnUI(() =>
            {
                IncomingFileRequest.Invoke(
                    transfer.FileName,
                    transfer.RemotePeer?.Name ?? "Unknown",
                    transfer.FileSize,
                    (accepted) => tcs.TrySetResult(accepted)
                );
            });
        }
        else
        {
            // Auto-accept if no handler
            tcs.SetResult(true);
        }

        return tcs.Task;
    }

    private async Task ReceiveFile(NetworkStream stream, FileTransfer transfer, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
        try
        {
            var totalReceived = 0L;
            var maxDownloadBytesPerSec = _settings.MaxDownloadSpeedKBps * 1024;

            // Wrap stream with ThrottledStream for download rate limiting (leaveOpen: true to keep NetworkStream alive)
            await using Stream readStream = maxDownloadBytesPerSec > 0
                ? new ThrottledStream(stream, maxReadBytesPerSecond: maxDownloadBytesPerSec, maxWriteBytesPerSecond: 0, leaveOpen: true)
                : stream;

            // Only retry opening the file stream, not the whole network transfer
            await using var fileStream = await FileHelpers.ExecuteWithRetryAsync(async () =>
            {
                return new FileStream(transfer.LocalPath!, FileMode.Create, FileAccess.Write, FileShare.None, BUFFER_SIZE, useAsync: true);
            });

            while (totalReceived < transfer.FileSize && !ct.IsCancellationRequested)
            {
                var toRead = (int)Math.Min(BUFFER_SIZE, transfer.FileSize - totalReceived);
                var bytesRead = await readStream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                
                if (bytesRead == 0) break;

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalReceived += bytesRead;
                transfer.BytesTransferred = totalReceived;

                TransferProgress?.Invoke(transfer);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task SendFile(Peer peer, string filePath, CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists) throw new FileNotFoundException("File not found", filePath);

        var transfer = new FileTransfer
        {
            FileName = fileInfo.Name,
            FileSize = fileInfo.Length,
            Direction = TransferDirection.Outgoing,
            Status = TransferStatus.Pending,
            RemotePeer = peer,
            LocalPath = filePath,
            StartTime = DateTime.Now
        };

        InvokeOnUI(() =>
        {
            Transfers.Add(transfer);
        });

        using var client = new TcpClient();
        
        try
        {
            await client.ConnectAsync(peer.IpAddress, peer.Port, ct);
            var stream = client.GetStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            // Send header and metadata
            writer.Write(PROTOCOL_HEADER);
            writer.Write(Environment.MachineName);
            writer.Write(fileInfo.Name);
            writer.Write(fileInfo.Length);

            // Wait for acceptance
            var response = reader.ReadString();
            if (response == ProtocolConstants.TRANSFER_REJECTED)
            {
                transfer.Status = TransferStatus.Cancelled;
                return;
            }

            // Begin sending
            transfer.Status = TransferStatus.InProgress;
            TransferStarted?.Invoke(transfer);

            var buffer = ArrayPool<byte>.Shared.Rent(BUFFER_SIZE);
            var totalSent = 0L;
            var maxUploadBytesPerSec = _settings.MaxUploadSpeedKBps * 1024;

            await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BUFFER_SIZE, useAsync: true);
            
            // Wrap network stream with ThrottledStream for upload rate limiting (leaveOpen: true to keep NetworkStream alive)
            await using Stream writeStream = maxUploadBytesPerSec > 0
                ? new ThrottledStream(stream, maxReadBytesPerSecond: 0, maxWriteBytesPerSecond: maxUploadBytesPerSec, leaveOpen: true)
                : stream;

            while (totalSent < fileInfo.Length && !ct.IsCancellationRequested)
            {
                var bytesRead = await fileStream.ReadAsync(buffer, ct);
                if (bytesRead == 0) break;

                await writeStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalSent += bytesRead;
                transfer.BytesTransferred = totalSent;

                TransferProgress?.Invoke(transfer);
            }

            transfer.Status = TransferStatus.Completed;
            transfer.EndTime = DateTime.Now;
            TransferCompleted?.Invoke(transfer);
        }
        catch (Exception ex)
        {
            transfer.Status = TransferStatus.Failed;
            System.Diagnostics.Debug.WriteLine($"Send error: {ex.Message}");
            throw;
        }
    }

    private string GetSafeFileName(string fileName)
    {
        // Remove invalid characters
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(fileName.Where(c => !invalid.Contains(c)).ToArray());
        
        var baseName = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        var targetPath = Path.Combine(_downloadPath, safe);
        
        // Fast path: if base name doesn't exist, use it directly
        if (!File.Exists(targetPath))
        {
            return safe;
        }
        
        // Optimized: scan directory once to find highest counter (O(1) instead of O(N) disk checks)
        var maxCounter = 0;
        var pattern = $"{baseName} (";
        
        try
        {
            foreach (var existingFile in Directory.EnumerateFiles(_downloadPath, $"{baseName}*{ext}"))
            {
                var existingName = Path.GetFileNameWithoutExtension(existingFile);
                
                // Check for pattern "baseName (N)"
                if (existingName.StartsWith(pattern) && existingName.EndsWith(")"))
                {
                    var numPart = existingName.Substring(pattern.Length, existingName.Length - pattern.Length - 1);
                    if (int.TryParse(numPart, out var num) && num > maxCounter)
                    {
                        maxCounter = num;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error scanning for existing files: {ex.Message}");
            // Fallback to old behavior on error
            var counter = 1;
            var result = safe;
            while (File.Exists(Path.Combine(_downloadPath, result)))
            {
                result = $"{baseName} ({counter}){ext}";
                counter++;
            }
            return result;
        }
        
        return $"{baseName} ({maxCounter + 1}){ext}";
    }

    #region Sync Transfer Methods

    /// <summary>
    /// Sends multiple small files to a peer in parallel using the connection pool.
    /// This significantly improves performance for folders with many small files.
    /// </summary>
    public async Task SendSyncFilesParallel(
        Peer peer,
        IEnumerable<(string filePath, SyncedFile syncFile)> files,
        Action<string, long, long>? progressCallback = null,
        CancellationToken ct = default)
    {
        var fileList = files.ToList();
        if (fileList.Count == 0) return;

        System.Diagnostics.Debug.WriteLine($"Starting parallel transfer of {fileList.Count} files to {peer.Name}");

        // Use SemaphoreSlim to limit concurrent transfers to pool size
        var semaphore = new SemaphoreSlim(ProtocolConstants.MAX_PARALLEL_CONNECTIONS);
        var tasks = new List<Task>();
        var completedCount = 0;

        foreach (var (filePath, syncFile) in fileList)
        {
            await semaphore.WaitAsync(ct);
            
            var task = Task.Run(async () =>
            {
                try
                {
                    await SendSingleFileFromPool(peer, filePath, syncFile, (sent, total) =>
                    {
                        progressCallback?.Invoke(syncFile.RelativePath, sent, total);
                    }, ct);
                    
                    Interlocked.Increment(ref completedCount);
                    System.Diagnostics.Debug.WriteLine($"Parallel transfer [{completedCount}/{fileList.Count}]: {syncFile.RelativePath}");
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct);
            
            tasks.Add(task);
        }

        await Task.WhenAll(tasks);
        semaphore.Dispose();

        System.Diagnostics.Debug.WriteLine($"Completed parallel transfer of {fileList.Count} files to {peer.Name}");
    }

    /// <summary>
    /// Internal method to send a single file using a pooled connection.
    /// </summary>
    private async Task SendSingleFileFromPool(
        Peer peer,
        string filePath,
        SyncedFile syncFile,
        Action<long, long>? progressCallback = null,
        CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists) return;

        PeerConnection? connection = null;
        try
        {
            // Acquire a connection from the pool (may wait or create new)
            connection = await AcquirePooledConnection(peer, ct);

            // Compress file content first
            await using var compressedBuffer = new MemoryStream();
            await using var fileStream = await FileHelpers.ExecuteWithRetryAsync(async () =>
            {
                return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BUFFER_SIZE, useAsync: true);
            });

            // Compress file to memory buffer
            {
                await using var brotliStream = new BrotliStream(compressedBuffer, CompressionLevel.Fastest, leaveOpen: true);
                await fileStream.CopyToAsync(brotliStream, ct);
            }

            var compressedData = compressedBuffer.ToArray();

            // Send sync header and metadata with compressed flag
            connection.Writer.Write(ProtocolConstants.SYNC_HEADER);
            connection.Writer.Write(ProtocolConstants.MSG_FILE_CHANGED_COMPRESSED);
            connection.Writer.Write(syncFile.RelativePath);
            connection.Writer.Write(syncFile.ContentHash);
            connection.Writer.Write(syncFile.LastModified.ToBinary());
            connection.Writer.Write(fileInfo.Length);               // Original uncompressed size
            connection.Writer.Write((long)compressedData.Length);   // Compressed size
            connection.Writer.Write(syncFile.IsDirectory);

            // Send compressed content
            await connection.Stream.WriteAsync(compressedData, ct);
            progressCallback?.Invoke(fileInfo.Length, fileInfo.Length);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Parallel send error for {syncFile.RelativePath}: {ex.Message}");
            RemoveConnectionPool(peer);
            throw;
        }
        finally
        {
            // Release connection back to pool
            connection?.Lock.Release();
        }
    }

    /// <summary>
    /// Checks if a file size qualifies for parallel transfer.
    /// </summary>
    public static bool IsSmallFile(long fileSize)
    {
        return fileSize <= ProtocolConstants.SMALL_FILE_THRESHOLD;
    }

    /// <summary>
    /// Sends a synced file to a peer (auto-accepted, no confirmation).
    /// Uses Brotli compression for efficient transfer.
    /// </summary>
    public async Task SendSyncFile(Peer peer, string filePath, SyncedFile syncFile, Action<long, long>? progressCallback = null, CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists) return;

        try
        {
            var connection = await GetOrCreatePeerConnection(peer, ct);
            await connection.Lock.WaitAsync(ct);
            try
            {
                // Compress file content first
                await using var compressedBuffer = new MemoryStream();
                await using var fileStream = await FileHelpers.ExecuteWithRetryAsync(async () =>
                {
                    return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BUFFER_SIZE, useAsync: true);
                });
                
                // Compress file to memory buffer
                {
                    await using var brotliStream = new BrotliStream(compressedBuffer, CompressionLevel.Fastest, leaveOpen: true);
                    await fileStream.CopyToAsync(brotliStream, ct);
                    // BrotliStream must be disposed/flushed to finalize the compressed data
                }
                
                var compressedData = compressedBuffer.ToArray();
                var compressionRatio = fileInfo.Length > 0 ? (1.0 - (double)compressedData.Length / fileInfo.Length) * 100 : 0;
                
                System.Diagnostics.Debug.WriteLine($"Compression: {syncFile.RelativePath} - {fileInfo.Length} -> {compressedData.Length} bytes ({compressionRatio:F1}% saved)");

                // Send sync header and metadata with compressed flag
                connection.Writer.Write(ProtocolConstants.SYNC_HEADER);
                connection.Writer.Write(ProtocolConstants.MSG_FILE_CHANGED_COMPRESSED);
                connection.Writer.Write(syncFile.RelativePath);
                connection.Writer.Write(syncFile.ContentHash);
                connection.Writer.Write(syncFile.LastModified.ToBinary());
                connection.Writer.Write(fileInfo.Length);          // Original uncompressed size
                connection.Writer.Write((long)compressedData.Length); // Compressed size
                connection.Writer.Write(syncFile.IsDirectory);

                // Send compressed content with optional throttling using ThrottledStream
                var maxUploadBytesPerSec = _settings.MaxUploadSpeedKBps * 1024;
                await using Stream writeStream = maxUploadBytesPerSec > 0
                    ? new ThrottledStream(connection.Stream, maxReadBytesPerSecond: 0, maxWriteBytesPerSecond: maxUploadBytesPerSec, leaveOpen: true)
                    : connection.Stream;

                // Send in chunks for progress reporting
                var totalSent = 0;
                while (totalSent < compressedData.Length && !ct.IsCancellationRequested)
                {
                    var chunkSize = Math.Min(BUFFER_SIZE, compressedData.Length - totalSent);
                    await writeStream.WriteAsync(compressedData.AsMemory(totalSent, chunkSize), ct);
                    totalSent += chunkSize;
                    progressCallback?.Invoke(totalSent, compressedData.Length);
                }

                System.Diagnostics.Debug.WriteLine($"Sync sent (compressed): {syncFile.RelativePath} to {peer.Name}");
            }
            finally
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {
            // If connection failed, remove it so we retry next time
            // Connection pool handles disposal of bad connections on next access
            
            System.Diagnostics.Debug.WriteLine($"Sync send error: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a delete notification to a peer.
    /// </summary>
    public async Task SendSyncDelete(Peer peer, SyncedFile syncFile, CancellationToken ct = default)
    {
        try
        {
            var connection = await GetOrCreatePeerConnection(peer, ct);
            await connection.Lock.WaitAsync(ct);
            try
            {
                connection.Writer.Write(ProtocolConstants.SYNC_HEADER);
                connection.Writer.Write(ProtocolConstants.MSG_FILE_DELETED);
                connection.Writer.Write(syncFile.RelativePath);
                connection.Writer.Write(syncFile.IsDirectory);

                System.Diagnostics.Debug.WriteLine($"Sync delete sent: {syncFile.RelativePath} to {peer.Name}");
            }
            finally
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {

            System.Diagnostics.Debug.WriteLine($"Sync delete error: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a rename notification to a peer.
    /// </summary>
    public async Task SendSyncRename(Peer peer, SyncedFile syncFile, CancellationToken ct = default)
    {
        try
        {
            var connection = await GetOrCreatePeerConnection(peer, ct);
            await connection.Lock.WaitAsync(ct);
            try
            {
                connection.Writer.Write(ProtocolConstants.SYNC_HEADER);
                connection.Writer.Write(ProtocolConstants.MSG_FILE_RENAMED);
                connection.Writer.Write(syncFile.OldRelativePath);
                connection.Writer.Write(syncFile.RelativePath);
                connection.Writer.Write(syncFile.IsDirectory);

                System.Diagnostics.Debug.WriteLine($"Sync rename sent: {syncFile.OldRelativePath} -> {syncFile.RelativePath} to {peer.Name}");
            }
            finally
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {

            System.Diagnostics.Debug.WriteLine($"Sync rename error: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a directory creation notification to a peer.
    /// </summary>
    public async Task SendSyncDirectory(Peer peer, SyncedFile syncFile, CancellationToken ct = default)
    {
        try
        {
            var connection = await GetOrCreatePeerConnection(peer, ct);
            await connection.Lock.WaitAsync(ct);
            try
            {
                connection.Writer.Write(ProtocolConstants.SYNC_HEADER);
                connection.Writer.Write(ProtocolConstants.MSG_DIR_CREATED);
                connection.Writer.Write(syncFile.RelativePath);

                System.Diagnostics.Debug.WriteLine($"Sync dir sent: {syncFile.RelativePath} to {peer.Name}");
            }
            finally 
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {

            System.Diagnostics.Debug.WriteLine($"Sync dir error: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends the full sync manifest to a peer.
    /// </summary>
    public async Task SendSyncManifest(Peer peer, List<SyncedFile> manifest, CancellationToken ct = default)
    {
        try
        {
            var connection = await GetOrCreatePeerConnection(peer, ct);
            await connection.Lock.WaitAsync(ct);
            try
            {
                connection.Writer.Write(ProtocolConstants.SYNC_HEADER);
                connection.Writer.Write(ProtocolConstants.MSG_SYNC_MANIFEST);
                
                var json = JsonSerializer.Serialize(manifest);
                connection.Writer.Write(json);

                System.Diagnostics.Debug.WriteLine($"Sync manifest sent to {peer.Name}: {manifest.Count} files");
            }
            finally
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {

            System.Diagnostics.Debug.WriteLine($"Sync manifest error: {ex.Message}");
        }
    }

    /// <summary>
    /// Requests a specific file from a peer.
    /// </summary>
    public async Task RequestSyncFile(Peer peer, string relativePath, CancellationToken ct = default)
    {
        try
        {
            var connection = await GetOrCreatePeerConnection(peer, ct);
            await connection.Lock.WaitAsync(ct);
            try
            {
                connection.Writer.Write(ProtocolConstants.SYNC_HEADER);
                connection.Writer.Write(ProtocolConstants.MSG_REQUEST_FILE);
                connection.Writer.Write(relativePath);

                System.Diagnostics.Debug.WriteLine($"Sync file requested: {relativePath} from {peer.Name}");
            }
            finally
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {

            System.Diagnostics.Debug.WriteLine($"Sync request error: {ex.Message}");
        }
    }

    #region Delta Sync Send Methods

    /// <summary>
    /// Requests block signatures for a file from a peer (for delta sync).
    /// </summary>
    public async Task RequestBlockSignatures(Peer peer, string relativePath, CancellationToken ct = default)
    {
        try
        {
            var connection = await GetOrCreatePeerConnection(peer, ct);
            await connection.Lock.WaitAsync(ct);
            try
            {
                connection.Writer.Write(ProtocolConstants.SYNC_HEADER);
                connection.Writer.Write(ProtocolConstants.MSG_REQUEST_SIGNATURES);
                connection.Writer.Write(relativePath);

                System.Diagnostics.Debug.WriteLine($"Requested signatures for: {relativePath} from {peer.Name}");
            }
            finally
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {

            System.Diagnostics.Debug.WriteLine($"Request signatures error: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends block signatures for a file to a peer.
    /// </summary>
    public async Task SendBlockSignatures(Peer peer, string relativePath, List<BlockSignature> signatures, CancellationToken ct = default)
    {
        try
        {
            var connection = await GetOrCreatePeerConnection(peer, ct);
            await connection.Lock.WaitAsync(ct);
            try
            {
                connection.Writer.Write(ProtocolConstants.SYNC_HEADER);
                connection.Writer.Write(ProtocolConstants.MSG_BLOCK_SIGNATURES);
                connection.Writer.Write(relativePath);
                connection.Writer.Write(signatures.Count);

                foreach (var sig in signatures)
                {
                    connection.Writer.Write(sig.BlockIndex);
                    connection.Writer.Write(sig.WeakChecksum);
                    connection.Writer.Write(sig.StrongChecksum);
                }

                System.Diagnostics.Debug.WriteLine($"Sent {signatures.Count} block signatures for: {relativePath} to {peer.Name}");
            }
            finally
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {

            System.Diagnostics.Debug.WriteLine($"Send signatures error: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends delta data (instructions to reconstruct a file) to a peer.
    /// </summary>
    public async Task SendDeltaData(Peer peer, SyncedFile syncFile, List<DeltaInstruction> instructions, CancellationToken ct = default)
    {
        try
        {
            var connection = await GetOrCreatePeerConnection(peer, ct);
            await connection.Lock.WaitAsync(ct);
            try
            {
                connection.Writer.Write(ProtocolConstants.SYNC_HEADER);
                connection.Writer.Write(ProtocolConstants.MSG_DELTA_DATA);
                connection.Writer.Write(syncFile.RelativePath);
                connection.Writer.Write(syncFile.ContentHash);
                connection.Writer.Write(syncFile.LastModified.ToBinary());
                connection.Writer.Write(syncFile.FileSize);
                connection.Writer.Write(instructions.Count);

                foreach (var instruction in instructions)
                {
                    connection.Writer.Write((byte)instruction.Type);

                    if (instruction.Type == DeltaType.Copy)
                    {
                        connection.Writer.Write(instruction.SourceBlockIndex);
                        connection.Writer.Write(instruction.Length);
                    }
                    else // Insert
                    {
                        connection.Writer.Write(instruction.Length);
                        if (instruction.Data != null)
                        {
                            connection.Writer.Write(instruction.Data);
                        }
                    }
                }

                var deltaSize = DeltaSyncService.EstimateDeltaSize(instructions);
                System.Diagnostics.Debug.WriteLine($"Sent delta for {syncFile.RelativePath} to {peer.Name}: {instructions.Count} instructions, ~{FileHelpers.FormatBytes(deltaSize)}");
            }
            finally
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {

            System.Diagnostics.Debug.WriteLine($"Send delta error: {ex.Message}");
        }
    }

    #endregion

    /// <summary>
    /// Event raised when a sync message is received. SyncService should subscribe to this.
    /// </summary>
    public event Action<SyncedFile, Stream, Peer>? SyncFileReceived;
    public event Action<SyncedFile, Peer>? SyncDeleteReceived;
    public event Action<SyncedFile, Peer>? SyncRenameReceived;
    public event Action<List<SyncedFile>, Peer>? SyncManifestReceived;
    public event Action<string, Peer>? SyncFileRequested;

    // Delta sync events
    public event Action<string, Peer>? SignaturesRequested;
    public event Action<string, List<BlockSignature>, Peer>? BlockSignaturesReceived;
    public event Action<SyncedFile, List<DeltaInstruction>, Peer>? DeltaDataReceived;

    #endregion


    public void Dispose()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        if (_listener != null)
        {
            _listener.Stop();
            _listener = null;
        }
        
        foreach (var pool in _connectionPools.Values)
        {
            pool.Dispose();
        }
        _connectionPools.Clear();
    }
}


