using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Swarm.Avalonia.Dialogs;

/// <summary>
/// Result of the pause sync dialog.
/// </summary>
public class PauseSyncResult
{
    /// <summary>
    /// Duration to pause in minutes. 0 means indefinite (until manual resume).
    /// </summary>
    public int DurationMinutes { get; set; }
    
    /// <summary>
    /// Optional reason/note for the pause.
    /// </summary>
    public string? Reason { get; set; }
}

public partial class PauseSyncDialog : Window
{
    public PauseSyncDialog()
    {
        InitializeComponent();
    }

    private void PauseButton_Click(object? sender, RoutedEventArgs e)
    {
        var selectedItem = DurationComboBox.SelectedItem as ComboBoxItem;
        var durationMinutes = 30; // Default
        
        if (selectedItem?.Tag is string tagValue && int.TryParse(tagValue, out var parsed))
        {
            durationMinutes = parsed;
        }
        
        var reason = string.IsNullOrWhiteSpace(ReasonTextBox.Text) ? null : ReasonTextBox.Text.Trim();
        
        Close(new PauseSyncResult
        {
            DurationMinutes = durationMinutes,
            Reason = reason
        });
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
