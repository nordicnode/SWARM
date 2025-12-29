using Avalonia.Controls;
using Swarm.Avalonia.ViewModels;

namespace Swarm.Avalonia.Dialogs;

public partial class DiffCompareDialog : Window
{
    public DiffCompareDialog()
    {
        InitializeComponent();
    }

    public DiffCompareDialog(DiffCompareViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.CloseAction = (result) => Close(result);
    }
}
