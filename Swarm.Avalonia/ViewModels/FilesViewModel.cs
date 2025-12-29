using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Swarm.Core.Helpers;
using Swarm.Core.ViewModels;
using Material.Icons;

namespace Swarm.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the Files view (file browser).
/// </summary>
public class FilesViewModel : ViewModelBase, IDisposable
{
    private readonly Settings _settings = null!;
    private readonly SyncService? _syncService;
    private readonly VersioningService? _versioningService;
    private readonly FolderEncryptionService? _folderEncryptionService;
    private readonly ILogger<FilesViewModel> _logger;
    private readonly System.Timers.Timer _refreshDebounceTimer;

    private string _currentPath = "";
    private ObservableCollection<FileItemViewModel> _files = new();
    private List<FileItemViewModel> _allFiles = new(); // Unfiltered cache
    private FileItemViewModel? _selectedFile;
    private FileSortColumn _sortColumn = FileSortColumn.Name;
    private bool _sortDescending = false;
    private string _searchFilter = "";
    private bool _isLoading;

    public FilesViewModel() {
        // Design-time
        _logger = NullLogger<FilesViewModel>.Instance;
        _refreshDebounceTimer = new System.Timers.Timer(500) { AutoReset = false };
    }

    public FileItemViewModel? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value))
            {
                RelayCommand.RaiseGlobalCanExecuteChanged();
            }
        }
    }

    public string CurrentPath
    {
        get => _currentPath;
        set => SetProperty(ref _currentPath, value);
    }

    public ObservableCollection<FileItemViewModel> Files
    {
        get => _files;
        set => SetProperty(ref _files, value);
    }

    public bool IsEmpty => Files.Count == 0 && !IsLoading;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public string SearchFilter
    {
        get => _searchFilter;
        set
        {
            if (SetProperty(ref _searchFilter, value))
            {
                ApplyFilter();
            }
        }
    }

    private ObservableCollection<PathSegment> _pathSegments = new();
    public ObservableCollection<PathSegment> PathSegments
    {
        get => _pathSegments;
        set => SetProperty(ref _pathSegments, value);
    }

    public ICommand NavigateUpCommand { get; } = null!;
    public ICommand OpenCommand { get; } = null!;
    public ICommand ShowInExplorerCommand { get; } = null!;
    public ICommand DeleteCommand { get; } = null!;
    public ICommand RefreshCommand { get; } = null!;
    public ICommand NavigateToPathCommand { get; } = null!;
    public ICommand SortByCommand { get; } = null!;
    public ICommand ViewHistoryCommand { get; } = null!;

    public FileSortColumn SortColumn
    {
        get => _sortColumn;
        set
        {
            if (SetProperty(ref _sortColumn, value))
            {
                LoadFiles();
            }
        }
    }

    public bool SortDescending
    {
        get => _sortDescending;
        set
        {
            if (SetProperty(ref _sortDescending, value))
            {
                LoadFiles();
            }
        }
    }

    public FilesViewModel(Settings settings, SyncService? syncService = null, VersioningService? versioningService = null, FolderEncryptionService? folderEncryptionService = null)
    {
        _settings = settings;
        _syncService = syncService;
        _versioningService = versioningService;
        _folderEncryptionService = folderEncryptionService;

        // Initialize debounce timer for auto-refresh (500ms)
        _refreshDebounceTimer = new System.Timers.Timer(500) { AutoReset = false };
        _refreshDebounceTimer.Elapsed += (s, e) => 
        {
            Dispatcher.UIThread.Post(() => LoadFiles());
        };

        // Subscribe to sync events for auto-refresh
        if (_syncService != null)
        {
            _syncService.FileChanged += OnSyncFileChanged;
        }

        CurrentPath = _settings.SyncFolderPath;
        
        NavigateToPathCommand = new RelayCommand<string>(path =>
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                CurrentPath = path;
                LoadFiles();
            }
        });

        LoadFiles();

        NavigateUpCommand = new RelayCommand(NavigateUp, CanNavigateUp);
        OpenCommand = new RelayCommand(Open, CanOpen);
        ShowInExplorerCommand = new RelayCommand(ShowInExplorer, CanFileAction);
        DeleteCommand = new RelayCommand(DeleteFile, CanFileAction);
        RefreshCommand = new RelayCommand(Refresh);
        ViewHistoryCommand = new RelayCommand(ViewHistory, CanViewHistory);
        SortByCommand = new RelayCommand<string>(column =>
        {
            if (Enum.TryParse<FileSortColumn>(column, out var sortCol))
            {
                if (_sortColumn == sortCol)
                {
                    SortDescending = !SortDescending;
                }
                else
                {
                    _sortDescending = false;
                    SortColumn = sortCol;
                }
            }
        });
        
        CreateEncryptedFolderCommand = new RelayCommand(CreateEncryptedFolder);
        UnlockFolderCommand = new RelayCommand(UnlockCurrentFolder, CanUnlockCurrentFolder);
        LockFolderCommand = new RelayCommand(LockCurrentFolder, CanLockCurrentFolder);
    }

    private void OnSyncFileChanged(Swarm.Core.Models.SyncedFile syncedFile)
    {
        // Debounce refresh to prevent excessive reloading during bulk sync
        _refreshDebounceTimer.Stop();
        _refreshDebounceTimer.Start();
    }

    /// <summary>
    /// Refreshes the file list (called via F5).
    /// </summary>
    private void Refresh()
    {
        LoadFiles();
    }


    private void LoadFiles()
    {
        try
        {
            IsLoading = true;
            if (!Directory.Exists(CurrentPath)) return;

            var items = new List<FileItemViewModel>();

            // Directories
            foreach (var dir in Directory.GetDirectories(CurrentPath))
            {
                var info = new DirectoryInfo(dir);
                items.Add(new FileItemViewModel
                {
                    Name = info.Name,
                    Path = info.FullName,
                    IsDirectory = true,
                    Size = "",
                    SizeBytes = 0,
                    Modified = info.LastWriteTime.ToString("g"),
                    ModifiedDate = info.LastWriteTime
                });
            }

            // Files
            foreach (var file in Directory.GetFiles(CurrentPath))
            {
                var info = new FileInfo(file);
                items.Add(new FileItemViewModel
                {
                    Name = info.Name,
                    Path = info.FullName,
                    IsDirectory = false,
                    Size = FileHelpers.FormatBytes(info.Length),
                    SizeBytes = info.Length,
                    Modified = info.LastWriteTime.ToString("g"),
                    ModifiedDate = info.LastWriteTime
                });
            }

            // Cache all items for filtering
            _allFiles = items;
            
            // Apply filter and sorting
            ApplyFilter();
            
            UpdatePathSegments();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load files from {Path}", CurrentPath);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        IEnumerable<FileItemViewModel> filtered = _allFiles;

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(_searchFilter))
        {
            var filter = _searchFilter.Trim();
            filtered = filtered.Where(f => 
                f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        // Apply sorting - directories first, then sort within each group
        var dirs = filtered.Where(i => i.IsDirectory);
        var files = filtered.Where(i => !i.IsDirectory);

        dirs = ApplySorting(dirs);
        files = ApplySorting(files);

        Files = new ObservableCollection<FileItemViewModel>(dirs.Concat(files));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void UpdatePathSegments()
    {
        var segments = new ObservableCollection<PathSegment>();
        var rootPath = _settings.SyncFolderPath;
        
        // Add Root
        segments.Add(new PathSegment { Name = "Home", Path = rootPath });

        if (CurrentPath != rootPath && CurrentPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            var relative = CurrentPath.Substring(rootPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.IsNullOrEmpty(relative))
            {
                var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
                var currentBuilder = rootPath;
                foreach (var part in parts)
                {
                    currentBuilder = Path.Combine(currentBuilder, part);
                    segments.Add(new PathSegment { Name = part, Path = currentBuilder });
                }
            }
        }
        
        PathSegments = segments;
    }

    private bool CanNavigateUp()
    {
        return !string.IsNullOrEmpty(CurrentPath) &&
               Path.GetFullPath(CurrentPath) != Path.GetFullPath(_settings.SyncFolderPath);
    }

    private void NavigateUp()
    {
        var parent = Directory.GetParent(CurrentPath);
        if (parent != null)
        {
            CurrentPath = parent.FullName;
            LoadFiles();
        }
    }

    private bool CanOpen() => SelectedFile != null;

    private void Open()
    {
        if (SelectedFile == null) return;
        
        if (SelectedFile.IsDirectory)
        {
            CurrentPath = SelectedFile.Path;
            LoadFiles();
        }
        else
        {
            // Open file with default application
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = SelectedFile.Path,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to open file: {ex.Message}");
            }
        }
    }

    private bool CanFileAction() => SelectedFile != null;

    private void ShowInExplorer()
    {
        if (SelectedFile == null) return;
        
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{SelectedFile.Path}\"");
            }
            else if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", $"-R \"{SelectedFile.Path}\"");
            }
            else if (OperatingSystem.IsLinux())
            {
                // Try xdg-open on parent dir
                var parent = Directory.GetParent(SelectedFile.Path)?.FullName;
                if (parent != null)
                {
                    System.Diagnostics.Process.Start("xdg-open", parent);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show file in explorer: {Path}", SelectedFile?.Path);
        }
    }

    private bool CanViewHistory() => SelectedFile != null && !SelectedFile.IsDirectory && _versioningService != null;

    private void ViewHistory()
    {
        if (SelectedFile == null || _versioningService == null) return;
        
        var relativePath = Path.GetRelativePath(_settings.SyncFolderPath, SelectedFile.Path);
        
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var mainWindow = global::Avalonia.Application.Current?.ApplicationLifetime is 
                    global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow
                    : null;
                    
                if (mainWindow == null) return;

                var vm = new FileHistoryViewModel(SelectedFile.Path, relativePath, _versioningService);
                var dialog = new Swarm.Avalonia.Views.FileHistoryDialog
                {
                    DataContext = vm
                };
                
                var result = await dialog.ShowDialog<bool>(mainWindow);
                if (result)
                {
                    // If restored, refresh file list
                    LoadFiles();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open history for {Path}", SelectedFile.Path);
            }
        });
    }

    private void DeleteFile()
    {
        if (SelectedFile == null) return;

        // Use Dispatcher to show async dialog from sync command
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var mainWindow = global::Avalonia.Application.Current?.ApplicationLifetime is 
                    global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                    ? desktop.MainWindow
                    : null;
                    
                if (mainWindow == null) return;

                var itemType = SelectedFile.IsDirectory ? "folder" : "file";
                var dialog = new Swarm.Avalonia.Dialogs.ConfirmationDialog();
                dialog.SetTitle($"Delete {itemType}?");
                dialog.SetMessage($"Are you sure you want to delete \"{SelectedFile.Name}\"?\n\nThis action cannot be undone.");
                dialog.SetConfirmButton("Delete", isDestructive: true);
                
                var result = await dialog.ShowDialog<bool>(mainWindow);
                if (!result) return;

                // Proceed with deletion
                if (SelectedFile.IsDirectory)
                    Directory.Delete(SelectedFile.Path, true);
                else
                    File.Delete(SelectedFile.Path);

                LoadFiles(); // Refresh
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete: {Path}", SelectedFile?.Path);
            }
        });
    }

    #region Encrypted Folders

    public ICommand CreateEncryptedFolderCommand { get; private set; } = null!;
    public ICommand UnlockFolderCommand { get; private set; } = null!;
    public ICommand LockFolderCommand { get; private set; } = null!;

    /// <summary>
    /// Gets whether the current folder is an encrypted folder.
    /// </summary>
    public bool IsCurrentFolderEncrypted => GetCurrentEncryptedFolder() != null;

    /// <summary>
    /// Gets whether the current encrypted folder is locked.
    /// </summary>
    public bool IsCurrentFolderLocked => GetCurrentEncryptedFolder()?.IsLocked ?? false;

    private EncryptedFolder? GetCurrentEncryptedFolder()
    {
        if (_folderEncryptionService == null) return null;
        
        var relativePath = GetRelativePath(CurrentPath);
        return _settings.EncryptedFolders.FirstOrDefault(f => 
            relativePath.StartsWith(f.FolderPath, StringComparison.OrdinalIgnoreCase) ||
            f.FolderPath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));
    }

    private string GetRelativePath(string fullPath)
    {
        if (fullPath.StartsWith(_settings.SyncFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            var relative = fullPath.Substring(_settings.SyncFolderPath.Length).TrimStart(Path.DirectorySeparatorChar);
            return string.IsNullOrEmpty(relative) ? "" : relative;
        }
        return fullPath;
    }

    private async void CreateEncryptedFolder()
    {
        if (_folderEncryptionService == null) return;

        try
        {
            var mainWindow = GetMainWindow();
            if (mainWindow == null) return;

            var dialog = new Dialogs.EncryptedFolderDialog(_folderEncryptionService);
            await dialog.ShowDialog(mainWindow);
            
            if (dialog.Success)
            {
                LoadFiles();
                OnPropertyChanged(nameof(IsCurrentFolderEncrypted));
                OnPropertyChanged(nameof(IsCurrentFolderLocked));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create encrypted folder");
        }
    }

    private bool CanUnlockCurrentFolder() => GetCurrentEncryptedFolder()?.IsLocked == true;

    private async void UnlockCurrentFolder()
    {
        if (_folderEncryptionService == null) return;
        
        var folder = GetCurrentEncryptedFolder();
        if (folder == null || !folder.IsLocked) return;

        try
        {
            var mainWindow = GetMainWindow();
            if (mainWindow == null) return;

            var dialog = new Dialogs.EncryptedFolderDialog(_folderEncryptionService, folder);
            await dialog.ShowDialog(mainWindow);
            
            if (dialog.Success)
            {
                LoadFiles();
                OnPropertyChanged(nameof(IsCurrentFolderLocked));
                RelayCommand.RaiseGlobalCanExecuteChanged();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unlock folder");
        }
    }

    private bool CanLockCurrentFolder() => GetCurrentEncryptedFolder()?.IsLocked == false;

    private void LockCurrentFolder()
    {
        var folder = GetCurrentEncryptedFolder();
        if (folder == null || folder.IsLocked) return;

        _folderEncryptionService?.LockFolder(folder.FolderPath);
        LoadFiles();
        OnPropertyChanged(nameof(IsCurrentFolderLocked));
        RelayCommand.RaiseGlobalCanExecuteChanged();
    }

    private static global::Avalonia.Controls.Window? GetMainWindow()
    {
        return global::Avalonia.Application.Current?.ApplicationLifetime is 
            global::Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
    }

    #endregion

    public void Dispose()
    {
        _refreshDebounceTimer?.Stop();
        _refreshDebounceTimer?.Dispose();
        
        if (_syncService != null)
        {
            _syncService.FileChanged -= OnSyncFileChanged;
        }
    }

    private IEnumerable<FileItemViewModel> ApplySorting(IEnumerable<FileItemViewModel> items)
    {
        return _sortColumn switch
        {
            FileSortColumn.Name => _sortDescending 
                ? items.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase) 
                : items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            FileSortColumn.Size => _sortDescending 
                ? items.OrderByDescending(i => i.SizeBytes) 
                : items.OrderBy(i => i.SizeBytes),
            FileSortColumn.Modified => _sortDescending 
                ? items.OrderByDescending(i => i.ModifiedDate) 
                : items.OrderBy(i => i.ModifiedDate),
            FileSortColumn.Extension => _sortDescending 
                ? items.OrderByDescending(i => i.Extension, StringComparer.OrdinalIgnoreCase) 
                : items.OrderBy(i => i.Extension, StringComparer.OrdinalIgnoreCase),
            _ => items
        };
    }
}

public class FileItemViewModel : ViewModelBase
{
    public required string Name { get; set; }
    public required string Path { get; set; }
    public bool IsDirectory { get; set; }
    public required string Size { get; set; }
    public long SizeBytes { get; set; }
    public required string Modified { get; set; }
    public DateTime ModifiedDate { get; set; }
    
    // Display extension (e.g., "EXE", "PNG") or "DIR" for directories
    public string Extension => IsDirectory 
        ? "DIR" 
        : (System.IO.Path.GetExtension(Name)?.TrimStart('.').ToUpperInvariant() ?? "FILE");
}

public class PathSegment
{
    public required string Name { get; set; }
    public required string Path { get; set; }
}

public enum FileSortColumn
{
    Name,
    Size,
    Modified,
    Extension
}
