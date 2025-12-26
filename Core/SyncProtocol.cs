namespace Swarm.Core;

/// <summary>
/// Protocol constants for sync communication.
/// </summary>
public static class SyncProtocol
{
    /// <summary>
    /// Protocol header for sync messages.
    /// </summary>
    public const string SYNC_HEADER = "SWARM_SYNC:1.0";

    /// <summary>
    /// Message type: Full manifest of all files in sync folder.
    /// </summary>
    public const byte MSG_SYNC_MANIFEST = 0x01;

    /// <summary>
    /// Message type: Single file was created or modified.
    /// </summary>
    public const byte MSG_FILE_CHANGED = 0x02;

    /// <summary>
    /// Message type: File was deleted.
    /// </summary>
    public const byte MSG_FILE_DELETED = 0x03;

    /// <summary>
    /// Message type: Request file content from peer.
    /// </summary>
    public const byte MSG_REQUEST_FILE = 0x04;

    /// <summary>
    /// Message type: File content response.
    /// </summary>
    public const byte MSG_FILE_DATA = 0x05;

    /// <summary>
    /// Message type: Directory was created.
    /// </summary>
    public const byte MSG_DIR_CREATED = 0x06;

    /// <summary>
    /// Message type: Directory was deleted.
    /// </summary>
    public const byte MSG_DIR_DELETED = 0x07;

    /// <summary>
    /// Message type: File was renamed.
    /// </summary>
    public const byte MSG_FILE_RENAMED = 0x08;

    /// <summary>
    /// Default port for sync-specific communication.
    /// </summary>
    public const int SYNC_PORT_OFFSET = 1;
}
