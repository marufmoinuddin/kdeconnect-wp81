using System;
using Windows.Phone.Devices.Power;
using KDEConnect.WP81.Plugins;

namespace KDEConnect.WP81.Services
{
    /// <summary>
    /// Battery level from the phone.
    ///
    /// Windows.Phone.Devices.Power.Battery exposes RemainingChargePercent but
    /// not charging state, so only the percentage is reported; the
    /// isCharging field is omitted rather than guessed
    /// (docs/known-limitations.md).
    /// </summary>
    public sealed class WinRtBatterySource : IBatterySource
    {
        public bool TryRead(out int percent)
        {
            percent = -1;
            try
            {
                Battery battery = Battery.GetDefault();
                if (battery == null)
                {
                    return false;
                }
                percent = battery.RemainingChargePercent;
                return percent >= 0 && percent <= 100;
            }
            catch (Exception)
            {
                // The API can be unavailable (no battery, restricted context).
                return false;
            }
        }
    }
}
