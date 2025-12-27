using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Swarm.Models;
using Color = System.Windows.Media.Color;

namespace Swarm.UI;

/// <summary>
/// Dialog to display integrity check results.
/// </summary>
public partial class IntegrityResultDialog : Window
{
    public IntegrityResultDialog(IntegrityResult result)
    {
        InitializeComponent();
        DisplayResult(result);
    }

    private void DisplayResult(IntegrityResult result)
    {
        // Summary stats
        HealthyCountText.Text = result.HealthyFiles.ToString();
        CorruptedCountText.Text = result.CorruptedFiles.Count.ToString();
        MissingCountText.Text = result.MissingFiles.Count.ToString();

        // Duration
        DurationText.Text = $"Checked {result.TotalFiles} files in {result.Duration.TotalSeconds:F1}s";

        // Status
        if (!result.CompletedSuccessfully)
        {
            SetErrorStatus(result.ErrorMessage ?? "Check failed");
        }
        else if (result.IsAllHealthy)
        {
            SetSuccessStatus();
        }
        else
        {
            SetWarningStatus(result);
        }

        // Show issues if any
        if (result.CorruptedFiles.Count > 0 || result.MissingFiles.Count > 0)
        {
            IssuesPanel.Visibility = Visibility.Visible;
            IssuesList.ItemsSource = result.CorruptedFiles;
            MissingFilesList.ItemsSource = result.MissingFiles;
        }
    }

    private void SetSuccessStatus()
    {
        StatusBorder.Background = new SolidColorBrush(Color.FromArgb(0x10, 0x43, 0xA0, 0x47));
        StatusIcon.Data = System.Windows.Media.Geometry.Parse("M9,16.17L4.83,12l-1.42,1.41L9,19L21,7l-1.41-1.41L9,16.17z");
        StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47));
        StatusText.Text = "All files are healthy!";
    }

    private void SetWarningStatus(IntegrityResult result)
    {
        StatusBorder.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xF4, 0x43, 0x36));
        StatusIcon.Data = System.Windows.Media.Geometry.Parse("M1,21h22L12,2L1,21z M13,18h-2v-2h2V18z M13,14h-2v-4h2V14z");
        StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
        
        var issues = result.CorruptedFiles.Count + result.MissingFiles.Count;
        StatusText.Text = $"{issues} issue{(issues != 1 ? "s" : "")} found - review the details below";
    }

    private void SetErrorStatus(string message)
    {
        StatusBorder.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xF4, 0x43, 0x36));
        StatusIcon.Data = System.Windows.Media.Geometry.Parse("M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12z");
        StatusIcon.Fill = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
        StatusText.Text = message;
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
