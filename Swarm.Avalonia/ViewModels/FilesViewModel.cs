using System.Collections.ObjectModel;

namespace Swarm.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the Files browser view.
/// </summary>
public class FilesViewModel : ViewModelBase
{
    private ObservableCollection<FileItemViewModel> _files = new();
    private FileItemViewModel? _selectedFile;
    private string _currentPath = "";

    public FilesViewModel()
    {
        // TODO: Initialize with actual data from Swarm.Core services
        CurrentPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    public ObservableCollection<FileItemViewModel> Files
    {
        get => _files;
        set => SetProperty(ref _files, value);
    }

    public FileItemViewModel? SelectedFile
    {
        get => _selectedFile;
        set => SetProperty(ref _selectedFile, value);
    }

    public string CurrentPath
    {
        get => _currentPath;
        set => SetProperty(ref _currentPath, value);
    }
}

/// <summary>
/// ViewModel for a single file item in the browser.
/// </summary>
public class FileItemViewModel : ViewModelBase
{
    public required string Name { get; set; }
    public required string FullPath { get; set; }
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public string SizeDisplay => IsDirectory ? "" : FormatSize(Size);

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }
}
