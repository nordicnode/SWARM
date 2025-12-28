using System.Diagnostics;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Swarm.Core.Abstractions;

namespace Swarm.Services;

/// <summary>
/// Service for displaying native Windows toast notifications.
/// </summary>
public class WpfToastService : IToastService
{
    private readonly string _appName = "Swarm";
    
    public void ShowToast(string title, string message, Swarm.Core.Abstractions.ToastType type = Swarm.Core.Abstractions.ToastType.Info)
    {
        try
        {
            var toastXml = $@"
<toast>
    <visual>
        <binding template='ToastGeneric'>
            <text>{EscapeXml(title)}</text>
            <text>{EscapeXml(message)}</text>
        </binding>
    </visual>
</toast>";

            ShowToastXml(toastXml);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WpfToastService] Failed to show notification: {ex.Message}");
        }
    }

    private void ShowToastXml(string xml)
    {
        var xmlDoc = new XmlDocument();
        xmlDoc.LoadXml(xml);

        var toast = new ToastNotification(xmlDoc);
        var notifier = ToastNotificationManager.CreateToastNotifier(_appName);
        notifier.Show(toast);
    }

    private static string EscapeXml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}

