using Swarm.Core.Abstractions;

namespace Swarm.Avalonia.Services;

/// <summary>
/// Cross-platform implementation of IPowerService.
/// Currently a stub as there is no universal cross-platform battery API in standard .NET.
/// TODO: Implement per-platform native calls or use a library like System.Device.Power.
/// </summary>
public class AvaloniaPowerService : IPowerService
{
    public bool IsOnBattery => false; // Assume AC power for now to avoid blocking sync

    public int? BatteryPercentage => null;
}
