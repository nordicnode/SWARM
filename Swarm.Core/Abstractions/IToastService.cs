namespace Swarm.Core.Abstractions;

/// <summary>
/// Platform-agnostic interface for displaying toast/notification messages.
/// </summary>
public interface IToastService
{
    /// <summary>
    /// Shows a toast notification to the user.
    /// </summary>
    void ShowToast(string title, string message, ToastType type = ToastType.Info);
}

/// <summary>
/// Types of toast notifications.
/// </summary>
public enum ToastType
{
    Info,
    Success,
    Warning,
    Error
}
