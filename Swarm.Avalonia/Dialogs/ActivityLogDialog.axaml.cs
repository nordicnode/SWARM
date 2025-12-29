using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Swarm.Core.Models;
using Swarm.Core.Services;

namespace Swarm.Avalonia.Dialogs;

public partial class ActivityLogDialog : Window
{
    private readonly ActivityLogService _activityLogService;
    private readonly ObservableCollection<ActivityLogItemViewModel> _allEntries = new();
    private readonly ObservableCollection<ActivityLogItemViewModel> _filteredEntries = new();

    public ActivityLogDialog()
    {
        InitializeComponent();
        _activityLogService = null!;
    }

    public ActivityLogDialog(ActivityLogService activityLogService)
    {
        InitializeComponent();
        _activityLogService = activityLogService;
        
        ActivityListBox.ItemsSource = _filteredEntries;
        LoadEntries();
    }

    private void LoadEntries()
    {
        _allEntries.Clear();
        
        var entries = _activityLogService.GetRecentEntries(1000);
        foreach (var entry in entries.OrderByDescending(e => e.Timestamp))
        {
            _allEntries.Add(new ActivityLogItemViewModel(entry));
        }

        ApplyFilter();
        UpdateEntryCount();
    }

    private void ApplyFilter()
    {
        var searchText = SearchBox?.Text?.ToLowerInvariant() ?? "";
        var filterIndex = FilterComboBox?.SelectedIndex ?? 0;

        _filteredEntries.Clear();

        foreach (var entry in _allEntries)
        {
            // Apply type filter
            if (filterIndex > 0)
            {
                var matchesType = filterIndex switch
                {
                    1 => entry.Type == ActivityType.FileCreated || entry.Type == ActivityType.FileModified || 
                         entry.Type == ActivityType.FileDeleted || entry.Type == ActivityType.FileRenamed,
                    2 => entry.Type == ActivityType.TransferStarted || entry.Type == ActivityType.TransferCompleted ||
                         entry.Type == ActivityType.TransferFailed,
                    3 => entry.Type == ActivityType.PeerConnected || entry.Type == ActivityType.PeerDisconnected,
                    4 => entry.Severity == ActivitySeverity.Error,
                    _ => true
                };
                if (!matchesType) continue;
            }

            // Apply search filter
            if (!string.IsNullOrEmpty(searchText))
            {
                if (!entry.Message.ToLowerInvariant().Contains(searchText) &&
                    !(entry.Details?.ToLowerInvariant().Contains(searchText) ?? false))
                {
                    continue;
                }
            }

            _filteredEntries.Add(entry);
        }

        EmptyStateText.IsVisible = _filteredEntries.Count == 0;
        ActivityListBox.IsVisible = _filteredEntries.Count > 0;
    }

    private void UpdateEntryCount()
    {
        EntryCountText.Text = $"{_filteredEntries.Count} of {_allEntries.Count} entries";
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilter();
        UpdateEntryCount();
    }

    private void FilterComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ApplyFilter();
        UpdateEntryCount();
    }

    private void ClearButton_Click(object? sender, RoutedEventArgs e)
    {
        _activityLogService.Clear();
        LoadEntries();
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        LoadEntries();
    }

    private async void ExportButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = global::Avalonia.Controls.TopLevel.GetTopLevel(this);
            if (topLevel == null) return;
            
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new global::Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Export Activity Log",
                DefaultExtension = "csv",
                FileTypeChoices = new[]
                {
                    new global::Avalonia.Platform.Storage.FilePickerFileType("CSV Files") { Patterns = new[] { "*.csv" } },
                    new global::Avalonia.Platform.Storage.FilePickerFileType("Text Files") { Patterns = new[] { "*.txt" } }
                }
            });
            
            if (file != null)
            {
                using var stream = await file.OpenWriteAsync();
                using var writer = new System.IO.StreamWriter(stream);
                
                await writer.WriteLineAsync("Timestamp,Type,Severity,Message,Details");
                
                // Export filtered entries
                foreach (var entry in _filteredEntries)
                {
                    var line = $"\"{entry.Timestamp:yyyy-MM-dd HH:mm:ss}\",\"{entry.Type}\",\"{entry.Severity}\",\"{entry.Message.Replace("\"", "\"\"")}\",\"{entry.Details?.Replace("\"", "\"\"")}\"";
                    await writer.WriteLineAsync(line);
                }
            }
        }
        catch (Exception ex)
        {
            // Ideally use a message box service here, but for now just log
            System.Diagnostics.Debug.WriteLine($"Export failed: {ex.Message}");
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

/// <summary>
/// ViewModel wrapper for ActivityLogEntry with display properties.
/// </summary>
public class ActivityLogItemViewModel
{
    private readonly ActivityLogEntry _entry;

    public ActivityLogItemViewModel(ActivityLogEntry entry)
    {
        _entry = entry;
    }

    public DateTime Timestamp => _entry.Timestamp;
    public string TimestampDisplay => _entry.Timestamp.ToString("HH:mm:ss");
    public string Message => _entry.Message;
    public string? Details => _entry.Details;
    public bool HasDetails => !string.IsNullOrEmpty(_entry.Details);
    public ActivityType Type => _entry.Type;
    public ActivitySeverity Severity => _entry.Severity;

    public string TypeDisplay => _entry.Type switch
    {
        ActivityType.FileCreated => "File",
        ActivityType.FileModified => "File",
        ActivityType.FileDeleted => "File",
        ActivityType.FileRenamed => "File",
        ActivityType.TransferStarted => "Transfer",
        ActivityType.TransferCompleted => "Transfer",
        ActivityType.TransferFailed => "Transfer",
        ActivityType.PeerConnected => "Peer",
        ActivityType.PeerDisconnected => "Peer",
        ActivityType.SyncStarted => "Sync",
        ActivityType.SyncCompleted => "Sync",
        ActivityType.Error => "Error",
        ActivityType.Warning => "Warning",
        _ => "Info"
    };

    public IBrush SeverityColor => _entry.Severity switch
    {
        ActivitySeverity.Success => Brushes.LimeGreen,
        ActivitySeverity.Warning => Brushes.Orange,
        ActivitySeverity.Error => Brushes.Tomato,
        _ => Brushes.Gray
    };
}
