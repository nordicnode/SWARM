using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Threading.Tasks;
using Swarm.Core.Models;
using Swarm.Core.Services;
using Swarm.Core.ViewModels;

namespace Swarm.ViewModels;

public class PeersViewModel : BaseViewModel
{
    private readonly DiscoveryService _discoveryService;
    private readonly TransferService _transferService;
    private readonly Settings _settings;

    private Peer? _selectedPeer;
    private string _statusText = "Scanning for peers...";

    public ObservableCollection<Peer> Peers => _discoveryService.Peers;

    public Peer? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            if (SetProperty(ref _selectedPeer, value))
            {
                OnPropertyChanged(nameof(HasSelectedPeer));
                RelayCommand.RaiseGlobalCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public bool HasNoPeers => _discoveryService.Peers.Count == 0;
    public bool HasSelectedPeer => SelectedPeer != null;

    public string LocalId => _settings.LocalId;

    public ICommand SendFilesCommand { get; }

    public PeersViewModel(DiscoveryService discoveryService, TransferService transferService, Settings settings)
    {
        _discoveryService = discoveryService;
        _transferService = transferService;
        _settings = settings;

        SendFilesCommand = new AsyncRelayCommand(SendFilesAsync, CanSendFiles);
        
        // Update status text based on peers
        _discoveryService.Peers.CollectionChanged += (s, e) => UpdateStatus();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var count = _discoveryService.Peers.Count;
        StatusText = count == 0 ? "Scanning for peers..." : $"{count} peer{(count == 1 ? "" : "s")} found";
        OnPropertyChanged(nameof(HasNoPeers));
    }

    private bool CanSendFiles(object? parameter)
    {
        // Always allow the button to be clicked - we'll prompt if no peer is selected
        return true;
    }

    private async Task SendFilesAsync(object? parameter)
    {
        // Check if we have a peer selected
        if (SelectedPeer == null)
        {
            if (_discoveryService.Peers.Count == 0)
            {
                System.Windows.MessageBox.Show("No devices found on the network.\n\nMake sure other devices running Swarm are connected to the same network.", "No Devices Available", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            else if (_discoveryService.Peers.Count == 1)
            {
                // Auto-select the only available peer
                SelectedPeer = _discoveryService.Peers.First();
            }
            else
            {
                System.Windows.MessageBox.Show("Please select a device from the list first.", "Select a Device", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }

        string[]? files = null;

        if (parameter is string[] paths)
        {
            files = paths;
        }
        else
        {
            // Open file picker
            var openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = "Select files to send"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                files = openFileDialog.FileNames;
            }
        }

        if (files != null && files.Length > 0 && SelectedPeer != null)
        {
            try
            {
                foreach (var file in files)
                {
                    if (File.Exists(file))
                    {
                        await _transferService.SendFile(SelectedPeer, file);
                    }
                }
                
                System.Windows.MessageBox.Show($"Started sending {files.Length} file(s) to {SelectedPeer.Name}", "Transfer Started", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error sending files: {ex.Message}", "Transfer Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
