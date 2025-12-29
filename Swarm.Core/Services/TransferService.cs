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

using Swarm.Core.Abstractions;
using Serilog;

namespace Swarm.Core.Services;

/// <summary>
/// TCP-based file transfer service for sending and receiving files.
/// </summary>
public class TransferService : ITransferService
{
    private const int BUFFER_SIZE = ProtocolConstants.DEFAULT_BUFFER_SIZE;
    private const string PROTOCOL_HEADER = ProtocolConstants.TRANSFER_HEADER;
    private const int MAX_CONCURRENT_CONNECTIONS = ProtocolConstants.MAX_CONCURRENT_CONNECTIONS;

    private readonly Settings _settings;
    private readonly CryptoService _cryptoService;
    private readonly Abstractions.IDispatcher? _dispatcher;
    private readonly SemaphoreSlim _connectionLimiter = new(MAX_CONCURRENT_CONNECTIONS, MAX_CONCURRENT_CONNECTIONS);
    private readonly ConnectionManager _connectionManager;
    private readonly SecureHandshakeHandler _handshakeHandler;
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
        
        // Initialize extracted handlers
        _connectionManager = new ConnectionManager(settings, cryptoService);
        _handshakeHandler = new SecureHandshakeHandler(settings, cryptoService);
        
        // Wire up handshake callback for connection pool
        _connectionManager.HandshakeHandler = async (conn, peer, ct) =>
            await _handshakeHandler.PerformHandshakeAsClient(conn, peer, ct);
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



    /// <summary>
    /// Gets or creates a connection pool for the specified peer (delegates to ConnectionManager).
    /// </summary>
    private ConnectionPool GetOrCreateConnectionPool(Peer peer) 
        => _connectionManager.GetOrCreateConnectionPool(peer);

    /// <summary>
    /// Gets a connection to a peer (delegates to ConnectionManager).
    /// </summary>
    private async Task<PeerConnection> GetOrCreatePeerConnection(Peer peer, CancellationToken ct)
        => await _connectionManager.GetOrCreatePeerConnection(peer, ct);

    /// <summary>
    /// Acquires a connection from the pool (delegates to ConnectionManager).
    /// </summary>
    private async Task<PeerConnection> AcquirePooledConnection(Peer peer, CancellationToken ct)
        => await _connectionManager.AcquirePooledConnection(peer, ct);

    /// <summary>
    /// Removes a connection pool (delegates to ConnectionManager).
    /// </summary>
    private void RemoveConnectionPool(Peer peer)
        => _connectionManager.RemoveConnectionPool(peer);

    /// <summary>
    /// Performs a secure handshake as the client (delegates to SecureHandshakeHandler).
    /// </summary>
    private async Task PerformSecureHandshakeAsClient(PeerConnection connection, Peer peer, CancellationToken ct)
        => await _handshakeHandler.PerformHandshakeAsClient(connection, peer, ct);

    /// <summary>
    /// Handles a secure handshake as the server (delegates to SecureHandshakeHandler).
    /// </summary>
    private async Task<bool> HandleSecureHandshakeAsServer(BinaryReader reader, BinaryWriter writer, NetworkStream stream, TcpClient client)
    {
        var result = await _handshakeHandler.HandleHandshakeAsServer(reader, writer, stream, client);
        return result.Succeeded;
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
                    Log.Warning($"Connection limit reached ({MAX_CONCURRENT_CONNECTIONS}), rejecting new connection");
                    client.Dispose();
                    continue;
                }
                
                _ = HandleIncomingConnectionWithLimit(client, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error(ex, $"Accept error: {ex.Message}");
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
                Log.Error(ex, $"Receive error: {ex.Message}");
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
                Log.Warning($"Unknown sync message type: {messageType}");
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

        Log.Information($"Sync file received: {relativePath} ({fileSize} bytes)");
        
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

        Log.Information($"Sync delete received: {relativePath}");
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

        Log.Information($"Sync dir created received: {relativePath}");
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

        Log.Information($"Sync dir deleted received: {relativePath}");
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

        Log.Information($"Sync rename received: {oldRelativePath} -> {newRelativePath}");
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

        Log.Information($"Sync file received (compressed): {relativePath} ({compressedSize} -> {fileSize} bytes)");
        
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

        Log.Information($"Sync manifest received: {manifest.Count} files");
        SyncManifestReceived?.Invoke(manifest, remotePeer);
    }

    private void HandleSyncFileRequest(BinaryReader reader, Peer remotePeer)
    {
        var relativePath = reader.ReadString();

        Log.Information($"Sync file requested: {relativePath}");
        SyncFileRequested?.Invoke(relativePath, remotePeer);
    }

    #region Delta Sync Handlers

    private Task HandleRequestSignatures(BinaryReader reader, Peer remotePeer)
    {
        var relativePath = reader.ReadString();
        Log.Debug($"Signature request received for: {relativePath}");
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

        Log.Debug($"Block signatures received for {relativePath}: {signatureCount} blocks");
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

        Log.Debug($"Delta data received for {relativePath}: {instructionCount} instructions");
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
            Log.Error(ex, $"Send error: {ex.Message}");
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
            Log.Warning(ex, $"Error scanning for existing files: {ex.Message}");
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

        Log.Information($"Starting parallel transfer of {fileList.Count} files to {peer.Name}");

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
                    Log.Debug($"Parallel transfer [{completedCount}/{fileList.Count}]: {syncFile.RelativePath}");
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

        Log.Information($"Completed parallel transfer of {fileList.Count} files to {peer.Name}");
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
            Log.Error(ex, $"Parallel send error for {syncFile.RelativePath}: {ex.Message}");
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
                
                Log.Debug($"Compression: {syncFile.RelativePath} - {fileInfo.Length} -> {compressedData.Length} bytes ({compressionRatio:F1}% saved)");

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

                Log.Information($"Sync sent (compressed): {syncFile.RelativePath} to {peer.Name}");
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
            
            Log.Error(ex, $"Sync send error: {ex.Message}");
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

                Log.Information($"Sync delete sent: {syncFile.RelativePath} to {peer.Name}");
            }
            finally
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {

            Log.Error(ex, $"Sync delete error: {ex.Message}");
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

                Log.Information($"Sync rename sent: {syncFile.OldRelativePath} -> {syncFile.RelativePath} to {peer.Name}");
            }
            finally
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {

            Log.Error(ex, $"Sync rename error: {ex.Message}");
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

                Log.Information($"Sync dir sent: {syncFile.RelativePath} to {peer.Name}");
            }
            finally 
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {

            Log.Error(ex, $"Sync dir error: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends the full sync manifest to a peer.
    /// </summary>
    public async Task SendSyncManifest(Peer peer, IEnumerable<SyncedFile> manifest, CancellationToken ct = default)
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

                Log.Information($"Sync manifest sent to {peer.Name}: {manifest.Count()} files");
            }
            finally
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {

            Log.Error(ex, $"Sync manifest error: {ex.Message}");
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

                Log.Information($"Sync file requested: {relativePath} from {peer.Name}");
            }
            finally
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {

            Log.Error(ex, $"Sync request error: {ex.Message}");
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

                Log.Debug($"Requested signatures for: {relativePath} from {peer.Name}");
            }
            finally
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {

            Log.Error(ex, $"Request signatures error: {ex.Message}");
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

                Log.Debug($"Sent {signatures.Count} block signatures for: {relativePath} to {peer.Name}");
            }
            finally
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {

            Log.Error(ex, $"Send signatures error: {ex.Message}");
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
                Log.Debug($"Sent delta for {syncFile.RelativePath} to {peer.Name}: {instructions.Count} instructions, ~{FileHelpers.FormatBytes(deltaSize)}");
            }
            finally
            {
                connection.Lock.Release();
            }
        }
        catch (Exception ex)
        {

            Log.Error(ex, $"Send delta error: {ex.Message}");
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
        
        _connectionManager.Dispose();
    }
}


