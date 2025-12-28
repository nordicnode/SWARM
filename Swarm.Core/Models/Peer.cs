namespace Swarm.Core.Models;

/// <summary>
/// Represents a discovered peer on the LAN.
/// </summary>
public class Peer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = Environment.MachineName;
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.Now;
    public bool IsOnline => (DateTime.Now - LastSeen).TotalSeconds < 10;
    
    /// <summary>
    /// Whether this peer has folder synchronization enabled.
    /// </summary>
    public bool IsSyncEnabled { get; set; }
    
    /// <summary>
    /// Hash of the peer's sync folder state for quick comparison.
    /// </summary>
    public string? SyncFolderHash { get; set; }

    /// <summary>
    /// The peer's public key (Base64 encoded) for signature verification.
    /// </summary>
    public string? PublicKeyBase64 { get; set; }

    /// <summary>
    /// Whether this peer's identity has been verified/trusted.
    /// </summary>
    public bool IsTrusted { get; set; }

    /// <summary>
    /// Gets the peer's fingerprint (short form) for display.
    /// </summary>
    public string? Fingerprint => string.IsNullOrEmpty(PublicKeyBase64) 
        ? null 
        : Swarm.Core.Services.CryptoService.ComputeShortFingerprint(Convert.FromBase64String(PublicKeyBase64));


    public override bool Equals(object? obj)
    {
        if (obj is Peer other)
            return Id == other.Id;
        return false;
    }

    public override int GetHashCode() => Id.GetHashCode();

    public override string ToString() => $"{Name} ({IpAddress})";
}

