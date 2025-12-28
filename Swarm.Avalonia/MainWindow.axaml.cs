using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Swarm.Avalonia.ViewModels;

namespace Swarm.Avalonia;

public partial class MainWindow : Window
{
    private bool _isClosingFromTray;

    public MainWindow()
    {
        InitializeComponent();

        // Note: DataContext is set in App.axaml.cs to ensure service injection
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized 
            ? WindowState.Normal 
            : WindowState.Maximized;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // If we have a view model, check settings
        if (DataContext is MainViewModel vm && !_isClosingFromTray)
        {
            // Check CloseToTray setting
            if (vm.Settings.CloseToTray)
            {
                e.Cancel = true;
                Hide();
            }
        }

        base.OnClosing(e);
    }

    public void ForceClose()
    {
        _isClosingFromTray = true;
        Close();
    }
}
