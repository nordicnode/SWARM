using System.Windows;
using System.Windows.Media;
using Swarm.ViewModels;

namespace Swarm.UI.Views;

public partial class PeersView : System.Windows.Controls.UserControl
{
    public PeersView()
    {
        InitializeComponent();
    }

    private void DropZone_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            if (DataContext is PeersViewModel vm)
            {
                string[] files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
                // Always try to execute - the command will handle the peer selection logic
                vm.SendFilesCommand.Execute(files);
            }
        }
        
        // Reset styling
        ResetDropZoneStyle();
        e.Handled = true;
    }

    private void DropZone_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            
            // Highlight drop zone
            DropZone.BorderThickness = new Thickness(3);
            DropZone.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(20, 45, 212, 191)); // Accent color with alpha
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        ResetDropZoneStyle();
        e.Handled = true;
    }
    
    private void ResetDropZoneStyle()
    {
        DropZone.BorderThickness = new Thickness(2);
        DropZone.Background = System.Windows.Media.Brushes.Transparent;
    }
}
