using Swarm.Core.Models;

namespace Swarm.Core.Abstractions;

/// <summary>
/// Repository interface for file state persistence.
/// Abstracts storage implementation to improve testability and enable future backends.
/// </summary>
public interface IFileStateRepository
{
    /// <summary>
    /// Gets a file state by its relative path.
    /// </summary>
    SyncedFile? Get(string relativePath);

    /// <summary>
    /// Gets all tracked file states.
    /// </summary>
    IReadOnlyList<SyncedFile> GetAll();

    /// <summary>
    /// Adds or updates a file state.
    /// </summary>
    void AddOrUpdate(SyncedFile file);

    /// <summary>
    /// Removes a file state by its relative path.
    /// </summary>
    bool Remove(string relativePath);

    /// <summary>
    /// Checks if a file state exists.
    /// </summary>
    bool Exists(string relativePath);

    /// <summary>
    /// Gets the count of tracked files.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Clears all file states.
    /// </summary>
    void Clear();

    /// <summary>
    /// Persists current state to storage.
    /// </summary>
    void SaveChanges();

    /// <summary>
    /// Loads state from storage.
    /// </summary>
    void Load();

    /// <summary>
    /// Gets file states as a read-only dictionary (for manifest generation).
    /// </summary>
    IReadOnlyDictionary<string, SyncedFile> AsReadOnlyDictionary();
}
