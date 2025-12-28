namespace Swarm.Core.Abstractions;

/// <summary>
/// Platform-agnostic interface for UI thread marshaling.
/// Replaces WPF's Dispatcher.Invoke for cross-platform compatibility.
/// </summary>
public interface IDispatcher
{
    /// <summary>
    /// Invokes an action on the UI thread synchronously.
    /// </summary>
    void Invoke(Action action);

    /// <summary>
    /// Invokes an action on the UI thread asynchronously.
    /// </summary>
    Task InvokeAsync(Action action);

    /// <summary>
    /// Checks if the current thread is the UI thread.
    /// </summary>
    bool CheckAccess();
}
