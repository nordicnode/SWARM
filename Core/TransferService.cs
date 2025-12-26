using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Threading;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Swarm.Helpers;
using Swarm.Models;

namespace Swarm.Core;

/// <summary>
/// TCP-based file transfer service for sending and receiving files.
/// </summary>
public class TransferService : IDisposable
{
    private const int BUFFER_SIZE = ProtocolConstants.DEFAULT_BUFFER_SIZE;
    private const string PROTOCOL_HEADER = ProtocolConstants.TRANSFER_HEADER;

    private readonly Settings _settings;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _downloadPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Swarm");

    public int ListenPort { get; private set; }
    public ObservableCollection<FileTransfer> Transfers { get; } = [];
    
    public TransferService(Settings settings)
    {
        _settings = settings;
    }
    
    private readonly ConcurrentDictionary<string, PeerConnection> _activeConnections = new();

    private class PeerConnection : IDisposable
    {
        public TcpClient Client { get; }
        public NetworkStream Stream { get; }
        public BinaryWriter Writer { get; }
        public BinaryReader Reader { get; }
        public SemaphoreSlim Lock { get; } = new(1, 1);

        public PeerConnection(TcpClient client)
        {
            Client = client;
            Stream = client.GetStream();
            Writer = new BinaryWriter(Stream, Encoding.UTF8, leaveOpen: true);
            Reader = new BinaryReader(Stream, Encoding.UTF8, leaveOpen: true);
        }

        public bool IsConnected => Client.Connected;

        public void Dispose()
        {
            try { Lock.Dispose(); } catch { }
            try { Writer.Dispose(); } catch { }
            try { Reader.Dispose(); } catch { }
            try { Client.Dispose(); } catch { }
        }
    }

    private async Task<PeerConnection> GetOrCreatePeerConnection(Peer peer, CancellationToken ct)
    {
        var key = $"{peer.IpAddress}:{peer.Port}";
        
        // Remove stale connection if exists and not connected
        if (_activeConnections.TryGetValue(key, out var existing))
        {
            if (existing.IsConnected) return existing;
            
            existing.Dispose();
            _activeConnections.TryRemove(key, out _);
        }

        var client = new TcpClient();
        await client.ConnectAsync(peer.IpAddress, peer.Port, ct);
        var connection = new PeerConnection(client);
        
        _activeConnections.AddOrUpdate(key, connection, (k, old) => 
        {
            old.Dispose();
            return connection;
        });

        System.Diagnostics.Debug.WriteLine($"Created new connection to {peer.Name} ({key})");
        return connection;
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
                _ = HandleIncomingConnection(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Accept error: {ex.Message}");
            }
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
                        writer.Write("REJECTED");
                        transfer.Status = TransferStatus.Cancelled;
                        continue; // Go back to waiting, though sender likely disconnects
                    }

                    writer.Write("ACCEPTED");
                    
                    // Begin receiving file
                    transfer.Status = TransferStatus.InProgress;
                    transfer.LocalPath = Path.Combine(_downloadPath, GetSafeFileName(fileName));

                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
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
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
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
        var buffer = new byte[BUFFER_SIZE];
        var totalReceived = 0L;

        // Only retry opening the file stream, not the whole network transfer
        await using var fileStream = await FileHelpers.ExecuteWithRetryAsync(async () =>
        {
            return new FileStream(transfer.LocalPath!, FileMode.Create, FileAccess.Write, FileShare.None, BUFFER_SIZE, useAsync: true);
        });

        while (totalReceived < transfer.FileSize && !ct.IsCancellationRequested)
        {
            var toRead = (int)Math.Min(BUFFER_SIZE, transfer.FileSize - totalReceived);
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct);
            
            if (bytesRead == 0) break;

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalReceived += bytesRead;
            transfer.BytesTransferred = totalReceived;

            TransferProgress?.Invoke(transfer);
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

        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
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
            if (response == "REJECTED")
            {
                transfer.Status = TransferStatus.Cancelled;
                return;
            }

            // Begin sending
            transfer.Status = TransferStatus.InProgress;
            TransferStarted?.Invoke(transfer);

            var buffer = new byte[BUFFER_SIZE];
            var totalSent = 0L;

            await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BUFFER_SIZE, useAsync: true);

            while (totalSent < fileInfo.Length && !ct.IsCancellationRequested)
            {
                var bytesRead = await fileStream.ReadAsync(buffer, ct);
                if (bytesRead == 0) break;

                await stream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
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
        
        // If file exists, add number
        var baseName = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        var counter = 1;
        var result = safe;

        while (File.Exists(Path.Combine(_downloadPath, result)))
        {
            result = $"{baseName} ({counter}){ext}";
            counter++;
        }

        return result;
    }

    #region Sync Transfer Methods

    /// <summary>
    /// Sends a synced file to a peer (auto-accepted, no confirmation).
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
                // Send sync header and metadata
                connection.Writer.Write(ProtocolConstants.SYNC_HEADER);
                connection.Writer.Write(ProtocolConstants.MSG_FILE_CHANGED);
                connection.Writer.Write(syncFile.RelativePath);
                connection.Writer.Write(syncFile.ContentHash);
                connection.Writer.Write(syncFile.LastModified.ToBinary());
                connection.Writer.Write(fileInfo.Length);
                connection.Writer.Write(syncFile.IsDirectory);

                // Send file content
                var buffer = new byte[BUFFER_SIZE];
                await using var fileStream = await FileHelpers.ExecuteWithRetryAsync(async () =>
                {
                    return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BUFFER_SIZE, useAsync: true);
                });
                
                long totalSent = 0;
                int bytesRead;
                while ((bytesRead = await fileStream.ReadAsync(buffer, ct)) > 0)
                {
                    await connection.Stream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    totalSent += bytesRead;
                    progressCallback?.Invoke(totalSent, fileInfo.Length);
                }

                System.Diagnostics.Debug.WriteLine($"Sync sent: {syncFile.RelativePath} to {peer.Name}");
            }
            finally
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {
            // If connection failed, remove it so we retry next time
            var key = $"{peer.IpAddress}:{peer.Port}";
            if (_activeConnections.TryRemove(key, out var conn)) conn.Dispose();
            
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
            var key = $"{peer.IpAddress}:{peer.Port}";
            if (_activeConnections.TryRemove(key, out var conn)) conn.Dispose();
            System.Diagnostics.Debug.WriteLine($"Sync delete error: {ex.Message}");
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
            var key = $"{peer.IpAddress}:{peer.Port}";
            if (_activeConnections.TryRemove(key, out var conn)) conn.Dispose();
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
            var key = $"{peer.IpAddress}:{peer.Port}";
            if (_activeConnections.TryRemove(key, out var conn)) conn.Dispose();
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
            var key = $"{peer.IpAddress}:{peer.Port}";
            if (_activeConnections.TryRemove(key, out var conn)) conn.Dispose();
            System.Diagnostics.Debug.WriteLine($"Sync request error: {ex.Message}");
        }
    }

    /// <summary>
    /// Event raised when a sync message is received. SyncService should subscribe to this.
    /// </summary>
    public event Action<SyncedFile, Stream, Peer>? SyncFileReceived;
    public event Action<SyncedFile, Peer>? SyncDeleteReceived;
    public event Action<List<SyncedFile>, Peer>? SyncManifestReceived;
    public event Action<string, Peer>? SyncFileRequested;

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
        
        foreach (var conn in _activeConnections.Values)
        {
            conn.Dispose();
        }
        _activeConnections.Clear();
    }
}
