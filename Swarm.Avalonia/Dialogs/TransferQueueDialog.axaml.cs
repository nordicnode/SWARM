using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Swarm.Core.Helpers;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Swarm.Core.ViewModels;

namespace Swarm.Avalonia.Dialogs;

public partial class TransferQueueDialog : Window
{
    private readonly TransferQueueViewModel _viewModel;

    public TransferQueueDialog(TransferService transferService)
    {
        InitializeComponent();
        _viewModel = new TransferQueueViewModel(transferService);
        DataContext = _viewModel;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

public class TransferQueueViewModel : INotifyPropertyChanged
{
    private readonly TransferService _transferService;
    
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    
    public ObservableCollection<TransferItemViewModel> Transfers { get; } = new();

    public TransferQueueViewModel(TransferService transferService)
    {
        _transferService = transferService;
        
        ClearCompletedCommand = new RelayCommand(ClearCompleted);
        
        // Subscribe to transfer events
        _transferService.TransferStarted += OnTransferChanged;
        _transferService.TransferProgress += OnTransferChanged;
        _transferService.TransferCompleted += OnTransferChanged;
        _transferService.TransferFailed += OnTransferChanged;
        
        // Initialize with current transfers
        RefreshTransfers();
    }

    public ICommand ClearCompletedCommand { get; }

    public string SummaryText
    {
        get
        {
            var active = Transfers.Count(t => t.IsInProgress);
            var pending = Transfers.Count(t => t.IsPending);
            var failed = Transfers.Count(t => t.IsFailed);
            
            if (active > 0)
                return $"{active} active, {pending} pending, {failed} failed";
            if (Transfers.Count == 0)
                return "No transfers";
            return $"{Transfers.Count} total transfers";
        }
    }

    public bool HasTransfers => Transfers.Count > 0;
    public bool HasCompletedTransfers => Transfers.Any(t => 
        t.Status == TransferStatus.Completed || 
        t.Status == TransferStatus.Failed || 
        t.Status == TransferStatus.Cancelled);

    private void OnTransferChanged(FileTransfer transfer)
    {
        Dispatcher.UIThread.InvokeAsync(RefreshTransfers);
    }

    private void RefreshTransfers()
    {
        Transfers.Clear();
        foreach (var transfer in _transferService.Transfers.OrderByDescending(t => t.StartTime))
        {
            Transfers.Add(new TransferItemViewModel(transfer, _transferService));
        }
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasTransfers));
        OnPropertyChanged(nameof(HasCompletedTransfers));
    }

    private void ClearCompleted()
    {
        _transferService.ClearCompletedTransfers();
        RefreshTransfers();
    }
}

public class TransferItemViewModel : INotifyPropertyChanged
{
    private readonly FileTransfer _transfer;
    private readonly TransferService _transferService;
    
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public TransferItemViewModel(FileTransfer transfer, TransferService transferService)
    {
        _transfer = transfer;
        _transferService = transferService;
        
        CancelCommand = new RelayCommand(Cancel, () => CanCancel);
        RetryCommand = new RelayCommand(async () => await RetryAsync(), () => CanRetry);
    }

    public string FileName => _transfer.FileName;
    public string PeerName => _transfer.RemotePeer?.Name ?? "Unknown";
    public double Progress => _transfer.Progress;
    public string FileSizeDisplay => FileHelpers.FormatBytes(_transfer.FileSize);
    public string SpeedDisplay => _transfer.SpeedDisplay;
    public TransferStatus Status => _transfer.Status;
    public string? ErrorMessage => _transfer.ErrorMessage;
    public bool HasError => !string.IsNullOrEmpty(_transfer.ErrorMessage);

    public bool CanCancel => _transfer.CanCancel;
    public bool CanRetry => _transfer.CanRetry;
    public bool IsInProgress => _transfer.Status == TransferStatus.InProgress;
    public bool IsPending => _transfer.Status == TransferStatus.Pending;
    public bool IsFailed => _transfer.Status == TransferStatus.Failed;
    public bool ShowProgress => Status == TransferStatus.InProgress || Status == TransferStatus.Pending;

    public string DirectionIcon => _transfer.Direction == TransferDirection.Incoming ? "⬇️" : "⬆️";
    
    public string StatusIcon => Status switch
    {
        TransferStatus.Pending => "⏳",
        TransferStatus.InProgress => "🔄",
        TransferStatus.Completed => "✅",
        TransferStatus.Failed => "❌",
        TransferStatus.Cancelled => "🚫",
        _ => "❓"
    };

    public string StatusText => Status switch
    {
        TransferStatus.Pending => "Pending",
        TransferStatus.InProgress => $"{Progress:F0}%",
        TransferStatus.Completed => "Completed",
        TransferStatus.Failed => "Failed",
        TransferStatus.Cancelled => "Cancelled",
        _ => "Unknown"
    };

    public IBrush StatusColor => Status switch
    {
        TransferStatus.Completed => Brushes.LimeGreen,
        TransferStatus.Failed => Brushes.Tomato,
        TransferStatus.Cancelled => Brushes.Gray,
        _ => Brushes.White
    };

    public ICommand CancelCommand { get; }
    public ICommand RetryCommand { get; }

    private void Cancel()
    {
        _transferService.CancelTransfer(_transfer.Id);
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(StatusText));
    }

    private async Task RetryAsync()
    {
        await _transferService.RetryTransferAsync(_transfer.Id);
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(StatusText));
    }
}
