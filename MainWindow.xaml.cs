using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Swarm.Helpers;
using Swarm.Models;
using Swarm.ViewModels;
using MessageBox = System.Windows.MessageBox;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Brush = System.Windows.Media.Brush;
using Swarm.UI;
using System.Windows.Forms;
using Drawing = System.Drawing;

namespace Swarm;

/// <summary>
/// MainWindow now acts purely as a View with minimal code-behind.
/// All business logic is delegated to MainViewModel.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private NotifyIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();

        // Create and bind ViewModel
        _viewModel = new MainViewModel(Dispatcher);
        DataContext = _viewModel;

        // Subscribe to ViewModel events that require View interaction
        _viewModel.DiscoveryBindingFailed += OnDiscoveryBindingFailed;
        _viewModel.UntrustedPeerDiscoveredEvent += OnUntrustedPeerDiscovered;
        _viewModel.IncomingFileRequestEvent += OnIncomingFileRequest;
        _viewModel.TransferCompletedEvent += OnTransferCompleted;
        _viewModel.FileConflictDetectedEvent += OnFileConflictDetected;

        InitializeTrayIcon();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void InitializeTrayIcon()
    {
        try
        {
            _trayIcon = new NotifyIcon
            {
                Icon = Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty),
                Text = "Swarm",
                Visible = true
            };

            _trayIcon.Click += (s, e) =>
            {
                Show();
                WindowState = WindowState.Normal;
                ShowInTaskbar = true;
                Activate();
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to initialize tray icon: {ex.Message}");
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Initialize ViewModel services
        _viewModel.Initialize();

        // Start radar animation
        var radarAnimation = (Storyboard)FindResource("RadarPulseAnimation");
        radarAnimation.Begin();

        // Check if we should start minimized
        if (_viewModel.Settings.StartMinimized)
        {
            HideToTray();
        }
    }

    private void HideToTray()
    {
        WindowState = WindowState.Minimized;
        Hide();
        ShowInTaskbar = false;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _viewModel.Dispose();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HideToTray();
        }
        base.OnStateChanged(e);
    }

    #region View-Specific Event Handlers

    private void PulseSyncIndicator()
    {
        SyncIndicator.Fill = (Brush)FindResource("AccentPrimaryBrush");
        
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
    }

    private void OnDiscoveryBindingFailed()
    {
        MessageBox.Show(
            "Could not bind to the standard discovery port (37420).\n\n" +
            "You may not be visible to other peers, but you can still search for them.\n" +
            "This usually happens if another application is using the port.",
            "Discovery Warning",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void OnUntrustedPeerDiscovered(Peer peer)
    {
        var fingerprint = peer.Fingerprint ?? "Unknown";
        var localFingerprint = _viewModel.CryptoService.GetShortFingerprint();
        
        var message = $"New device discovered: {peer.Name}\n\n" +
                     $"Device Fingerprint:\n{fingerprint}\n\n" +
                     $"Your Fingerprint:\n{localFingerprint}\n\n" +
                     "To verify this device, compare fingerprints on both devices.\n\n" +
                     "Do you want to trust this device?";
        
        var result = MessageBox.Show(
            message,
            "Trust New Device",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        
        if (result == MessageBoxResult.Yes && !string.IsNullOrEmpty(peer.PublicKeyBase64))
        {
            _viewModel.Settings.TrustedPeerPublicKeys[peer.Id] = peer.PublicKeyBase64;
            
            if (!_viewModel.Settings.TrustedPeers.Any(p => p.Id == peer.Id))
            {
                _viewModel.Settings.TrustedPeers.Add(new TrustedPeer { Id = peer.Id, Name = peer.Name });
            }
            
            _viewModel.Settings.Save();
            peer.IsTrusted = true;
            
            Debug.WriteLine($"Trusted peer: {peer.Name} ({peer.Id})");
        }
    }

    private void OnIncomingFileRequest(string fileName, string senderName, long fileSize, Action<bool> acceptCallback)
    {
        if (!_viewModel.Settings.NotificationsEnabled)
        {
            acceptCallback(false);
            return;
        }

        var sizeStr = FileHelpers.FormatBytes(fileSize);
        var result = MessageBox.Show(
            $"{senderName} wants to send you:\n\n{fileName} ({sizeStr})\n\nAccept?",
            "Incoming File",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        acceptCallback(result == MessageBoxResult.Yes);
    }

    private void OnTransferCompleted(FileTransfer transfer)
    {
        if (transfer.Direction == TransferDirection.Incoming && 
            _viewModel.Settings.NotificationsEnabled && 
            _viewModel.Settings.ShowTransferComplete)
        {
            MessageBox.Show($"File received:\n{transfer.LocalPath}", "Transfer Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnFileConflictDetected(string filePath, string backupPath)
    {
        var fileName = Path.GetFileName(filePath);
        var msg = $"Conflict detected in {fileName}. Backup created.";
        
        Debug.WriteLine($"[CONFLICT] {filePath} backed up to {backupPath}");
        
        if (_trayIcon != null && _trayIcon.Visible && _viewModel.Settings.NotificationsEnabled)
        {
            _trayIcon.ShowBalloonTip(3000, "Sync Conflict", msg, ToolTipIcon.Info);
        }
    }

    #endregion

    #region Drag & Drop

    private void DropZone_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            var animation = (Storyboard)FindResource("DragEnterAnimation");
            animation.Begin();
            DropZoneTitle.Text = "Release to send";
            DropZoneIcon.Fill = (Brush)FindResource("AccentGlowBrush");
        }
    }

    private void DropZone_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        var animation = (Storyboard)FindResource("DragLeaveAnimation");
        animation.Begin();
        DropZoneTitle.Text = "Drop files here to send";
        DropZoneIcon.Fill = (Brush)FindResource("AccentPrimaryBrush");
    }

    private async void DropZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        DropZone_DragLeave(sender, e);

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        await _viewModel.SendFilesAsync(files);
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
            await _viewModel.SendFilesAsync(dialog.FileNames);
        }
    }

    private void PeerListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _viewModel.SelectedPeer = PeerListBox.SelectedItem as Peer;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var settingsDialog = new SettingsDialog(_viewModel.Settings, _viewModel.ApplySettings)
        {
            Owner = this
        };
        settingsDialog.ShowDialog();
    }

    #endregion

    #region Window Chrome

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
}