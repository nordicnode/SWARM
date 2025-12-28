using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Swarm.Core.Helpers;
using Swarm.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace Swarm.UI;

/// <summary>
/// Activity Log dialog for viewing sync and transfer history.
/// </summary>
public partial class ActivityLogDialog : Window
{
    private readonly ActivityLogService _activityLogService;
    private readonly ObservableCollection<ActivityLogEntry> _displayedEntries = new();
    private string _currentFilter = "All";
    private string _searchQuery = "";
    private bool _isInitialized = false;

    public ActivityLogDialog(ActivityLogService activityLogService)
    {
        InitializeComponent();
        _activityLogService = activityLogService;
        
        ActivityListBox.ItemsSource = _displayedEntries;
        
        // Subscribe to new entries
        _activityLogService.EntryAdded += OnEntryAdded;
        
        // Mark as initialized before refresh
        _isInitialized = true;
        RefreshList();
    }

    private void OnEntryAdded(ActivityLogEntry entry)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (ShouldDisplay(entry))
            {
                _displayedEntries.Insert(0, entry);
                UpdateEntryCount();
            }
        });
    }

    private void RefreshList()
    {
        _displayedEntries.Clear();
        
        var entries = _activityLogService.Entries
            .Where(ShouldDisplay)
            .Take(500); // Limit for performance
        
        foreach (var entry in entries)
        {
            _displayedEntries.Add(entry);
        }
        
        UpdateEntryCount();
        EmptyStateText.Visibility = _displayedEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool ShouldDisplay(ActivityLogEntry entry)
    {
        // Apply filter
        var passesFilter = _currentFilter switch
        {
            "Files Only" => entry.Type is ActivityType.FileCreated or ActivityType.FileModified 
                or ActivityType.FileDeleted or ActivityType.FileRenamed or ActivityType.FileSynced,
            "Transfers Only" => entry.Type is ActivityType.TransferStarted or ActivityType.TransferCompleted 
                or ActivityType.TransferFailed,
            "Peers Only" => entry.Type is ActivityType.PeerConnected or ActivityType.PeerDisconnected 
                or ActivityType.PeerTrusted or ActivityType.PeerUntrusted,
            "Errors Only" => entry.Severity == ActivitySeverity.Error,
            "Warnings" => entry.Severity is ActivitySeverity.Warning or ActivitySeverity.Error,
            _ => true
        };
        
        if (!passesFilter) return false;
        
        // Apply search
        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            return entry.Message.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
                   (entry.FilePath?.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (entry.PeerName?.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (entry.Details?.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ?? false);
        }
        
        return true;
    }

    private void UpdateEntryCount()
    {
        EntryCountText.Text = $"({_displayedEntries.Count} entries)";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = SearchBox.Text;
        RefreshList();
    }

    private void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return; // Skip during initialization
        
        if (FilterComboBox.SelectedItem is ComboBoxItem item)
        {
            _currentFilter = item.Content?.ToString() ?? "All";
            RefreshList();
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Activity Log",
            Filter = "CSV files (*.csv)|*.csv|JSON files (*.json)|*.json",
            DefaultExt = ".csv",
            FileName = $"swarm_activity_{DateTime.Now:yyyyMMdd_HHmmss}"
        };
        
        if (dialog.ShowDialog() == true)
        {
            try
            {
                var isJson = dialog.FilterIndex == 2;
                await _activityLogService.ExportAsync(dialog.FileName, isJson);
                MessageBox.Show($"Activity log exported to:\n{dialog.FileName}", "Export Complete", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export: {ex.Message}", "Export Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Are you sure you want to clear all activity log entries?\n\nThis action cannot be undone.",
            "Clear Activity Log",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        
        if (result == MessageBoxResult.Yes)
        {
            _activityLogService.Clear();
            RefreshList();
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshList();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _activityLogService.EntryAdded -= OnEntryAdded;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            else
                WindowState = WindowState.Maximized;
        }
        else
        {
            DragMove();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _activityLogService.EntryAdded -= OnEntryAdded;
        base.OnClosed(e);
    }
}
