namespace Swarm.Core.Abstractions;

/// <summary>
/// Platform-agnostic interface for power status checks.
/// Replaces WPF/WinForms PowerLineStatus for cross-platform compatibility.
/// </summary>
public interface IPowerService
{
    /// <summary>
    /// Returns true if the device is currently running on battery power.
    /// </summary>
    bool IsOnBattery { get; }

    /// <summary>
    /// Gets the current battery charge percentage (0-100).
    /// Returns null if battery info is unavailable.
    /// </summary>
    int? BatteryPercentage { get; }
}
