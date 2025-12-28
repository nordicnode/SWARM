using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Swarm.Avalonia.ViewModels;
using System.ComponentModel;

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

    protected override void OnClosing(CancelEventArgs e)
    {
        // If we have a view model, check settings
        if (DataContext is MainViewModel vm && !_isClosingFromTray)
        {
            // If configured to minimize to tray (or just default behavior for this app type)
            // TODO: Add actual setting "CloseToTray" in Settings model
            // For now, let's assume we want to hide if the tray icon is available
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    public void ForceClose()
    {
        _isClosingFromTray = true;
        Close();
    }
}
