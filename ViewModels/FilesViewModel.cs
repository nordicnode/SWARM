using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Swarm.Core.Helpers;
using Swarm.Core.ViewModels;

namespace Swarm.ViewModels;

public class FilesViewModel : BaseViewModel, IDisposable
{
    private readonly Settings _settings;
    private readonly ShareLinkService _shareLinkService;
    private readonly Dispatcher _dispatcher;
    private FileSystemWatcher? _watcher;
    private readonly System.Timers.Timer _debounceTimer;
    private bool _refreshPending;
    
    private string _currentPath = "";
    private string _searchQuery = "";
    private string _currentFilter = "All Files";
    private bool _sortAscending = true;
    private string _sortProperty = "Name";
    private string _currentPathDisplay = "/";
    private bool _canNavigateUp;
    private bool _isEmpty;
    private string _emptyStateMessage = "";
    private string _statusText = "0 items";
    private string _totalSizeText = "0 B";
    private string _syncStatusText = "Synced";

    public string SyncStatusText
    {
        get => _syncStatusText;
        set => SetProperty(ref _syncStatusText, value);
    }

    private readonly ObservableCollection<FileListItem> _allFiles = new();
    public ObservableCollection<FileListItem> Files { get; } = new();

    public string SearchQuery
    {
        get => _searchQuery;
        set { if (SetProperty(ref _searchQuery, value)) ApplyFilters(); }
    }

    public string CurrentFilter
    {
        get => _currentFilter;
        set { if (SetProperty(ref _currentFilter, value)) ApplyFilters(); }
    }

    public string SortPropertyDisplay => $"{_sortProperty} {(_sortAscending ? "↑" : "↓")}";

    public string CurrentPathDisplay
    {
        get => _currentPathDisplay;
        set => SetProperty(ref _currentPathDisplay, value);
    }

    public bool CanNavigateUp
    {
        get => _canNavigateUp;
        set => SetProperty(ref _canNavigateUp, value);
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        set => SetProperty(ref _isEmpty, value);
    }

    public string EmptyStateMessage
    {
        get => _emptyStateMessage;
        set => SetProperty(ref _emptyStateMessage, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string TotalSizeText
    {
        get => _totalSizeText;
        set => SetProperty(ref _totalSizeText, value);
    }

    public ICommand NavigateUpCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ToggleSortCommand { get; }
    public ICommand OpenItemCommand { get; }
    public ICommand OpenInExplorerCommand { get; }
    public ICommand CreateShareLinkCommand { get; }

    public FilesViewModel(Settings settings, ShareLinkService shareLinkService)
    {
        _settings = settings;
        _shareLinkService = shareLinkService;
        _dispatcher = Dispatcher.CurrentDispatcher;
        
        // Setup debounce timer (300ms delay to batch rapid changes)
        _debounceTimer = new System.Timers.Timer(300);
        _debounceTimer.AutoReset = false;
        _debounceTimer.Elapsed += (s, e) => _dispatcher.Invoke(() =>
        {
            if (_refreshPending)
            {
                _refreshPending = false;
                LoadFiles();
                SyncStatusText = "Syncing...";
                // Reset status after a short delay
                Task.Delay(1000).ContinueWith(_ => _dispatcher.Invoke(() => SyncStatusText = "Synced"));
            }
        });

        NavigateUpCommand = new RelayCommand(_ => NavigateUp(), _ => CanNavigateUp);
        RefreshCommand = new RelayCommand(_ => LoadFiles());
        ToggleSortCommand = new RelayCommand(_ => ToggleSort());
        OpenItemCommand = new RelayCommand(OpenItem);
        OpenInExplorerCommand = new RelayCommand(_ => OpenInExplorer());
        CreateShareLinkCommand = new RelayCommand(CreateShareLink);

        LoadFiles();
        InitializeFileWatcher();
    }
    
    private void InitializeFileWatcher()
    {
        try
        {
            if (!Directory.Exists(_settings.SyncFolderPath)) return;
            
            _watcher = new FileSystemWatcher(_settings.SyncFolderPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | 
                               NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };
            
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Changed += OnFileChanged;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FilesView] Failed to initialize file watcher: {ex.Message}");
        }
    }
    
    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        RequestRefresh();
    }
    
    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        RequestRefresh();
    }
    
    private void RequestRefresh()
    {
        _refreshPending = true;
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }
    
    public void Dispose()
    {
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
    }

    public void LoadFiles()
    {
        _allFiles.Clear();
        Files.Clear();

        var basePath = Path.Combine(_settings.SyncFolderPath, _currentPath);

        if (!Directory.Exists(basePath))
        {
            ShowEmptyState("Sync folder not found");
            return;
        }

        try
        {
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
        var filtered = _allFiles.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(_searchQuery))
        {
            filtered = filtered.Where(f => f.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
        }

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

        var sorted = filtered.OrderByDescending(f => f.IsDirectory);

        sorted = _sortProperty switch
        {
            "Name" => _sortAscending ? sorted.ThenBy(f => f.Name) : sorted.ThenByDescending(f => f.Name),
            "Size" => _sortAscending ? sorted.ThenBy(f => f.Size) : sorted.ThenByDescending(f => f.Size),
            "Modified" => _sortAscending ? sorted.ThenBy(f => f.Modified) : sorted.ThenByDescending(f => f.Modified),
            _ => sorted.ThenBy(f => f.Name)
        };

        Files.Clear();
        foreach (var item in sorted)
        {
            Files.Add(item);
        }

        if (Files.Count == 0)
        {
            ShowEmptyState(_searchQuery.Length > 0 ? "No matching files" : "Folder is empty");
        }
        else
        {
            IsEmpty = false;
        }

        UpdateStatusBar();
    }

    private void NavigateUp()
    {
        if (!string.IsNullOrEmpty(_currentPath))
        {
            var parent = Path.GetDirectoryName(_currentPath);
            _currentPath = parent ?? "";
            LoadFiles();
        }
    }

    private void ToggleSort()
    {
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

        OnPropertyChanged(nameof(SortPropertyDisplay));
        ApplyFilters();
    }

    private void OpenItem(object? parameter)
    {
        if (parameter is FileListItem item)
        {
            if (item.IsDirectory)
            {
                _currentPath = item.RelativePath;
                LoadFiles();
            }
            else
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = item.FullPath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Failed to open file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    private void OpenInExplorer()
    {
        var path = Path.Combine(_settings.SyncFolderPath, _currentPath);
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = path,
                UseShellExecute = true
            });
        }
    }

    private void CreateShareLink(object? parameter)
    {
        if (parameter is FileListItem item && !item.IsDirectory)
        {
            try
            {
                var shareLink = _shareLinkService.CreateShareLink(item.RelativePath);
                var clipboardText = _shareLinkService.GetClipboardText(shareLink);
                
                System.Windows.Clipboard.SetText(clipboardText);
                
                System.Windows.MessageBox.Show($"Share link created and copied to clipboard!\n\n{shareLink.Uri}", "Share Link Created", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to create share link: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void UpdateBreadcrumb()
    {
        CurrentPathDisplay = string.IsNullOrEmpty(_currentPath) ? "/" : "/" + _currentPath.Replace("\\", "/");
        CanNavigateUp = !string.IsNullOrEmpty(_currentPath);
        RelayCommand.RaiseGlobalCanExecuteChanged();
    }

    private void UpdateStatusBar()
    {
        var fileCount = Files.Count(f => !f.IsDirectory);
        var folderCount = Files.Count(f => f.IsDirectory);
        var totalSize = Files.Where(f => !f.IsDirectory).Sum(f => f.Size);

        var countText = new List<string>();
        if (folderCount > 0) countText.Add($"{folderCount} folder{(folderCount == 1 ? "" : "s")}");
        if (fileCount > 0) countText.Add($"{fileCount} file{(fileCount == 1 ? "" : "s")}");

        StatusText = countText.Count > 0 ? string.Join(", ", countText) : "Empty";
        TotalSizeText = FileHelpers.FormatBytes(totalSize);
    }

    private void ShowEmptyState(string hint)
    {
        IsEmpty = true;
        EmptyStateMessage = hint;
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
}

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
    public System.Windows.Media.Brush IconBrush => IsDirectory 
        ? new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F59E0B"))
        : new SolidColorBrush(GetFileTypeColor());
    
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
        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex);
    }
}
