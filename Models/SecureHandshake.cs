namespace Swarm.Models;

/// <summary>
/// Represents the handshake data for establishing a secure connection.
/// </summary>
public record SecureHandshake
{
    /// <summary>
    /// The peer's unique identifier.
    /// </summary>
    public required string PeerId { get; init; }

    /// <summary>
    /// The peer's device name.
    /// </summary>
    public required string PeerName { get; init; }

    /// <summary>
    /// The ephemeral ECDH public key for this session.
    /// </summary>
    public required byte[] EphemeralPublicKey { get; init; }

    /// <summary>
    /// The peer's identity public key (for verification).
    /// </summary>
    public required byte[] IdentityPublicKey { get; init; }

    /// <summary>
    /// Signature of (PeerId || EphemeralPublicKey) using the identity key.
    /// </summary>
    public required byte[] Signature { get; init; }
}
