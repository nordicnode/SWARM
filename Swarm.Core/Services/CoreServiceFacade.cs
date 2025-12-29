namespace Swarm.Core.Services;

/// <summary>
/// Facade that aggregates all Core services into a single dependency.
/// Reduces constructor complexity for consuming ViewModels.
/// </summary>
public class CoreServiceFacade
{
    public Settings Settings { get; }
    public CryptoService CryptoService { get; }
    public DiscoveryService DiscoveryService { get; }
    public TransferService TransferService { get; }
    public SyncService SyncService { get; }
    public VersioningService VersioningService { get; }
    public IntegrityService IntegrityService { get; }
    public RescanService RescanService { get; }
    public ActivityLogService ActivityLogService { get; }
    public ConflictResolutionService ConflictResolutionService { get; }
    public ShareLinkService ShareLinkService { get; }
    public PairingService PairingService { get; }
    public BandwidthTrackingService BandwidthTrackingService { get; }
    public FolderEncryptionService FolderEncryptionService { get; }

    public CoreServiceFacade(
        Settings settings,
        CryptoService cryptoService,
        DiscoveryService discoveryService,
        TransferService transferService,
        SyncService syncService,
        VersioningService versioningService,
        IntegrityService integrityService,
        RescanService rescanService,
        ActivityLogService activityLogService,
        ConflictResolutionService conflictResolutionService,
        ShareLinkService shareLinkService,
        PairingService pairingService,
        BandwidthTrackingService bandwidthTrackingService,
        FolderEncryptionService folderEncryptionService)
    {
        Settings = settings;
        CryptoService = cryptoService;
        DiscoveryService = discoveryService;
        TransferService = transferService;
        SyncService = syncService;
        VersioningService = versioningService;
        IntegrityService = integrityService;
        RescanService = rescanService;
        ActivityLogService = activityLogService;
        ConflictResolutionService = conflictResolutionService;
        ShareLinkService = shareLinkService;
        PairingService = pairingService;
        BandwidthTrackingService = bandwidthTrackingService;
        FolderEncryptionService = folderEncryptionService;
    }
}
