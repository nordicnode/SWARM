using Avalonia.Controls;
using Avalonia.Input;
using Swarm.Avalonia.ViewModels;

namespace Swarm.Avalonia.Views;

public partial class FilesView : UserControl
{
    public FilesView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        
        // Subscribe to double-click on DataGrid rows
        if (FilesGrid != null)
        {
            FilesGrid.DoubleTapped += OnRowDoubleTapped;
        }
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is FilesViewModel viewModel && viewModel.OpenCommand.CanExecute(null))
        {
            viewModel.OpenCommand.Execute(null);
        }
    }
}
