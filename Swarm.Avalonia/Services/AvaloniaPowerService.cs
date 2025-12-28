using System.Runtime.InteropServices;
using Swarm.Core.Abstractions;

namespace Swarm.Avalonia.Services;

/// <summary>
/// Cross-platform implementation of IPowerService.
/// Uses platform-specific APIs where available.
/// </summary>
public class AvaloniaPowerService : IPowerService
{
    public bool IsOnBattery
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return IsOnBatteryWindows();
            }
            // For Linux/macOS, return false to avoid blocking sync
            return false;
        }
    }

    public int? BatteryPercentage
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return GetBatteryPercentageWindows();
            }
            return null;
        }
    }

    #region Windows Power Detection

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    private static bool IsOnBatteryWindows()
    {
        try
        {
            if (GetSystemPowerStatus(out var status))
            {
                // ACLineStatus: 0 = Offline (battery), 1 = Online (AC), 255 = Unknown
                return status.ACLineStatus == 0;
            }
        }
        catch
        {
            // Ignore errors - return false as safe default
        }
        return false;
    }

    private static int? GetBatteryPercentageWindows()
    {
        try
        {
            if (GetSystemPowerStatus(out var status))
            {
                // BatteryLifePercent: 0-100, or 255 if unknown
                if (status.BatteryLifePercent <= 100)
                {
                    return status.BatteryLifePercent;
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        return null;
    }

    #endregion
}
