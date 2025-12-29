using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swarm.Core.Models;

namespace Swarm.Core.Services;

/// <summary>
/// Handles secure ECDH handshake protocol for establishing encrypted connections.
/// Extracted from TransferService for better separation of concerns.
/// </summary>
public class SecureHandshakeHandler
{
    private readonly Settings _settings;
    private readonly CryptoService _cryptoService;
    private readonly ILogger<SecureHandshakeHandler> _logger;

    public SecureHandshakeHandler(Settings settings, CryptoService cryptoService, ILogger<SecureHandshakeHandler>? logger = null)
    {
        _settings = settings;
        _cryptoService = cryptoService;
        _logger = logger ?? NullLogger<SecureHandshakeHandler>.Instance;
    }

    /// <summary>
    /// Performs a secure handshake as the client (initiator).
    /// Exchanges ephemeral ECDH keys to derive a session key.
    /// </summary>
    public async Task PerformHandshakeAsClient(PeerConnection connection, Peer peer, CancellationToken ct)
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
    /// <returns>True if handshake succeeded; false otherwise.</returns>
    public async Task<HandshakeResult> HandleHandshakeAsServer(BinaryReader reader, BinaryWriter writer, NetworkStream stream, TcpClient client)
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
            if (!_cryptoService.Verify(signableData, signature, clientIdentityKey))
            {
                _logger.LogWarning("Signature verification failed during handshake from {PeerId}", peerId);
                writer.Write(ProtocolConstants.HANDSHAKE_FAILED_PREFIX + "INVALID_SIGNATURE");
                return HandshakeResult.Failed("Invalid signature");
            }
            
            // Check if peer is trusted (optional - can still proceed but warn)
            var clientKeyBase64 = Convert.ToBase64String(clientIdentityKey);
            var isTrusted = _settings.TrustedPeerPublicKeys.TryGetValue(peerId, out var storedKey) && storedKey == clientKeyBase64;
            
            if (!isTrusted)
            {
                _logger.LogWarning($"Handshake from untrusted peer: {peerName} ({peerId})");
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
            
            _logger.LogDebug($"Secure handshake completed as server with {peerName}");
            
            return HandshakeResult.Success(peerId, peerName, isTrusted, sessionKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Handshake error: {ex.Message}");
            try { writer.Write($"{ProtocolConstants.HANDSHAKE_FAILED_PREFIX}GENERIC_ERROR"); } catch { }
            return HandshakeResult.Failed(ex.Message);
        }
    }
}

/// <summary>
/// Result of a handshake operation.
/// </summary>
public class HandshakeResult
{
    public bool Succeeded { get; private set; }
    public string? PeerId { get; private set; }
    public string? PeerName { get; private set; }
    public bool IsTrusted { get; private set; }
    public byte[]? SessionKey { get; private set; }
    public string? ErrorMessage { get; private set; }

    public static HandshakeResult Success(string peerId, string peerName, bool isTrusted, byte[] sessionKey)
    {
        return new HandshakeResult
        {
            Succeeded = true,
            PeerId = peerId,
            PeerName = peerName,
            IsTrusted = isTrusted,
            SessionKey = sessionKey
        };
    }

    public static HandshakeResult Failed(string errorMessage)
    {
        return new HandshakeResult
        {
            Succeeded = false,
            ErrorMessage = errorMessage
        };
    }
}
