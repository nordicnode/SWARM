using System.Windows;

namespace Swarm.UI;

/// <summary>
/// Splash screen shown during application startup
/// </summary>
public partial class StartupSplash : Window
{
    public StartupSplash()
    {
        InitializeComponent();
    }
    
    /// <summary>
    /// Updates the loading status text
    /// </summary>
    public void SetStatus(string status)
    {
        StatusText.Text = status;
    }
}
