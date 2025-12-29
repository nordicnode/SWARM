using Serilog;

namespace Swarm.Core.Helpers;

/// <summary>
/// Extension methods for safe fire-and-forget async operations.
/// </summary>
public static class TaskExtensions
{
    /// <summary>
    /// Safely executes a task without awaiting, logging any exceptions.
    /// Use this instead of discarding tasks with _ = Task.Run(...).
    /// </summary>
    public static async void SafeFireAndForget(this Task task, string context = "")
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellations - they're expected during shutdown
        }
        catch (Exception ex)
        {
            var contextMsg = string.IsNullOrEmpty(context) ? "" : $" [{context}]";
            Log.Error(ex, $"Fire-and-forget error{contextMsg}: {ex.Message}");
        }
    }

    /// <summary>
    /// Safely executes an async action, logging any exceptions.
    /// Use this for event handlers that would otherwise be async void.
    /// </summary>
    public static async void SafeExecute(Func<Task> action, string context = "")
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellations
        }
        catch (Exception ex)
        {
            var contextMsg = string.IsNullOrEmpty(context) ? "" : $" [{context}]";
            Log.Error(ex, $"Async handler error{contextMsg}: {ex.Message}");
        }
    }
}
