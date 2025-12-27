namespace Swarm.Core;

/// <summary>
/// Centralized protocol constants and magic numbers used for networking and synchronization.
/// </summary>
public static class ProtocolConstants
{
    // --- Networking ---
    
    /// <summary>
    /// UDP port used for peer discovery.
    /// </summary>
    public const int DISCOVERY_PORT = 37420;

    /// <summary>
    /// Default port for sync-specific communication (offset from discovery port).
    /// </summary>
    public const int SYNC_PORT_OFFSET = 1;

    /// <summary>
    /// Interval between discovery broadcasts in milliseconds.
    /// </summary>
    public const int BROADCAST_INTERVAL_MS = 3000;

    /// <summary>
    /// Frequency of the peer cleanup cycle in milliseconds.
    /// </summary>
    public const int CLEANUP_INTERVAL_MS = 5000;

    /// <summary>
    /// Time in seconds before a peer is considered stale and removed.
    /// </summary>
    public const int PEER_TIMEOUT_SECONDS = 15;


    // --- Buffers and Streams ---

    /// <summary>
    /// Standard buffer size for network transfers (1MB).
    /// </summary>
    public const int DEFAULT_BUFFER_SIZE = 1024 * 1024;

    /// <summary>
    /// Buffer size used for file stream operations (80KB).
    /// </summary>
    public const int FILE_STREAM_BUFFER_SIZE = 81920;


    // --- Protocol Headers ---

    /// <summary>
    /// Header for peer discovery messages (legacy format).
    /// </summary>
    public const string DISCOVERY_HEADER = "SWARM:1.0";

    /// <summary>
    /// Current discovery protocol version for JSON messages.
    /// </summary>
    public const string DISCOVERY_PROTOCOL_VERSION = "1.1";

    /// <summary>
    /// Protocol identifier for discovery messages.
    /// </summary>
    public const string DISCOVERY_PROTOCOL_ID = "SWARM";


    // --- TCP Connection Robustness ---

    /// <summary>
    /// Timeout for establishing TCP connections in milliseconds.
    /// </summary>
    public const int CONNECTION_TIMEOUT_MS = 5000;

    /// <summary>
    /// Maximum number of retry attempts for failed connections.
    /// </summary>
    public const int MAX_RETRY_ATTEMPTS = 3;

    /// <summary>
    /// Base delay between retry attempts in milliseconds (exponential backoff applied).
    /// </summary>
    public const int RETRY_BASE_DELAY_MS = 1000;

    /// <summary>
    /// Interval for TCP keepalive probes in milliseconds.
    /// </summary>
    public const int TCP_KEEPALIVE_INTERVAL_MS = 30000;

    /// <summary>
    /// Time to wait before sending the first keepalive probe in milliseconds.
    /// </summary>
    public const int TCP_KEEPALIVE_TIME_MS = 60000;

    /// <summary>
    /// Number of keepalive retries before considering connection dead.
    /// </summary>
    public const int TCP_KEEPALIVE_RETRIES = 3;

    /// <summary>
    /// Header for file transfer messages.
    /// </summary>
    public const string TRANSFER_HEADER = "SWARM_TRANSFER:1.0";

    /// <summary>
    /// Header for synchronization messages.
    /// </summary>
    public const string SYNC_HEADER = "SWARM_SYNC:1.0";


    // --- Synchronization ---

    /// <summary>
    /// Debounce time for file change events in milliseconds.
    /// </summary>
    public const int SYNC_DEBOUNCE_MS = 500;

    /// <summary>
    /// Duration in milliseconds to ignore local file changes after writing them during a sync.
    /// </summary>
    public const int SYNC_IGNORE_DURATION_MS = 5000;


    // --- Sync Message Types ---

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


    // --- Security ---

    /// <summary>
    /// Discovery protocol header version 2 (with signatures).
    /// </summary>
    public const string DISCOVERY_HEADER_V2 = "SWARM:2.0";

    /// <summary>
    /// Header for secure handshake messages.
    /// </summary>
    public const string SECURE_HANDSHAKE_HEADER = "SWARM_SECURE:1.0";

    /// <summary>
    /// AES-GCM nonce size in bytes.
    /// </summary>
    public const int NONCE_SIZE = 12;

    /// <summary>
    /// AES-GCM authentication tag size in bytes.
    /// </summary>
    public const int TAG_SIZE = 16;

    /// <summary>
    /// AES-256 session key size in bytes.
    /// </summary>
    public const int SESSION_KEY_SIZE = 32;

    /// <summary>
    /// Maximum size of an encrypted chunk (buffer + overhead).
    /// </summary>
    public const int MAX_ENCRYPTED_CHUNK_SIZE = DEFAULT_BUFFER_SIZE + TAG_SIZE + NONCE_SIZE + 1024;
}
