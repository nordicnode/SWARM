using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.Linq;
using Swarm.Avalonia.ViewModels;

namespace Swarm.Avalonia.Views;

public partial class PeersView : UserControl
{
    // Store original backgrounds for restoration
    private static readonly IBrush DropTargetHighlight = new SolidColorBrush(Color.FromArgb(40, 76, 175, 80)); // Green tint
    private static readonly IBrush DropTargetBorder = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green border

    public PeersView()
    {
        InitializeComponent();
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (sender is not Border border) return;
        
        // Check if we have files
        var files = e.Data.GetFiles();
        if (files == null || !files.Any())
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        
        // Visual feedback - highlight the drop target
        border.Tag = border.Background; // Store original
        border.Background = DropTargetHighlight;
        border.BorderBrush = DropTargetBorder;
        border.BorderThickness = new Thickness(2);
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is not Border border) return;
        
        // Restore original appearance
        if (border.Tag is IBrush originalBg)
        {
            border.Background = originalBg;
        }
        else
        {
            border.Background = Brushes.Transparent;
        }
        border.BorderBrush = Brushes.Transparent;
        border.BorderThickness = new Thickness(0);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        // Reset visual state first
        if (sender is Border border)
        {
            if (border.Tag is IBrush originalBg)
            {
                border.Background = originalBg;
            }
            else
            {
                border.Background = Brushes.Transparent;
            }
            border.BorderBrush = Brushes.Transparent;
            border.BorderThickness = new Thickness(0);
        }

        var files = e.Data.GetFiles()?.Select(f => f.Path.LocalPath).ToList();
        if (files == null || !files.Any()) return;

        // Find the peer from the DataContext
        var peerItem = (sender as Control)?.DataContext as PeerItemViewModel 
                       ?? (sender as Control)?.Parent?.DataContext as PeerItemViewModel;
        
        if (peerItem != null && DataContext is PeersViewModel viewModel)
        {
            await viewModel.SendFiles(peerItem, files);
        }
    }
}

