using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Swarm.Core;

namespace Swarm.UI;

/// <summary>
/// Settings dialog for configuring Swarm application settings.
/// </summary>
public partial class SettingsDialog : Window
{
    private readonly Settings _settings;
    private readonly Action<Settings>? _onSettingsChanged;

    public SettingsDialog(Settings settings, Action<Settings>? onSettingsChanged = null)
    {
        InitializeComponent();
        
        _settings = settings;
        _onSettingsChanged = onSettingsChanged;
        
        LoadSettings();
    }

    private void LoadSettings()
    {
        // General
        DeviceNameTextBox.Text = _settings.DeviceName;
        StartMinimizedToggle.IsChecked = _settings.StartMinimized;
        
        // Transfers
        DownloadPathText.Text = ShortenPath(_settings.DownloadPath);
        DownloadPathText.ToolTip = _settings.DownloadPath;
        AutoAcceptToggle.IsChecked = _settings.AutoAcceptFromTrusted;
        ShowCompleteToggle.IsChecked = _settings.ShowTransferComplete;
        
        // Sync
        SyncEnabledToggle.IsChecked = _settings.IsSyncEnabled;
        SyncFolderPathText.Text = ShortenPath(_settings.SyncFolderPath);
        SyncFolderPathText.ToolTip = _settings.SyncFolderPath;
        
        // Trusted Peers
        UpdateTrustedPeersList();
        
        // Version
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $" v{version?.Major ?? 1}.{version?.Minor ?? 0}.{version?.Build ?? 0}";
    }

    private void UpdateTrustedPeersList()
    {
        TrustedPeersListBox.ItemsSource = null;
        TrustedPeersListBox.ItemsSource = _settings.TrustedPeerIds;
        NoTrustedPeersText.Visibility = _settings.TrustedPeerIds.Count == 0 
            ? Visibility.Visible 
            : Visibility.Collapsed;
    }

    private static string ShortenPath(string path)
    {
        // Get the last two directory parts for display
        var parts = path.Split(Path.DirectorySeparatorChar);
        if (parts.Length > 2)
        {
            return string.Join(Path.DirectorySeparatorChar.ToString(), 
                parts[^3], parts[^2], parts[^1]);
        }
        return path;
    }

    #region Button Handlers

    private void BrowseDownloadPath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select download folder",
            SelectedPath = _settings.DownloadPath,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _settings.DownloadPath = dialog.SelectedPath;
            DownloadPathText.Text = ShortenPath(dialog.SelectedPath);
            DownloadPathText.ToolTip = dialog.SelectedPath;
        }
    }

    private void BrowseSyncFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select sync folder",
            SelectedPath = _settings.SyncFolderPath,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _settings.SyncFolderPath = dialog.SelectedPath;
            SyncFolderPathText.Text = ShortenPath(dialog.SelectedPath);
            SyncFolderPathText.ToolTip = dialog.SelectedPath;
        }
    }

    private void RemoveTrustedPeer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && 
            button.DataContext is string peerId)
        {
            _settings.TrustedPeerIds.Remove(peerId);
            UpdateTrustedPeersList();
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Apply settings from UI
        _settings.DeviceName = string.IsNullOrWhiteSpace(DeviceNameTextBox.Text) 
            ? Environment.MachineName 
            : DeviceNameTextBox.Text.Trim();
        _settings.StartMinimized = StartMinimizedToggle.IsChecked ?? false;
        _settings.AutoAcceptFromTrusted = AutoAcceptToggle.IsChecked ?? false;
        _settings.ShowTransferComplete = ShowCompleteToggle.IsChecked ?? true;
        _settings.IsSyncEnabled = SyncEnabledToggle.IsChecked ?? true;
        
        // Save to disk
        _settings.Save();
        
        // Notify parent
        _onSettingsChanged?.Invoke(_settings);
        
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        // Reload original settings (discard changes)
        DialogResult = false;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    #endregion

    #region Window Controls

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }

    #endregion
}
