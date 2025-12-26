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
    private readonly Settings _originalSettings;
    private readonly Settings _workingSettings;
    private readonly Action<Settings>? _onSettingsChanged;

    public SettingsDialog(Settings settings, Action<Settings>? onSettingsChanged = null)
    {
        InitializeComponent();
        
        _originalSettings = settings;
        _workingSettings = settings.Clone();
        _onSettingsChanged = onSettingsChanged;
        
        LoadSettings();
    }

    private void LoadSettings()
    {
        // General
        DeviceNameTextBox.Text = _workingSettings.DeviceName;
        StartMinimizedToggle.IsChecked = _workingSettings.StartMinimized;
        
        // Transfers
        NotificationsToggle.IsChecked = _workingSettings.NotificationsEnabled;
        DownloadPathText.Text = ShortenPath(_workingSettings.DownloadPath);
        DownloadPathText.ToolTip = _workingSettings.DownloadPath;
        AutoAcceptToggle.IsChecked = _workingSettings.AutoAcceptFromTrusted;
        ShowCompleteToggle.IsChecked = _workingSettings.ShowTransferComplete;
        
        // Sync
        SyncEnabledToggle.IsChecked = _workingSettings.IsSyncEnabled;
        SyncFolderPathText.Text = ShortenPath(_workingSettings.SyncFolderPath);
        SyncFolderPathText.ToolTip = _workingSettings.SyncFolderPath;
        
        // Trusted Peers
        UpdateTrustedPeersList();
        
        // Version
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $" v{version?.Major ?? 1}.{version?.Minor ?? 0}.{version?.Build ?? 0}";
    }

    private void UpdateTrustedPeersList()
    {
        TrustedPeersListBox.ItemsSource = null;
        TrustedPeersListBox.ItemsSource = _workingSettings.TrustedPeers;
        NoTrustedPeersText.Visibility = _workingSettings.TrustedPeers.Count == 0 
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
            SelectedPath = _workingSettings.DownloadPath,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _workingSettings.DownloadPath = dialog.SelectedPath;
            DownloadPathText.Text = ShortenPath(dialog.SelectedPath);
            DownloadPathText.ToolTip = dialog.SelectedPath;
        }
    }

    private void BrowseSyncFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select sync folder",
            SelectedPath = _workingSettings.SyncFolderPath,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _workingSettings.SyncFolderPath = dialog.SelectedPath;
            SyncFolderPathText.Text = ShortenPath(dialog.SelectedPath);
            SyncFolderPathText.ToolTip = dialog.SelectedPath;
        }
    }

    private void RemoveTrustedPeer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && 
            button.DataContext is Swarm.Models.TrustedPeer peer)
        {
            var itemToRemove = _workingSettings.TrustedPeers.FirstOrDefault(p => p.Id == peer.Id);
            if (itemToRemove != null)
            {
                _workingSettings.TrustedPeers.Remove(itemToRemove);
                UpdateTrustedPeersList();
            }
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Apply remaining settings from UI to working copy
        _workingSettings.DeviceName = string.IsNullOrWhiteSpace(DeviceNameTextBox.Text) 
            ? Environment.MachineName 
            : DeviceNameTextBox.Text.Trim();
        _workingSettings.StartMinimized = StartMinimizedToggle.IsChecked ?? false;
        _workingSettings.NotificationsEnabled = NotificationsToggle.IsChecked ?? true;
        _workingSettings.AutoAcceptFromTrusted = AutoAcceptToggle.IsChecked ?? false;
        _workingSettings.ShowTransferComplete = ShowCompleteToggle.IsChecked ?? true;
        _workingSettings.IsSyncEnabled = SyncEnabledToggle.IsChecked ?? true;
        
        // Update original settings from working copy
        _originalSettings.UpdateFrom(_workingSettings);
        
        // Save to disk
        _originalSettings.Save();
        
        // Notify parent
        _onSettingsChanged?.Invoke(_originalSettings);
        
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
