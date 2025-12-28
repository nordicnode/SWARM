using Avalonia.Controls;
using Avalonia.Interactivity;
using Swarm.Core.Helpers;

namespace Swarm.Avalonia.Views;

/// <summary>
/// Dialog for accepting or rejecting incoming file transfers.
/// </summary>
public partial class FileTransferDialog : Window
{
    public FileTransferDialog()
    {
        InitializeComponent();
    }

    public FileTransferDialog(string fileName, string senderName, long fileSize) : this()
    {
        SenderText.Text = $"From: {senderName}";
        FileNameText.Text = fileName;
        FileSizeText.Text = FileHelpers.FormatBytes(fileSize);

        AcceptButton.Click += OnAcceptClick;
        RejectButton.Click += OnRejectClick;
    }

    private void OnAcceptClick(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void OnRejectClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
