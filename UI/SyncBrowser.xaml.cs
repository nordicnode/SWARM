using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Swarm.Core.Helpers;

namespace Swarm.UI;

/// <summary>
/// View model for file items in the browser.
/// </summary>
public class FileListItem
{
    public string Name { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    
    public string SizeDisplay => IsDirectory ? "" : FileHelpers.FormatBytes(Size);
    public string ModifiedDisplay => Modified.ToString("MMM d, yyyy");
    public Visibility ShowPath => Visibility.Collapsed;
    
    public string IconText => IsDirectory ? "📁" : GetFileTypeIcon();
    public System.Windows.Media.Color IconColor => IsDirectory 
        ? (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F59E0B")!
        : GetFileTypeColor();
    
    private string GetFileTypeIcon()
    {
        var ext = Path.GetExtension(Name).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "PDF",
            ".doc" or ".docx" => "DOC",
            ".xls" or ".xlsx" => "XLS",
            ".ppt" or ".pptx" => "PPT",
            ".txt" or ".md" => "TXT",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "IMG",
            ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" => "VID",
            ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" => "AUD",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "ZIP",
            ".exe" or ".msi" => "EXE",
            ".cs" or ".js" or ".py" or ".java" or ".cpp" or ".html" or ".css" => "COD",
            _ => Path.GetExtension(Name).TrimStart('.').ToUpperInvariant().PadLeft(3)[..3]
        };
    }
    
    private System.Windows.Media.Color GetFileTypeColor()
    {
        var ext = Path.GetExtension(Name).ToLowerInvariant();
        var colorHex = ext switch
        {
            ".pdf" => "#E11D48",
            ".doc" or ".docx" => "#2563EB",
            ".xls" or ".xlsx" => "#16A34A",
            ".ppt" or ".pptx" => "#EA580C",
            ".txt" or ".md" => "#6B7280",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "#8B5CF6",
            ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" => "#DB2777",
            ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" => "#0891B2",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "#A16207",
            ".exe" or ".msi" => "#DC2626",
            ".cs" or ".js" or ".py" or ".java" or ".cpp" or ".html" or ".css" => "#059669",
            _ => "#6366F1"
        };
        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex)!;
    }
}

/// <summary>
/// Sync Folder Browser with search, filter, and file listing.
/// </summary>
public partial class SyncBrowser : Window
{
    private readonly Settings _settings;
    private readonly ShareLinkService _shareLinkService;
    private readonly ObservableCollection<FileListItem> _allFiles = new();
    private readonly ObservableCollection<FileListItem> _filteredFiles = new();
    
    private string _currentPath = "";
    private string _searchQuery = "";
    private string _currentFilter = "All Files";
    private bool _sortAscending = true;
    private string _sortProperty = "Name";

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            _searchQuery = value;
            if (IsLoaded) ApplyFilters();
        }
    }

    public SyncBrowser(Settings settings, ShareLinkService shareLinkService)
    {
        InitializeComponent();
        
        _settings = settings;
        _shareLinkService = shareLinkService;
        _currentPath = "";
        
        FileListView.ItemsSource = _filteredFiles;
        FileListView.SelectionChanged += FileListView_SelectionChanged;
        
        Loaded += SyncBrowser_Loaded;
    }

    private void SyncBrowser_Loaded(object sender, RoutedEventArgs e)
    {
        LoadFiles();
    }

    private void LoadFiles()
    {
        _allFiles.Clear();
        _filteredFiles.Clear();
        
        var basePath = Path.Combine(_settings.SyncFolderPath, _currentPath);
        
        if (!Directory.Exists(basePath))
        {
            ShowEmptyState("Sync folder not found");
            return;
        }

        try
        {
            // Load directories first
            foreach (var dir in Directory.GetDirectories(basePath))
            {
                var dirInfo = new DirectoryInfo(dir);
                _allFiles.Add(new FileListItem
                {
                    Name = dirInfo.Name,
                    RelativePath = Path.Combine(_currentPath, dirInfo.Name),
                    FullPath = dir,
                    IsDirectory = true,
                    Modified = dirInfo.LastWriteTime
                });
            }
            
            // Load files
            foreach (var file in Directory.GetFiles(basePath))
            {
                var fileInfo = new FileInfo(file);
                _allFiles.Add(new FileListItem
                {
                    Name = fileInfo.Name,
                    RelativePath = Path.Combine(_currentPath, fileInfo.Name),
                    FullPath = file,
                    IsDirectory = false,
                    Size = fileInfo.Length,
                    Modified = fileInfo.LastWriteTime
                });
            }
            
            ApplyFilters();
            UpdateBreadcrumb();
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            ShowEmptyState($"Error loading files: {ex.Message}");
        }
    }

    private void ApplyFilters()
    {
        // Guard against being called before UI is fully loaded
        if (!IsLoaded || ClearSearchButton == null) return;
        
        var filtered = _allFiles.AsEnumerable();
        
        // Apply search filter
        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            filtered = filtered.Where(f => 
                f.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
            ClearSearchButton.Visibility = Visibility.Visible;
        }
        else
        {
            ClearSearchButton.Visibility = Visibility.Collapsed;
        }
        
        // Apply type filter
        filtered = _currentFilter switch
        {
            "Documents" => filtered.Where(f => f.IsDirectory || IsDocument(f.Name)),
            "Images" => filtered.Where(f => f.IsDirectory || IsImage(f.Name)),
            "Videos" => filtered.Where(f => f.IsDirectory || IsVideo(f.Name)),
            "Audio" => filtered.Where(f => f.IsDirectory || IsAudio(f.Name)),
            "Archives" => filtered.Where(f => f.IsDirectory || IsArchive(f.Name)),
            "Other" => filtered.Where(f => f.IsDirectory || IsOther(f.Name)),
            _ => filtered
        };
        
        // Apply sorting (directories always first)
        var sorted = filtered.OrderByDescending(f => f.IsDirectory);
        
        sorted = _sortProperty switch
        {
            "Name" => _sortAscending 
                ? sorted.ThenBy(f => f.Name) 
                : sorted.ThenByDescending(f => f.Name),
            "Size" => _sortAscending 
                ? sorted.ThenBy(f => f.Size) 
                : sorted.ThenByDescending(f => f.Size),
            "Modified" => _sortAscending 
                ? sorted.ThenBy(f => f.Modified) 
                : sorted.ThenByDescending(f => f.Modified),
            _ => sorted.ThenBy(f => f.Name)
        };
        
        _filteredFiles.Clear();
        foreach (var item in sorted)
        {
            _filteredFiles.Add(item);
        }
        
        // Show/hide empty state
        if (_filteredFiles.Count == 0)
        {
            ShowEmptyState(_searchQuery.Length > 0 ? "No matching files" : "Folder is empty");
        }
        else
        {
            EmptyState.Visibility = Visibility.Collapsed;
            FileListView.Visibility = Visibility.Visible;
        }
        
        UpdateStatusBar();
    }

    private void UpdateBreadcrumb()
    {
        CurrentPathText.Text = string.IsNullOrEmpty(_currentPath) 
            ? "/" 
            : "/" + _currentPath.Replace("\\", "/");
        BackButton.IsEnabled = !string.IsNullOrEmpty(_currentPath);
    }

    private void UpdateStatusBar()
    {
        var fileCount = _filteredFiles.Count(f => !f.IsDirectory);
        var folderCount = _filteredFiles.Count(f => f.IsDirectory);
        var totalSize = _filteredFiles.Where(f => !f.IsDirectory).Sum(f => f.Size);
        
        var countText = new List<string>();
        if (folderCount > 0) countText.Add($"{folderCount} folder{(folderCount == 1 ? "" : "s")}");
        if (fileCount > 0) countText.Add($"{fileCount} file{(fileCount == 1 ? "" : "s")}");
        
        FileCountText.Text = countText.Count > 0 ? string.Join(", ", countText) : "Empty";
        TotalSizeText.Text = FileHelpers.FormatBytes(totalSize);
    }

    private void ShowEmptyState(string hint)
    {
        if (!IsLoaded || EmptyState == null) return;
        EmptyState.Visibility = Visibility.Visible;
        FileListView.Visibility = Visibility.Collapsed;
        EmptyHintText.Text = hint;
    }

    #region File Type Helpers
    
    private static bool IsDocument(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" 
            or ".txt" or ".rtf" or ".odt" or ".ods" or ".odp" or ".md" or ".csv";
    }
    
    private static bool IsImage(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico" or ".tiff" or ".raw";
    }
    
    private static bool IsVideo(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".flv" or ".webm" or ".m4v";
    }
    
    private static bool IsAudio(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".mp3" or ".wav" or ".flac" or ".aac" or ".ogg" or ".wma" or ".m4a";
    }
    
    private static bool IsArchive(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz";
    }
    
    private static bool IsOther(string name)
    {
        return !IsDocument(name) && !IsImage(name) && !IsVideo(name) && !IsAudio(name) && !IsArchive(name);
    }
    
    #endregion

    #region Event Handlers

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchQuery = SearchBox.Text;
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchQuery = "";
    }

    private void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (FilterCombo.SelectedItem is ComboBoxItem item)
        {
            _currentFilter = item.Content?.ToString() ?? "All Files";
            ApplyFilters();
        }
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        // Cycle through sort options: Name → Size → Modified → Name
        _sortProperty = _sortProperty switch
        {
            "Name" => _sortAscending ? "Name" : "Size",
            "Size" => _sortAscending ? "Size" : "Modified",
            "Modified" => _sortAscending ? "Modified" : "Name",
            _ => "Name"
        };
        
        if (_sortProperty == "Name" && !_sortAscending)
        {
            _sortProperty = "Size";
        }
        else
        {
            _sortAscending = !_sortAscending;
        }
        
        SortText.Text = $"{_sortProperty} {(_sortAscending ? "↑" : "↓")}";
        ApplyFilters();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentPath))
        {
            var parent = Path.GetDirectoryName(_currentPath);
            _currentPath = parent ?? "";
            LoadFiles();
        }
    }

    private void FileListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileListView.SelectedItem is FileListItem item)
        {
            if (item.IsDirectory)
            {
                _currentPath = item.RelativePath;
                LoadFiles();
            }
            else
            {
                // Open file with default application
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = item.FullPath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(
                        $"Failed to open file: {ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }
    }

    private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShareButton.IsEnabled = FileListView.SelectedItems.Count > 0;
    }

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(_settings.SyncFolderPath, _currentPath);
        if (Directory.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = path,
                UseShellExecute = true
            });
        }
    }

    private void ShareButton_Click(object sender, RoutedEventArgs e)
    {
        if (FileListView.SelectedItem is FileListItem item && !item.IsDirectory)
        {
            try
            {
                var shareLink = _shareLinkService.CreateShareLink(item.RelativePath);
                var clipboardText = _shareLinkService.GetClipboardText(shareLink);
                
                System.Windows.Clipboard.SetText(clipboardText);
                
                System.Windows.MessageBox.Show(
                    $"Share link created and copied to clipboard!\n\n{shareLink.Uri}",
                    "Share Link Created",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Failed to create share link: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }

    #endregion
}
