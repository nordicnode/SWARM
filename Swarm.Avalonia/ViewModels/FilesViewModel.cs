using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Swarm.Core.Helpers;

namespace Swarm.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the Files view (file browser).
/// </summary>
public class FilesViewModel : ViewModelBase
{
    private readonly Settings _settings = null!;
    private readonly ShareLinkService _shareLinkService = null!;

    private string _currentPath = "";
    private ObservableCollection<FileItemViewModel> _files = new();
    private FileItemViewModel? _selectedFile;

    public FilesViewModel() {
        // Design-time
    }

    public FilesViewModel(Settings settings, ShareLinkService shareLinkService)
    {
        _settings = settings;
        _shareLinkService = shareLinkService;

        CurrentPath = _settings.SyncFolderPath;
        LoadFiles();

        NavigateUpCommand = new RelayCommand(NavigateUp, CanNavigateUp);
        OpenCommand = new RelayCommand(Open, CanOpen);
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

    public ICommand NavigateUpCommand { get; } = null!;
    public ICommand OpenCommand { get; } = null!;

    private void LoadFiles()
    {
        try
        {
            if (!Directory.Exists(CurrentPath)) return;

            var items = new ObservableCollection<FileItemViewModel>();

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
                    Modified = info.LastWriteTime.ToString("g")
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
                    Modified = info.LastWriteTime.ToString("g")
                });
            }

            Files = items;
        }
        catch
        {
            // Ignore access errors
        }
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
        if (SelectedFile?.IsDirectory == true)
        {
            CurrentPath = SelectedFile.Path;
            LoadFiles();
        }
    }
}

public class FileItemViewModel : ViewModelBase
{
    public required string Name { get; set; }
    public required string Path { get; set; }
    public bool IsDirectory { get; set; }
    public required string Size { get; set; }
    public required string Modified { get; set; }
    public string IconKind => IsDirectory ? "Folder" : "File";
}
