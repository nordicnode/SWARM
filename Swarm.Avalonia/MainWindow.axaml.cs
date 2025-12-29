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
        
        // Register global keyboard shortcuts
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.F5:
                // Refresh - reload current view
                if (vm.FilesVM != null && vm.IsFilesSelected)
                {
                    vm.FilesVM.RefreshCommand?.Execute(null);
                }
                else if (vm.OverviewVM != null && vm.IsOverviewSelected)
                {
                    // Trigger stats recalculation
                    vm.RefreshOverviewCommand?.Execute(null);
                }
                e.Handled = true;
                break;

            case Key.OemComma when ctrl:
                // Ctrl+, = Open Settings
                vm.NavigateToSettingsCommand?.Execute(null);
                e.Handled = true;
                break;

            case Key.Delete:
                // Delete selected file (only in Files view)
                if (vm.IsFilesSelected && vm.FilesVM?.SelectedFile != null)
                {
                    vm.FilesVM.DeleteCommand?.Execute(null);
                    e.Handled = true;
                }
                break;

            case Key.F when ctrl:
                // Ctrl+F = Focus search (in Files view, navigate there first)
                if (!vm.IsFilesSelected)
                {
                    vm.NavigateToFilesCommand?.Execute(null);
                }
                vm.FocusSearchCommand?.Execute(null);
                e.Handled = true;
                break;
        }
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

