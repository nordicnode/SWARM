namespace Swarm.UI.Views;

public partial class FilesView : System.Windows.Controls.UserControl
{
    public FilesView()
    {
        InitializeComponent();
    }

    private void ListBoxItem_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBoxItem item && DataContext is ViewModels.FilesViewModel vm)
        {
            vm.OpenItemCommand.Execute(item.DataContext);
        }
    }
}
