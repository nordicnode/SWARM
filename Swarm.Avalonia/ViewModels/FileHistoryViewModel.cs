using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Swarm.Core.ViewModels;
using Swarm.Core.Helpers;
using Swarm.Avalonia.Dialogs;

namespace Swarm.Avalonia.ViewModels;

public class FileHistoryViewModel : ViewModelBase
{
    private readonly VersioningService _versioningService;
    private readonly string _filePath;
    private readonly string _relativePath;
    private ObservableCollection<VersionInfo> _versions = new();
    private VersionInfo? _selectedVersion;
    private bool _isLoading;

    public FileHistoryViewModel(string filePath, string relativePath, VersioningService versioningService)
    {
        _filePath = filePath;
        _relativePath = relativePath;
        _versioningService = versioningService;

        LoadVersions();

        RestoreCommand = new RelayCommand(RestoreVersion, CanRestore);
        DeleteCommand = new RelayCommand(DeleteVersion, CanDelete);
        OpenLocationCommand = new RelayCommand(OpenLocation, CanOpenLocation);
        CompareCommand = new RelayCommand(CompareWithCurrent, CanCompare);
        CloseCommand = new RelayCommand<object>(Close);
    }

    public string FileName => System.IO.Path.GetFileName(_filePath);
    public string RelativePath => _relativePath;

    public ObservableCollection<VersionInfo> Versions
    {
        get => _versions;
        set => SetProperty(ref _versions, value);
    }

    public VersionInfo? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                RelayCommand.RaiseGlobalCanExecuteChanged();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public ICommand RestoreCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenLocationCommand { get; }
    public ICommand CompareCommand { get; }
    public ICommand CloseCommand { get; }

    // Action to close the dialog, set by the view
    public Action<bool>? CloseAction { get; set; }

    private void LoadVersions()
    {
        IsLoading = true;
        Task.Run(() =>
        {
            var versions = _versioningService.GetVersions(_relativePath);
            
            Dispatcher.UIThread.Post(() =>
            {
                Versions = new ObservableCollection<VersionInfo>(versions);
                IsLoading = false;
            });
        });
    }

    private bool CanRestore() => SelectedVersion != null;

    private async void RestoreVersion()
    {
        if (SelectedVersion == null) return;

        IsLoading = true;
        var success = await _versioningService.RestoreVersionAsync(SelectedVersion);
        IsLoading = false;

        if (success)
        {
            // Close with success result
            CloseAction?.Invoke(true);
        }
        else
        {
            // TODO: Show error
        }
    }

    private bool CanDelete() => SelectedVersion != null;

    private void DeleteVersion()
    {
        if (SelectedVersion == null) return;

        if (_versioningService.DeleteVersion(SelectedVersion))
        {
            Versions.Remove(SelectedVersion);
            SelectedVersion = null;
        }
    }

    private bool CanOpenLocation() => SelectedVersion != null;

    private void OpenLocation()
    {
        if (SelectedVersion != null)
        {
            _versioningService.OpenVersionLocation(SelectedVersion);
        }
    }

    private void Close(object? parameter)
    {
        CloseAction?.Invoke(false);
    }

    private bool CanCompare() => SelectedVersion != null && File.Exists(_filePath);

    private async void CompareWithCurrent()
    {
        if (SelectedVersion == null) return;

        try
        {
            // Read version file
            var versionFilePath = _versioningService.GetVersionFilePath(SelectedVersion);
            if (versionFilePath == null || !File.Exists(versionFilePath))
            {
                return;
            }

            var oldContent = await File.ReadAllTextAsync(versionFilePath);
            
            // Read current file
            var currentContent = File.Exists(_filePath) 
                ? await File.ReadAllTextAsync(_filePath) 
                : "";

            var leftTitle = $"Version from {SelectedVersion.CreatedAtDisplay}";
            var rightTitle = "Current Version";

            var viewModel = new DiffCompareViewModel(oldContent, currentContent, leftTitle, rightTitle);
            var dialog = new DiffCompareDialog(viewModel);

            if (ParentWindow != null)
            {
                await dialog.ShowDialog(ParentWindow);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to compare versions");
        }
    }

    // Set by the view for launching dialogs
    public global::Avalonia.Controls.Window? ParentWindow { get; set; }
}
