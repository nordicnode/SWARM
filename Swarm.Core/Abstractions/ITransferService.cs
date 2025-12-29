using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Swarm.Core.Models;

namespace Swarm.Core.Abstractions;

public interface ITransferService : IDisposable
{
    int ListenPort { get; }
    ObservableCollection<FileTransfer> Transfers { get; }

    event Action<FileTransfer>? TransferStarted;
    event Action<FileTransfer>? TransferProgress;
    event Action<FileTransfer>? TransferCompleted;
    event Action<string, string, long, Action<bool>>? IncomingFileRequest;
    
    event Action<SyncedFile, Stream, Peer>? SyncFileReceived;
    event Action<SyncedFile, Peer>? SyncDeleteReceived;
    event Action<SyncedFile, Peer>? SyncRenameReceived;
    event Action<List<SyncedFile>, Peer>? SyncManifestReceived;
    event Action<string, Peer>? SyncFileRequested;
    
    event Action<string, Peer>? SignaturesRequested;
    event Action<string, List<BlockSignature>, Peer>? BlockSignaturesReceived;
    event Action<SyncedFile, List<DeltaInstruction>, Peer>? DeltaDataReceived;

    void Start();
    void SetDownloadPath(string path);
    
    Task SendFile(Peer peer, string filePath, CancellationToken ct = default);
    
    Task SendSyncManifest(Peer peer, IEnumerable<SyncedFile> files, CancellationToken ct = default);
    Task SendSyncFile(Peer peer, string filePath, SyncedFile syncFile, Action<long, long>? progressCallback = null, CancellationToken ct = default);
    Task SendSyncFilesParallel(Peer peer, IEnumerable<(string filePath, SyncedFile syncFile)> files, Action<string, long, long>? progressCallback = null, CancellationToken ct = default);
    Task RequestSyncFile(Peer peer, string relativePath, CancellationToken ct = default);
    
    Task SendSyncDelete(Peer peer, SyncedFile syncFile, CancellationToken ct = default);
    Task SendSyncRename(Peer peer, SyncedFile syncFile, CancellationToken ct = default);
    Task SendSyncDirectory(Peer peer, SyncedFile syncFile, CancellationToken ct = default);
    
    Task RequestBlockSignatures(Peer peer, string relativePath, CancellationToken ct = default);
    Task SendBlockSignatures(Peer peer, string relativePath, List<BlockSignature> signatures, CancellationToken ct = default);
    Task SendDeltaData(Peer peer, SyncedFile syncFile, List<DeltaInstruction> instructions, CancellationToken ct = default);
}
