using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Swarm.Core.Helpers;
using Swarm.Core.Models;
using Swarm.ViewModels;
using System.Windows.Forms;
using Drawing = System.Drawing;
using System.Windows.Controls;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;

namespace Swarm;

/// <summary>
/// MainWindow now acts purely as a View with minimal code-behind.
/// All business logic is delegated to MainViewModel.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private NotifyIcon? _trayIcon;

    // State for Modals
    private Action<bool>? _pendingFileAcceptCallback;
    private Peer? _pendingTrustPeer;

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
        
        // These are now handled by Commands/Navigation, but ActivityLog/VersionHistory are still Dialogs
        _viewModel.OpenVersionHistoryRequested += OnOpenVersionHistoryRequested;
        _viewModel.OpenActivityLogRequested += OnOpenActivityLogRequested;

        InitializeTrayIcon();

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void InitializeTrayIcon()
    {
        try
        {
            // Load icon from embedded resource
            Drawing.Icon? appIcon = null;
            try
            {
                var iconUri = new Uri("pack://application:,,,/Assets/swarm.ico", UriKind.Absolute);
                var iconStream = System.Windows.Application.GetResourceStream(iconUri)?.Stream;
                if (iconStream != null)
                {
                    appIcon = new Drawing.Icon(iconStream);
                }
            }
            catch
            {
                // Fallback to process icon
                appIcon = Drawing.Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty);
            }

            _trayIcon = new NotifyIcon
            {
                Icon = appIcon,
                Text = "Swarm - LAN File Sync",
                Visible = true
            };

            // Create context menu
            var contextMenu = new ContextMenuStrip();
            
            // Show/Hide option
            var showItem = new ToolStripMenuItem("Show Swarm");
            showItem.Click += (s, e) =>
            {
                Show();
                WindowState = WindowState.Normal;
                ShowInTaskbar = true;
                Activate();
            };
            showItem.Font = new Drawing.Font(showItem.Font, Drawing.FontStyle.Bold);
            contextMenu.Items.Add(showItem);
            
            contextMenu.Items.Add(new ToolStripSeparator());
            
            // Open Sync Folder
            var openFolderItem = new ToolStripMenuItem("Open Sync Folder");
            openFolderItem.Click += (s, e) =>
            {
                try
                {
                    var syncPath = _viewModel.Settings.SyncFolderPath;
                    if (Directory.Exists(syncPath))
                    {
                        Process.Start("explorer.exe", syncPath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to open sync folder: {ex.Message}");
                }
            };
            contextMenu.Items.Add(openFolderItem);
            
            // Toggle Sync
            var toggleSyncItem = new ToolStripMenuItem("Pause Sync");
            toggleSyncItem.Click += (s, e) =>
            {
                _viewModel.Settings.IsSyncEnabled = !_viewModel.Settings.IsSyncEnabled;
                _viewModel.Settings.Save();
                toggleSyncItem.Text = _viewModel.Settings.IsSyncEnabled ? "Pause Sync" : "Resume Sync";
                _viewModel.ApplySettings(_viewModel.Settings);
            };
            contextMenu.Items.Add(toggleSyncItem);
            
            contextMenu.Items.Add(new ToolStripSeparator());
            
            // Settings
            var settingsItem = new ToolStripMenuItem("Settings...");
            settingsItem.Click += (s, e) =>
            {
                Show();
                WindowState = WindowState.Normal;
                ShowInTaskbar = true;
                Activate();
                _viewModel.NavigateToSettingsCommand.Execute(null);
            };
            contextMenu.Items.Add(settingsItem);
            
            contextMenu.Items.Add(new ToolStripSeparator());
            
            // Exit
            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) =>
            {
                _trayIcon!.Visible = false;
                System.Windows.Application.Current.Shutdown();
            };
            contextMenu.Items.Add(exitItem);
            
            _trayIcon.ContextMenuStrip = contextMenu;

            // Double-click to show
            _trayIcon.DoubleClick += (s, e) =>
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
        
        // Note: SettingsView initialization is now handled via DataContextChanged in that View.

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

    private void ShowNotification(string title, string message, bool isError = false)
    {
        Dispatcher.Invoke(() =>
        {
            var border = new Border
            {
                Background = (Brush)FindResource("BackgroundCardBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                Margin = new Thickness(0, 8, 0, 0),
                BorderBrush = (Brush)FindResource(isError ? "StatusErrorBrush" : "AccentPrimaryBrush"),
                BorderThickness = new Thickness(1),
                Effect = (System.Windows.Media.Effects.Effect)FindResource("SmallShadow"),
                Opacity = 0,
                RenderTransform = new TranslateTransform(20, 0)
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock 
            { 
                Text = title, 
                FontWeight = FontWeights.SemiBold, 
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 4)
            });
            stack.Children.Add(new TextBlock 
            { 
                Text = message, 
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });

            border.Child = stack;
            NotificationStack.Children.Add(border);

            // Animate In
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300));
            var slideIn = new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut } };
            
            border.BeginAnimation(OpacityProperty, fadeIn);
            border.RenderTransform.BeginAnimation(TranslateTransform.XProperty, slideIn);

            // Auto Dismiss
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
                fadeOut.Completed += (s2, e2) => NotificationStack.Children.Remove(border);
                border.BeginAnimation(OpacityProperty, fadeOut);
            };
            timer.Start();
        });
    }

    private void OnDiscoveryBindingFailed()
    {
        ShowNotification("Discovery Warning", "Could not bind to port 37420. Other peers might not see you.", true);
    }

    private void OnUntrustedPeerDiscovered(Peer peer)
    {
        _pendingTrustPeer = peer;
        
        TrustDeviceName.Text = peer.Name;
        DeviceFingerprint.Text = peer.Fingerprint ?? "Unknown";
        MyFingerprint.Text = _viewModel.CryptoService.GetShortFingerprint();
        
        ModalOverlay.Visibility = Visibility.Visible;
        TrustDeviceDialog.Visibility = Visibility.Visible;
        IncomingFileDialog.Visibility = Visibility.Collapsed;
    }

    private void AcceptTrust_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingTrustPeer != null && !string.IsNullOrEmpty(_pendingTrustPeer.PublicKeyBase64))
        {
             _viewModel.Settings.TrustedPeerPublicKeys[_pendingTrustPeer.Id] = _pendingTrustPeer.PublicKeyBase64;
            
            if (!_viewModel.Settings.TrustedPeers.Any(p => p.Id == _pendingTrustPeer.Id))
            {
                _viewModel.Settings.TrustedPeers.Add(new TrustedPeer { Id = _pendingTrustPeer.Id, Name = _pendingTrustPeer.Name });
            }
            
            _viewModel.Settings.Save();
            _pendingTrustPeer.IsTrusted = true;
            
            ShowNotification("Device Trusted", $"Added {_pendingTrustPeer.Name} to trusted devices.");
        }
        
        CloseModal();
    }

    private void RejectTrust_Click(object sender, RoutedEventArgs e)
    {
        CloseModal();
    }

    private void DeviceFingerprint_Click(object sender, MouseButtonEventArgs e)
    {
        CopyToClipboardWithFeedback(DeviceFingerprint.Text, "Device fingerprint");
    }

    private void MyFingerprint_Click(object sender, MouseButtonEventArgs e)
    {
        CopyToClipboardWithFeedback(MyFingerprint.Text, "Your fingerprint");
    }

    private void CopyToClipboardWithFeedback(string text, string itemName)
    {
        if (string.IsNullOrEmpty(text)) return;
        
        try
        {
            System.Windows.Clipboard.SetText(text);
            ShowNotification("Copied to Clipboard", $"{itemName} copied.");
        }
        catch (Exception ex)
        {
            ShowNotification("Copy Failed", $"Could not copy: {ex.Message}", true);
        }
    }

    private void OnIncomingFileRequest(string fileName, string senderName, long fileSize, Action<bool> acceptCallback)
    {
        if (!_viewModel.Settings.NotificationsEnabled)
        {
            acceptCallback(false);
            return;
        }

        _pendingFileAcceptCallback = acceptCallback;
        
        IncomingSender.Text = $"from {senderName}";
        IncomingFileName.Text = fileName;
        IncomingFileSize.Text = FileHelpers.FormatBytes(fileSize);
        
        ModalOverlay.Visibility = Visibility.Visible;
        IncomingFileDialog.Visibility = Visibility.Visible;
        TrustDeviceDialog.Visibility = Visibility.Collapsed;
    }

    private void AcceptFile_Click(object sender, RoutedEventArgs e)
    {
        _pendingFileAcceptCallback?.Invoke(true);
        CloseModal();
    }

    private void RejectFile_Click(object sender, RoutedEventArgs e)
    {
        _pendingFileAcceptCallback?.Invoke(false);
        CloseModal();
    }

    private void CloseModal()
    {
        ModalOverlay.Visibility = Visibility.Collapsed;
        TrustDeviceDialog.Visibility = Visibility.Collapsed;
        IncomingFileDialog.Visibility = Visibility.Collapsed;
        _pendingTrustPeer = null;
        _pendingFileAcceptCallback = null;
    }

    private void OnTransferCompleted(FileTransfer transfer)
    {
        if (transfer.Direction == TransferDirection.Incoming && 
            _viewModel.Settings.NotificationsEnabled && 
            _viewModel.Settings.ShowTransferComplete)
        {
            ShowNotification("Transfer Complete", $"Received: {Path.GetFileName(transfer.LocalPath)}");
        }
    }

    private void OnFileConflictDetected(string filePath, string? backupPath)
    {
        var fileName = Path.GetFileName(filePath);
        var msg = backupPath != null 
            ? $"Conflict detected. Backup created."
            : $"Conflict detected. Saved to History.";
        
        ShowNotification("Sync Conflict", msg, true);
        
        Debug.WriteLine($"[CONFLICT] {filePath} -> {msg}");
        
        if (_trayIcon != null && _trayIcon.Visible && _viewModel.Settings.NotificationsEnabled)
        {
            _trayIcon.ShowBalloonTip(3000, "Sync Conflict", msg, ToolTipIcon.Info);
        }
    }

    private void ActivityFlyout_BackgroundClick(object sender, MouseButtonEventArgs e)
    {
        // Close flyout when clicking the background
        if (e.OriginalSource == sender)
        {
            _viewModel.IsActivityFlyoutOpen = false;
        }
    }
    
    private void SyncingIndicator_Click(object sender, MouseButtonEventArgs e)
    {
        // Open the activity flyout when clicking the syncing indicator
        _viewModel.IsActivityFlyoutOpen = true;
    }

    #endregion

    #region Dialog Handlers

    private void OnOpenActivityLogRequested()
    {
        var dialog = new UI.ActivityLogDialog(_viewModel.ActivityLogService)
        {
            Owner = this
        };
        dialog.ShowDialog();
    }

    private void OnOpenVersionHistoryRequested()
    {
        var dialog = new UI.FileHistoryDialog(_viewModel.VersioningService)
        {
            Owner = this
        };
        dialog.ShowDialog();
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