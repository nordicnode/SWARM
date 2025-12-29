using System.Net;
using System.Net.Sockets;
using System.Text;
using Swarm.Core.Models;

namespace Swarm.Core.Services;

/// <summary>
/// Represents a TCP connection to a peer with stream management and encryption support.
/// </summary>
public class PeerConnection : IDisposable
{
    public TcpClient Client { get; }
    public NetworkStream NetworkStream { get; }
    public Stream Stream { get; private set; }
    public BinaryWriter Writer { get; private set; }
    public BinaryReader Reader { get; private set; }
    public SemaphoreSlim Lock { get; } = new(1, 1);
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public byte[]? SessionKey { get; private set; }
    public bool IsEncrypted => SessionKey != null;

    /// <summary>
    /// Measured round-trip time in milliseconds. -1 if not measured.
    /// </summary>
    public int RttMs { get; set; } = -1;

    /// <summary>
    /// Gets the optimal buffer size based on measured RTT.
    /// </summary>
    public int GetOptimalBufferSize()
    {
        if (RttMs < 0)
        {
            // Not measured yet - use default
            return ProtocolConstants.DEFAULT_BUFFER_SIZE;
        }

        if (RttMs < ProtocolConstants.FAST_LAN_RTT_MS)
        {
            // Fast LAN - use maximum buffer
            return ProtocolConstants.MAX_BUFFER_SIZE;
        }
        else if (RttMs > ProtocolConstants.SLOW_LINK_RTT_MS)
        {
            // Slow link - use minimum buffer for responsiveness
            return ProtocolConstants.MIN_BUFFER_SIZE;
        }
        else
        {
            // Medium speed - use default
            return ProtocolConstants.DEFAULT_BUFFER_SIZE;
        }
    }


    public PeerConnection(TcpClient client)
    {
        Client = client;
        NetworkStream = client.GetStream();
        Stream = NetworkStream;
        Writer = new BinaryWriter(Stream, Encoding.UTF8, leaveOpen: true);
        Reader = new BinaryReader(Stream, Encoding.UTF8, leaveOpen: true);
    }

    /// <summary>
    /// Upgrades the connection to use encryption with the given session key.
    /// </summary>
    public void EnableEncryption(byte[] sessionKey)
    {
        SessionKey = sessionKey;
        var secureStream = new SecureStream(NetworkStream, sessionKey);
        Stream = secureStream;
        Writer = new BinaryWriter(Stream, Encoding.UTF8, leaveOpen: true);
        Reader = new BinaryReader(Stream, Encoding.UTF8, leaveOpen: true);
    }

    public bool IsConnected => Client.Connected;

    /// <summary>
    /// Performs an active health check on the connection to detect half-open TCP connections.
    /// </summary>
    public bool IsHealthy()
    {
        if (!Client.Connected) return false;

        try
        {
            // Check if the socket is still connected by polling
            var socket = Client.Client;
            if (socket == null) return false;

            // Poll with a zero timeout to check if connection is still valid
            // SelectRead returns true if: data available, connection closed, or error
            // If no data and connection is good, Poll returns false
            bool readable = socket.Poll(0, SelectMode.SelectRead);
            bool hasData = socket.Available > 0;
            
            // If readable but no data, the connection was closed by the remote side
            if (readable && !hasData)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { Lock.Dispose(); } catch { }
        try { Writer.Dispose(); } catch { }
        try { Reader.Dispose(); } catch { }
        try { Client.Dispose(); } catch { }
    }
}
