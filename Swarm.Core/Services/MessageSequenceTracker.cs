using System.Collections.Concurrent;

namespace Swarm.Core.Services;

/// <summary>
/// Tracks message sequence numbers per connection to prevent replay attacks.
/// Each connection has an expected next sequence number, and messages that are
/// duplicates or out-of-order are rejected.
/// </summary>
public class MessageSequenceTracker
{
    private readonly ConcurrentDictionary<string, SequenceState> _sequences = new();
    
    // Maximum gap allowed for out-of-order messages (sliding window)
    private const int MaxSequenceGap = 100;
    
    // How many old sequences to track to detect duplicates
    private const int WindowSize = 1000;

    /// <summary>
    /// Initializes sequence tracking for a new connection.
    /// </summary>
    public void InitializeConnection(string connectionId)
    {
        _sequences[connectionId] = new SequenceState();
    }

    /// <summary>
    /// Validates an incoming message sequence number.
    /// Returns true if valid (not a replay), false if duplicate or replay detected.
    /// </summary>
    public bool ValidateSequence(string connectionId, long sequenceNumber)
    {
        if (!_sequences.TryGetValue(connectionId, out var state))
        {
            // Connection not tracked - could be allowed or rejected based on policy
            return true;
        }

        lock (state)
        {
            // Check if this is a duplicate
            if (state.ReceivedSequences.Contains(sequenceNumber))
            {
                return false; // Duplicate detected
            }

            // Check if sequence is too old (replay attack or packet loss)
            if (sequenceNumber < state.MinAcceptedSequence)
            {
                return false; // Too old, likely replay
            }

            // Check if sequence is too far ahead (possible attack or lost messages)
            if (sequenceNumber > state.ExpectedNextSequence + MaxSequenceGap)
            {
                return false; // Too far ahead
            }

            // Accept this sequence
            state.ReceivedSequences.Add(sequenceNumber);
            
            // Update expected next sequence
            if (sequenceNumber >= state.ExpectedNextSequence)
            {
                state.ExpectedNextSequence = sequenceNumber + 1;
            }

            // Slide window - remove old sequences to prevent unbounded growth
            while (state.ReceivedSequences.Count > WindowSize)
            {
                state.MinAcceptedSequence++;
                state.ReceivedSequences.Remove(state.MinAcceptedSequence - 1);
            }

            return true;
        }
    }

    /// <summary>
    /// Gets the next sequence number to use for an outgoing message.
    /// </summary>
    public long GetNextOutgoingSequence(string connectionId)
    {
        if (!_sequences.TryGetValue(connectionId, out var state))
        {
            InitializeConnection(connectionId);
            state = _sequences[connectionId];
        }

        lock (state)
        {
            return state.OutgoingSequence++;
        }
    }

    /// <summary>
    /// Clears tracking for a connection (call on disconnect).
    /// </summary>
    public void ClearConnection(string connectionId)
    {
        _sequences.TryRemove(connectionId, out _);
    }

    /// <summary>
    /// Clears all connection tracking.
    /// </summary>
    public void Clear()
    {
        _sequences.Clear();
    }

    /// <summary>
    /// Gets the number of active connections being tracked.
    /// </summary>
    public int ActiveConnectionCount => _sequences.Count;

    private class SequenceState
    {
        public long ExpectedNextSequence { get; set; } = 0;
        public long MinAcceptedSequence { get; set; } = 0;
        public long OutgoingSequence { get; set; } = 0;
        public HashSet<long> ReceivedSequences { get; } = new();
    }
}
