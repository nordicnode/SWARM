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
    private readonly IntegrityService? _integrityService;

    public SettingsDialog(Settings settings, Action<Settings>? onSettingsChanged = null, IntegrityService? integrityService = null)
    {
        InitializeComponent();
        
        _originalSettings = settings;
        _workingSettings = settings.Clone();
        _onSettingsChanged = onSettingsChanged;
        _integrityService = integrityService;
        
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
        
        // Versioning Settings
        VersioningEnabledToggle.IsChecked = _workingSettings.VersioningEnabled;
        MaxVersionsSlider.Value = _workingSettings.MaxVersionsPerFile;
        MaxVersionsValueText.Text = _workingSettings.MaxVersionsPerFile.ToString();
        MaxAgeSlider.Value = _workingSettings.MaxVersionAgeDays;
        MaxAgeValueText.Text = _workingSettings.MaxVersionAgeDays == 0 ? "Forever" : $"{_workingSettings.MaxVersionAgeDays} days";

        // Bandwidth Settings
        MaxDownloadSpeedSlider.Value = _workingSettings.MaxDownloadSpeedKBps;
        MaxDownloadSpeedValueText.Text = FormatSpeed(_workingSettings.MaxDownloadSpeedKBps);
        MaxUploadSpeedSlider.Value = _workingSettings.MaxUploadSpeedKBps;
        MaxUploadSpeedValueText.Text = FormatSpeed(_workingSettings.MaxUploadSpeedKBps);
        
        // Trusted Peers
        UpdateTrustedPeersList();
        
        // Excluded Folders (Selective Sync)
        UpdateExcludedFoldersList();
        
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

    private void UpdateExcludedFoldersList()
    {
        ExcludedFoldersListBox.ItemsSource = null;
        ExcludedFoldersListBox.ItemsSource = _workingSettings.ExcludedFolders;
        NoExcludedFoldersText.Visibility = _workingSettings.ExcludedFolders.Count == 0 
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

    private void StartMinimizedToggle_Click(object sender, RoutedEventArgs e)
    {
        // Handled by binding/toggle logic
    }

    private void AddExcludedFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select folder to exclude from sync",
            SelectedPath = _workingSettings.SyncFolderPath,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            // Make path relative to sync folder
            var syncFolderPath = _workingSettings.SyncFolderPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (dialog.SelectedPath.StartsWith(syncFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                var relativePath = dialog.SelectedPath.Substring(syncFolderPath.Length);
                if (!string.IsNullOrEmpty(relativePath) && !_workingSettings.ExcludedFolders.Contains(relativePath))
                {
                    _workingSettings.ExcludedFolders.Add(relativePath);
                    UpdateExcludedFoldersList();
                }
            }
            else
            {
                System.Windows.MessageBox.Show(
                    "Please select a folder inside your sync folder.",
                    "Invalid Selection",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private void RemoveExcludedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && 
            button.DataContext is string folder)
        {
            _workingSettings.ExcludedFolders.Remove(folder);
            UpdateExcludedFoldersList();
        }
    }

    private void MaxVersionsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MaxVersionsValueText != null)
        {
            MaxVersionsValueText.Text = ((int)e.NewValue).ToString();
        }
    }

    private void MaxAgeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MaxAgeValueText != null)
        {
            int days = (int)e.NewValue;
            MaxAgeValueText.Text = days == 0 ? "Forever" : $"{days} days";
        }
    }

    private void MaxDownloadSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MaxDownloadSpeedValueText != null)
        {
            MaxDownloadSpeedValueText.Text = FormatSpeed((long)e.NewValue);
        }
    }

    private void MaxUploadSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MaxUploadSpeedValueText != null)
        {
            MaxUploadSpeedValueText.Text = FormatSpeed((long)e.NewValue);
        }
    }

    private static string FormatSpeed(long kbps)
    {
        if (kbps == 0) return "Unlimited";
        if (kbps >= 1024) return $"{kbps / 1024} MB/s";
        return $"{kbps} KB/s";
    }

    private async void VerifyIntegrity_Click(object sender, RoutedEventArgs e)
    {
        if (_integrityService == null)
        {
            System.Windows.MessageBox.Show(
                "Integrity service is not available. Please ensure sync is enabled.",
                "Service Not Available",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // Disable button during check
        VerifyIntegrityButton.IsEnabled = false;
        VerifyIntegrityButton.Content = "Checking...";

        try
        {
            var result = await _integrityService.VerifyLocalIntegrityAsync();
            
            // Show result dialog
            var resultDialog = new IntegrityResultDialog(result)
            {
                Owner = this
            };
            resultDialog.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to verify integrity: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            VerifyIntegrityButton.IsEnabled = true;
            VerifyIntegrityButton.Content = "Verify Integrity";
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
        _workingSettings.VersioningEnabled = VersioningEnabledToggle.IsChecked ?? true;
        _workingSettings.MaxVersionsPerFile = (int)MaxVersionsSlider.Value;
        _workingSettings.MaxVersionAgeDays = (int)MaxAgeSlider.Value;
        _workingSettings.MaxDownloadSpeedKBps = (long)MaxDownloadSpeedSlider.Value;
        _workingSettings.MaxUploadSpeedKBps = (long)MaxUploadSpeedSlider.Value;
        
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
