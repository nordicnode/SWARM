using Avalonia.Controls;
using Avalonia.Interactivity;
using Swarm.Core.Models;
using Swarm.Core.Services;

namespace Swarm.Avalonia.Views;

public partial class PairingDialog : Window
{
    private readonly Peer _peer;
    private readonly PairingService _pairingService;
    
    public bool WasTrusted { get; private set; }

    public PairingDialog()
    {
        InitializeComponent();
        _peer = null!;
        _pairingService = null!;
    }

    public PairingDialog(Peer peer, PairingService pairingService)
    {
        InitializeComponent();
        _peer = peer;
        _pairingService = pairingService;

        // Set peer info
        PeerNameText.Text = peer.Name;
        FingerprintText.Text = pairingService.GetPeerFingerprint(peer);
        PairingCodeText.Text = pairingService.GeneratePeerPairingCode(peer);
        MyCodeText.Text = pairingService.GenerateMyPairingCode();

        // Wire up buttons
        TrustButton.Click += OnTrustClicked;
        RejectButton.Click += OnRejectClicked;
    }

    private void OnTrustClicked(object? sender, RoutedEventArgs e)
    {
        WasTrusted = true;
        Close(true);
    }

    private void OnRejectClicked(object? sender, RoutedEventArgs e)
    {
        WasTrusted = false;
        Close(false);
    }
}
