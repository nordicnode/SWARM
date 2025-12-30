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
            // Number keys for navigation (1-5)
            case Key.D1 or Key.NumPad1:
                vm.NavigateToOverviewCommand?.Execute(null);
                e.Handled = true;
                break;
            case Key.D2 or Key.NumPad2:
                vm.NavigateToFilesCommand?.Execute(null);
                e.Handled = true;
                break;
            case Key.D3 or Key.NumPad3:
                vm.NavigateToPeersCommand?.Execute(null);
                e.Handled = true;
                break;
            case Key.D4 or Key.NumPad4:
                vm.NavigateToBandwidthCommand?.Execute(null);
                e.Handled = true;
                break;
            case Key.D5 or Key.NumPad5:
                vm.NavigateToStatsCommand?.Execute(null);
                e.Handled = true;
                break;
            case Key.D6 or Key.NumPad6:
                vm.NavigateToSettingsCommand?.Execute(null);
                e.Handled = true;
                break;

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

            case Key.Space:
                // Space = Pause/Resume sync
                vm.ToggleSyncCommand?.Execute(null);
                e.Handled = true;
                break;

            case Key.F1:
            case Key.OemQuestion: // ? key
                // Show keyboard shortcuts help
                ShowKeyboardShortcuts();
                e.Handled = true;
                break;
        }
    }

    private async void ShowKeyboardShortcuts()
    {
        var dialog = new Dialogs.KeyboardShortcutsDialog();
        await dialog.ShowDialog(this);
    }

    private void HelpButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowKeyboardShortcuts();
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

    private void ResumeSync_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Settings.ResumeSync();
            // Log resume event
            vm.ToastService.Show("Sync Resumed", "Sync has been resumed.", global::Avalonia.Controls.Notifications.NotificationType.Information);
        }
    }
}

