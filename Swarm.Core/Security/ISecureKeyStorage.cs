namespace Swarm.Core.Security;

/// <summary>
/// Platform abstraction for secure key storage.
/// Implementations use platform-specific credential stores (DPAPI, Keychain, etc.)
/// </summary>
public interface ISecureKeyStorage
{
    /// <summary>
    /// Stores key data securely using platform-specific protection.
    /// </summary>
    /// <param name="keyName">Unique identifier for the key</param>
    /// <param name="keyData">Raw key bytes to protect</param>
    void StoreKey(string keyName, byte[] keyData);

    /// <summary>
    /// Retrieves key data from secure storage.
    /// </summary>
    /// <param name="keyName">Unique identifier for the key</param>
    /// <returns>Raw key bytes, or null if not found</returns>
    byte[]? RetrieveKey(string keyName);

    /// <summary>
    /// Checks if a key exists in secure storage.
    /// </summary>
    bool KeyExists(string keyName);

    /// <summary>
    /// Deletes a key from secure storage.
    /// </summary>
    void DeleteKey(string keyName);
}
