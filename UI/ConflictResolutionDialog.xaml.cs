using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Swarm.Core.Helpers;

namespace Swarm.UI;

/// <summary>
/// Dialog for resolving file conflicts during sync.
/// </summary>
public partial class ConflictResolutionDialog : Window
{
    private readonly FileConflict _conflict;
    
    /// <summary>
    /// The user's resolution choice after dialog closes.
    /// </summary>
    public ConflictChoice? Result { get; private set; }

    public ConflictResolutionDialog(FileConflict conflict)
    {
        InitializeComponent();
        _conflict = conflict;
        
        LoadConflictDetails();
    }

    private void LoadConflictDetails()
    {
        FilePathText.Text = _conflict.RelativePath;
        
        // Local version
        LocalModifiedText.Text = _conflict.LocalModified.ToLocalTime().ToString("MMM d, yyyy h:mm tt");
        LocalSizeText.Text = FormatFileSize(_conflict.LocalSize);
        LocalHashText.Text = _conflict.LocalHash.Length > 16 
            ? _conflict.LocalHash[..16] + "..." 
            : _conflict.LocalHash;
        
        // Remote version
        RemotePeerText.Text = _conflict.SourcePeerName;
        RemoteModifiedText.Text = _conflict.RemoteModified.ToLocalTime().ToString("MMM d, yyyy h:mm tt");
        RemoteSizeText.Text = FormatFileSize(_conflict.RemoteSize);
        RemoteHashText.Text = _conflict.RemoteHash.Length > 16 
            ? _conflict.RemoteHash[..16] + "..." 
            : _conflict.RemoteHash;
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= 1024 * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} bytes";
    }

    private void KeepLocal_Click(object sender, RoutedEventArgs e)
    {
        Result = ConflictChoice.KeepLocal;
        DialogResult = true;
        Close();
    }

    private void KeepRemote_Click(object sender, RoutedEventArgs e)
    {
        Result = ConflictChoice.KeepRemote;
        DialogResult = true;
        Close();
    }

    private void KeepBoth_Click(object sender, RoutedEventArgs e)
    {
        Result = ConflictChoice.KeepBoth;
        DialogResult = true;
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Result = ConflictChoice.Skip;
        DialogResult = false;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }
}
