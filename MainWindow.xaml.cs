using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Swarm.Core;
using Swarm.Models;
using MessageBox = System.Windows.MessageBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Brush = System.Windows.Media.Brush;
using Swarm.UI;

namespace Swarm;

public partial class MainWindow : Window
{
    private readonly Settings _settings;
    private readonly DiscoveryService _discoveryService;
    private readonly TransferService _transferService;
    private readonly SyncService _syncService;
    private Peer? _selectedPeer;

    public MainWindow()
    {
        InitializeComponent();

        // Load settings
        _settings = Settings.Load();

        _discoveryService = new DiscoveryService();
        _transferService = new TransferService(_settings);
        _syncService = new SyncService(_settings, _discoveryService, _transferService);

        // Bind collections
        PeerListBox.ItemsSource = _discoveryService.Peers;
        TransfersListBox.ItemsSource = _transferService.Transfers;

        // Wire up events
        _discoveryService.Peers.CollectionChanged += (s, e) => UpdatePeerUI();
        _transferService.IncomingFileRequest += OnIncomingFileRequest;
        _transferService.TransferProgress += OnTransferProgress;
        _transferService.TransferCompleted += OnTransferCompleted;

        // Wire up sync events
        _syncService.SyncStatusChanged += OnSyncStatusChanged;
        _syncService.FileChanged += OnSyncFileChanged;
        _syncService.IncomingSyncFile += OnIncomingSyncFile;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Start services
        _transferService.Start();
        _discoveryService.Start(_transferService.ListenPort);

        // Update discovery service's settings
        _discoveryService.LocalName = _settings.DeviceName;
        _discoveryService.IsSyncEnabled = _settings.IsSyncEnabled;

        // Start sync service
        if (_settings.IsSyncEnabled)
        {
            _syncService.Start();
        }

        // Start radar animation
        var radarAnimation = (Storyboard)FindResource("RadarPulseAnimation");
        radarAnimation.Begin();

        UpdatePeerUI();
        UpdateSyncUI();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _syncService.Dispose();
        _discoveryService.Dispose();
        _transferService.Dispose();
    }

    private void UpdatePeerUI()
    {
        var count = _discoveryService.Peers.Count;
        PeerCountText.Text = count == 1 ? "1 device found" : $"{count} devices found";
        EmptyPeersPanel.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = count == 0 ? "Scanning for peers..." : $"Connected to {count} device(s)";
    }

    private void UpdateSyncUI()
    {
        var isEnabled = _syncService.IsEnabled;
        
        // Update status text
        SyncStatusText.Text = isEnabled ? "Sync enabled" : "Sync disabled";
        
        // Update indicator color
        SyncIndicator.Fill = isEnabled 
            ? (Brush)FindResource("StatusOnlineBrush")
            : (Brush)FindResource("TextMutedBrush");
        
        // Update toggle button text
        SyncToggleButton.Content = isEnabled ? "Disable Sync" : "Enable Sync";
        
        // Update folder path display
        var docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var displayPath = _settings.SyncFolderPath.StartsWith(docPath)
            ? _settings.SyncFolderPath.Replace(docPath, "Documents")
            : _settings.SyncFolderPath;
        SyncFolderPathText.Text = displayPath;
    }

    private void OnSyncStatusChanged(string status)
    {
        Dispatcher.Invoke(() =>
        {
            SyncStatusText.Text = status;
        });
    }

    private void OnSyncFileChanged(SyncedFile file)
    {
        Dispatcher.Invoke(() =>
        {
            // Brief visual feedback that sync is active
            SyncIndicator.Fill = (Brush)FindResource("AccentPrimaryBrush");
            
            // Reset after a short delay
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            timer.Tick += (s, e) =>
            {
                SyncIndicator.Fill = (Brush)FindResource("StatusOnlineBrush");
                timer.Stop();
            };
            timer.Start();
        });
    }

    private void OnIncomingSyncFile(SyncedFile file)
    {
        Dispatcher.Invoke(() =>
        {
            System.Diagnostics.Debug.WriteLine($"Synced: {file.RelativePath}");
        });
    }

    private void OnIncomingFileRequest(string fileName, string senderName, long fileSize, Action<bool> acceptCallback)
    {
        var sizeStr = FormatFileSize(fileSize);
        var result = MessageBox.Show(
            $"{senderName} wants to send you:\n\n{fileName} ({sizeStr})\n\nAccept?",
            "Incoming File",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        acceptCallback(result == MessageBoxResult.Yes);
    }

    private void OnTransferProgress(FileTransfer transfer)
    {
        Dispatcher.Invoke(() =>
        {
            EmptyTransfersText.Visibility = Visibility.Collapsed;
            // Force refresh
            TransfersListBox.Items.Refresh();
        });
    }

    private void OnTransferCompleted(FileTransfer transfer)
    {
        Dispatcher.Invoke(() =>
        {
            if (transfer.Direction == TransferDirection.Incoming && _settings.ShowTransferComplete)
            {
                MessageBox.Show($"File received:\n{transfer.LocalPath}", "Transfer Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        });
    }

    #region Drag & Drop

    private void DropZone_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            var animation = (Storyboard)FindResource("DragEnterAnimation");
            animation.Begin();
            DropZoneTitle.Text = "Release to send";
            // Change icon color on drag
            DropZoneIcon.Fill = (Brush)FindResource("AccentGlowBrush");
        }
    }

    private void DropZone_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        var animation = (Storyboard)FindResource("DragLeaveAnimation");
        animation.Begin();
        DropZoneTitle.Text = "Drop files here to send";
        // Reset icon color
        DropZoneIcon.Fill = (Brush)FindResource("AccentPrimaryBrush");
    }

    private async void DropZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        DropZone_DragLeave(sender, e);

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        await SendFiles(files);
    }

    #endregion

    #region Button Handlers

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select files to send",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            await SendFiles(dialog.FileNames);
        }
    }

    private void PeerListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _selectedPeer = PeerListBox.SelectedItem as Peer;
        if (_selectedPeer != null)
        {
            SelectedPeerText.Text = $"Sending to: {_selectedPeer.Name}";
        }
        else
        {
            SelectedPeerText.Text = "";
        }
    }

    #endregion

    #region Sync Folder Handlers

    private void SyncToggle_Click(object sender, RoutedEventArgs e)
    {
        var newState = !_syncService.IsEnabled;
        _syncService.SetEnabled(newState);
        _discoveryService.IsSyncEnabled = newState;
        UpdateSyncUI();
    }

    private void ChangeSyncFolder_Click(object sender, RoutedEventArgs e)
    {
        // Use folder browser dialog
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select Sync Folder",
            InitialDirectory = _settings.SyncFolderPath,
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _syncService.SetSyncFolderPath(dialog.SelectedPath);
            UpdateSyncUI();
        }
    }

    private void OpenSyncFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.EnsureSyncFolderExists();
            Process.Start(new ProcessStartInfo
            {
                FileName = _settings.SyncFolderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open folder:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region File Sending

    private async Task SendFiles(string[] filePaths)
    {
        if (_selectedPeer == null)
        {
            if (_discoveryService.Peers.Count == 0)
            {
                MessageBox.Show("No devices found. Make sure another device is running Swarm on the same network.",
                    "No Devices", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Auto-select first peer if only one
            if (_discoveryService.Peers.Count == 1)
            {
                _selectedPeer = _discoveryService.Peers[0];
            }
            else
            {
                MessageBox.Show("Please select a device to send to.", "Select Device", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        EmptyTransfersText.Visibility = Visibility.Collapsed;

        foreach (var filePath in filePaths)
        {
            if (File.Exists(filePath))
            {
                try
                {
                    await _transferService.SendFile(_selectedPeer, filePath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to send {Path.GetFileName(filePath)}:\n{ex.Message}",
                        "Transfer Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    #endregion

    #region Window Controls

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsDialog = new SettingsDialog(_settings, OnSettingsChanged)
        {
            Owner = this
        };
        settingsDialog.ShowDialog();
    }

    private void OnSettingsChanged(Settings settings)
    {
        // Apply changes that affect running services
        _discoveryService.LocalName = settings.DeviceName;
        _discoveryService.IsSyncEnabled = settings.IsSyncEnabled;
        _transferService.SetDownloadPath(settings.DownloadPath);
        
        // Update sync service state
        if (settings.IsSyncEnabled && !_syncService.IsRunning)
        {
            _syncService.Start();
        }
        else if (!settings.IsSyncEnabled && _syncService.IsRunning)
        {
            _syncService.Stop();
        }
        
        // Update UI
        UpdateSyncUI();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            MaximizeButton_Click(sender, e);
        }
        else
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #endregion

    #region Helpers

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    #endregion
}