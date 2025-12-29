using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Swarm.Avalonia.ViewModels;

namespace Swarm.Avalonia.Views;

public partial class FileHistoryDialog : Window
{
    public FileHistoryDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is FileHistoryViewModel vm)
        {
            vm.CloseAction = (result) =>
            {
                Close(result);
            };
            vm.ParentWindow = this;
        }
    }
}
