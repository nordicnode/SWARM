using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Swarm.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace Swarm.UI;

public partial class DiffCompareDialog : Window
{
    private static readonly Brush AddedBackground = new SolidColorBrush(Color.FromArgb(0x22, 0x34, 0xd3, 0x99));
    private static readonly Brush DeletedBackground = new SolidColorBrush(Color.FromArgb(0x22, 0xef, 0x44, 0x44));
    private static readonly Brush ModifiedBackground = new SolidColorBrush(Color.FromArgb(0x22, 0xa1, 0xa1, 0xaa));
    private static readonly Brush UnchangedBackground = Brushes.Transparent;

    private static readonly Brush AddedForeground = new SolidColorBrush(Color.FromRgb(0x34, 0xd3, 0x99));
    private static readonly Brush DeletedForeground = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
    private static readonly Brush ModifiedForeground = new SolidColorBrush(Color.FromRgb(0xa1, 0xa1, 0xaa));
    private static readonly Brush UnchangedForeground = new SolidColorBrush(Color.FromRgb(0xe4, 0xe4, 0xe7));

    public DiffCompareDialog(string currentFilePath, string versionFilePath, VersionInfo version)
    {
        InitializeComponent();

        FilePathText.Text = version.RelativePath;
        VersionInfoText.Text = $"Comparing: Current file vs. Version from {version.CreatedAtDisplay} ({version.Reason})";

        LoadDiff(currentFilePath, versionFilePath);
    }

    private void LoadDiff(string currentFilePath, string versionFilePath)
    {
        try
        {
            var currentText = File.Exists(currentFilePath) ? File.ReadAllText(currentFilePath) : "";
            var versionText = File.Exists(versionFilePath) ? File.ReadAllText(versionFilePath) : "";

            var diffBuilder = new SideBySideDiffBuilder(new Differ());
            var diffModel = diffBuilder.BuildDiffModel(versionText, currentText);

            // Populate version panel (left side - old)
            var versionLines = new List<DiffLine>();
            foreach (var line in diffModel.OldText.Lines)
            {
                versionLines.Add(CreateDiffLine(line));
            }
            VersionDiffPanel.ItemsSource = versionLines;

            // Populate current panel (right side - new)
            var currentLines = new List<DiffLine>();
            foreach (var line in diffModel.NewText.Lines)
            {
                currentLines.Add(CreateDiffLine(line));
            }
            CurrentDiffPanel.ItemsSource = currentLines;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load diff: {ex.Message}");
        }
    }

    private static DiffLine CreateDiffLine(DiffPiece piece)
    {
        var (background, foreground) = piece.Type switch
        {
            ChangeType.Inserted => (AddedBackground, AddedForeground),
            ChangeType.Deleted => (DeletedBackground, DeletedForeground),
            ChangeType.Modified => (ModifiedBackground, ModifiedForeground),
            ChangeType.Imaginary => (UnchangedBackground, ModifiedForeground),
            _ => (UnchangedBackground, UnchangedForeground)
        };

        return new DiffLine
        {
            LineNumber = piece.Position?.ToString() ?? "",
            Text = piece.Text ?? "",
            Background = background,
            Foreground = foreground
        };
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

public class DiffLine
{
    public string LineNumber { get; set; } = "";
    public string Text { get; set; } = "";
    public Brush Background { get; set; } = Brushes.Transparent;
    public Brush Foreground { get; set; } = Brushes.White;
}
