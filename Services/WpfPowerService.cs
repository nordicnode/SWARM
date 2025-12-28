using System.Windows.Forms;
using Swarm.Core.Abstractions;

namespace Swarm.Services;

/// <summary>
/// WPF/WinForms implementation of IPowerService.
/// </summary>
public class WpfPowerService : IPowerService
{
    public bool IsOnBattery
    {
        get
        {
            try
            {
                return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
            }
            catch
            {
                return false;
            }
        }
    }

    public int? BatteryPercentage
    {
        get
        {
            try
            {
                var percent = SystemInformation.PowerStatus.BatteryLifePercent;
                if (percent >= 0 && percent <= 1)
                {
                    return (int)(percent * 100);
                }
                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
