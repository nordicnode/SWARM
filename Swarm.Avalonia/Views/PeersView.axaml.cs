using Avalonia.Controls;
using Avalonia.Input;
using System.Linq;
using Swarm.Avalonia.ViewModels;

namespace Swarm.Avalonia.Views;

public partial class PeersView : UserControl
{
    public PeersView()
    {
        InitializeComponent();
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.Data.GetFiles()?.Select(f => f.Path.LocalPath).ToList();
        if (files == null || !files.Any()) return;

        if (sender is Control control && 
            control.DataContext is PeerItemViewModel peerItem && 
            DataContext is PeersViewModel viewModel)
        {
            await viewModel.SendFiles(peerItem, files);
        }
    }
}
