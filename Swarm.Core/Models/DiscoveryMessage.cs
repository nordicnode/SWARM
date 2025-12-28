using System.Text.Json.Serialization;

namespace Swarm.Core.Models;

/// <summary>
/// Structured JSON message for peer discovery protocol.
/// Provides extensibility for future protocol enhancements.
/// </summary>
public class DiscoveryMessage
{
    /// <summary>
    /// Protocol identifier (always "SWARM").
    /// </summary>
    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "SWARM";

    /// <summary>
    /// Protocol version for compatibility checking.
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.1";

    /// <summary>
    /// Unique identifier for this peer.
    /// </summary>
    [JsonPropertyName("peerId")]
    public string PeerId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name of the peer.
    /// </summary>
    [JsonPropertyName("peerName")]
    public string PeerName { get; set; } = string.Empty;

    /// <summary>
    /// TCP port for file transfers.
    /// </summary>
    [JsonPropertyName("transferPort")]
    public int TransferPort { get; set; }

    /// <summary>
    /// Whether folder synchronization is enabled on this peer.
    /// </summary>
    [JsonPropertyName("syncEnabled")]
    public bool SyncEnabled { get; set; }

    /// <summary>
    /// Optional device type for UI display (e.g., "desktop", "laptop", "server").
    /// </summary>
    [JsonPropertyName("deviceType")]
    public string? DeviceType { get; set; }

    /// <summary>
    /// Optional timestamp for message freshness validation.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; set; }

    /// <summary>
    /// The sender's public key (Base64 encoded) for identity verification.
    /// </summary>
    [JsonPropertyName("publicKey")]
    public string? PublicKey { get; set; }

    /// <summary>
    /// Signature of the message payload (Base64 encoded).
    /// Signs: PeerId + PeerName + TransferPort + Timestamp
    /// </summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    /// <summary>
    /// Gets the signable payload for this message.
    /// </summary>
    public string GetSignablePayload()
    {
        return $"{PeerId}|{PeerName}|{TransferPort}|{Timestamp}";
    }
}

