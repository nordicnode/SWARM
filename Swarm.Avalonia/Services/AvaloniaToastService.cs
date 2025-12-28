using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using Swarm.Core.Abstractions;

namespace Swarm.Avalonia.Services;

/// <summary>
/// Avalonia implementation of IToastService using WindowNotificationManager.
/// </summary>
public class AvaloniaToastService : IToastService
{
    private WindowNotificationManager? _notificationManager;

    public void SetNotificationManager(Window hostWindow)
    {
        _notificationManager = new WindowNotificationManager(hostWindow)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = 3
        };
    }

    public void ShowToast(string title, string message, ToastType type = ToastType.Info)
    {
        if (_notificationManager == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            var notificationType = type switch
            {
                ToastType.Success => NotificationType.Success,
                ToastType.Warning => NotificationType.Warning,
                ToastType.Error => NotificationType.Error,
                _ => NotificationType.Information
            };

            _notificationManager.Show(new Notification(title, message, notificationType));
        });
    }

    /// <summary>
    /// Convenience method that directly accepts Avalonia's NotificationType.
    /// </summary>
    public void Show(string title, string message, NotificationType type)
    {
        if (_notificationManager == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            _notificationManager.Show(new Notification(title, message, type));
        });
    }
}
