using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Swarm.Avalonia.Dialogs;

/// <summary>
/// A reusable confirmation dialog for destructive operations.
/// </summary>
public partial class ConfirmationDialog : Window
{
    public ConfirmationDialog()
    {
        InitializeComponent();
        
        CancelButton.Click += OnCancel;
        ConfirmButton.Click += OnConfirm;
    }

    /// <summary>
    /// Sets the dialog title.
    /// </summary>
    public void SetTitle(string title)
    {
        TitleText.Text = title;
        Title = title;
    }

    /// <summary>
    /// Sets the dialog message.
    /// </summary>
    public void SetMessage(string message)
    {
        MessageText.Text = message;
    }

    /// <summary>
    /// Sets the confirm button text and optionally makes it destructive (red).
    /// </summary>
    public void SetConfirmButton(string text, bool isDestructive = false)
    {
        ConfirmButton.Content = text;
        if (isDestructive)
        {
            ConfirmButton.Classes.Remove("primary");
            ConfirmButton.Classes.Add("destructive");
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }
}
