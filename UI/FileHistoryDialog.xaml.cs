using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Swarm.Core.Helpers;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;

namespace Swarm.UI;

public partial class FileHistoryDialog : Window
{
    private readonly VersioningService _versioningService;
    private readonly ObservableCollection<FileWithVersions> _files = [];
    private string _searchFilter = "";

    public FileHistoryDialog(VersioningService versioningService)
    {
        InitializeComponent();
        _versioningService = versioningService;
        
        FilesListBox.ItemsSource = _files;
        LoadFiles();
    }

    private void LoadFiles()
    {
        _files.Clear();
        var files = _versioningService.GetFilesWithVersions().ToList();

        foreach (var file in files)
        {
            if (!string.IsNullOrWhiteSpace(_searchFilter) && 
                !file.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var versions = _versioningService.GetVersions(file);
            _files.Add(new FileWithVersions
            {
                RelativePath = file,
                VersionCount = versions.Count()
            });
        }

        if (_files.Count > 0 && FilesListBox.SelectedIndex == -1)
        {
            FilesListBox.SelectedIndex = 0;
        }
    }

    private void RefreshVersionList()
    {
        if (FilesListBox.SelectedItem is FileWithVersions selectedFile)
        {
            SelectedFileTitle.Text = Path.GetFileName(selectedFile.RelativePath);
            SelectedFilePath.Text = selectedFile.RelativePath;
            
            var versions = _versioningService.GetVersions(selectedFile.RelativePath).ToList();
            VersionsListBox.ItemsSource = versions;
            
            EmptyVersionsText.Visibility = versions.Any() ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            SelectedFileTitle.Text = "Select a file";
            SelectedFilePath.Text = "";
            VersionsListBox.ItemsSource = null;
            EmptyVersionsText.Visibility = Visibility.Visible;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchFilter = SearchBox.Text;
        LoadFiles();
    }

    private void FilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshVersionList();
    }

    private void ShowVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is VersionInfo version)
        {
            _versioningService.OpenVersionLocation(version);
        }
    }

    private void CompareVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is VersionInfo version)
        {
            // Check if it's a text file
            if (!IsTextFile(version.RelativePath))
            {
                MessageBox.Show(
                    "Visual comparison is only available for text files.\nUse 'Show' to open the version in File Explorer.",
                    "Binary File", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Get the version file path
            var versionFilePath = _versioningService.GetVersionFilePath(version);
            if (versionFilePath == null)
            {
                MessageBox.Show("Version file not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Get current file path
            var currentFilePath = Path.Combine(_versioningService.Settings.SyncFolderPath, version.RelativePath);

            // Open comparison dialog
            var diffDialog = new DiffCompareDialog(currentFilePath, versionFilePath, version)
            {
                Owner = this
            };
            diffDialog.ShowDialog();
        }
    }

    private static bool IsTextFile(string path)
    {
        var textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".markdown", ".json", ".xml", ".yaml", ".yml",
            ".cs", ".csx", ".vb", ".fs", ".fsx",
            ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs",
            ".html", ".htm", ".css", ".scss", ".sass", ".less",
            ".py", ".pyw", ".rb", ".php", ".java", ".kt", ".kts",
            ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hxx",
            ".go", ".rs", ".swift", ".m", ".mm",
            ".sql", ".sh", ".bash", ".ps1", ".psm1", ".psd1",
            ".bat", ".cmd", ".ini", ".cfg", ".conf", ".config",
            ".log", ".csv", ".tsv", ".toml", ".env",
            ".gitignore", ".gitattributes", ".editorconfig",
            ".sln", ".csproj", ".vbproj", ".fsproj", ".props", ".targets",
            ".xaml", ".axaml", ".razor", ".cshtml", ".vbhtml"
        };

        var extension = Path.GetExtension(path);
        return textExtensions.Contains(extension);
    }

    private async void RestoreVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is VersionInfo version)
        {
            var result = MessageBox.Show(
                $"Are you sure you want to restore this version?\nThe current file will be backed up as a new version.",
                "Confirm Restore", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                var success = await _versioningService.RestoreVersionAsync(version);
                if (success)
                {
                    MessageBox.Show("File successfully restored.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    LoadFiles(); // Refresh to see backup version
                    RefreshVersionList();
                }
                else
                {
                    MessageBox.Show("Failed to restore file.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }

    private void DeleteVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is VersionInfo version)
        {
            var result = MessageBox.Show(
                $"Are you sure you want to delete this version permanently?",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                if (_versioningService.DeleteVersion(version))
                {
                    // Refresh view
                    RefreshVersionList();
                    
                    // Update count in left panel
                    var fileItem = _files.FirstOrDefault(f => f.RelativePath == version.RelativePath);
                    if (fileItem != null)
                    {
                        var count = _versioningService.GetVersions(version.RelativePath).Count();
                        if (count == 0)
                        {
                            _files.Remove(fileItem);
                        }
                        else
                        {
                            fileItem.VersionCount = count;
                            // Trigger property changed? Or just reload
                            LoadFiles();
                            FilesListBox.SelectedItem = _files.FirstOrDefault(f => f.RelativePath == version.RelativePath);
                        }
                    }
                }
            }
        }
    }

    private async void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Are you sure you want to delete ALL file versions for ALL files?\nThis cannot be undone.",
            "Confirm Clear All", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            var files = _versioningService.GetFilesWithVersions().ToList();
            foreach(var file in files)
            {
                _versioningService.DeleteAllVersionsForFile(file);
            }
            
            LoadFiles();
            RefreshVersionList();
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        _versioningService.OpenVersionsFolder();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }
}

public class FileWithVersions
{
    public string RelativePath { get; set; } = string.Empty;
    public int VersionCount { get; set; }
}
