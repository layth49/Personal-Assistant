using System;
using System.Windows.Forms;

namespace Personal_Assistant.Power
{
    // A single reading of the battery.
    public sealed class BatteryInfo
    {
        public bool HasBattery { get; set; }
        public bool OnMains { get; set; }
        public int Percent { get; set; }

        // How long Windows thinks is left. Null whenever it will not say, which
        // is most of the time: it does not estimate while charging, and it needs
        // a minute or two of discharge before the figure settles. Null is a real
        // answer here, not a failure — reporting a made-up duration for a laptop
        // about to die is worse than saying you don't know.
        public TimeSpan? Remaining { get; set; }

        // The sentence to say. Leads with time when there is one, because "about
        // forty minutes left" is something you can act on and "37 percent" is
        // something you then have to do arithmetic on.
        public string Spoken()
        {
            if (!HasBattery) return "This machine doesn't have a battery.";

            if (OnMains)
            {
                return Percent >= 99
                    ? "You're plugged in and fully charged."
                    : $"You're plugged in, at {Percent} percent.";
            }

            if (Remaining.HasValue)
            {
                return $"About {Describe(Remaining.Value)} left, at {Percent} percent.";
            }

            return $"You're on battery at {Percent} percent. " +
                   "Windows hasn't worked out how long that is yet.";
        }

        private static string Describe(TimeSpan left)
        {
            int hours = (int)left.TotalHours;
            int minutes = left.Minutes;

            if (hours <= 0)
            {
                return minutes <= 1 ? "a minute" : $"{minutes} minutes";
            }
            string h = hours == 1 ? "an hour" : $"{hours} hours";
            if (minutes < 5) return h;
            return $"{h} and {minutes} minutes";
        }
    }

    // Reads the battery through SystemInformation.PowerStatus.
    //
    // Windows reports "unknown" for the time remaining far more often than it
    // reports a number: always while charging, and for a minute or two after
    // unplugging. The .NET sentinel is -1 seconds; WMI's is 71582788 minutes.
    // Both are filtered here so nothing downstream has to know that.
    public class BatteryReader
    {
        // Anything longer than this is the API failing to say "I don't know" in a
        // way we recognise, rather than a genuinely enormous battery.
        private static readonly TimeSpan Implausible = TimeSpan.FromHours(48);

        public virtual BatteryInfo Read()
        {
            try
            {
                PowerStatus status = SystemInformation.PowerStatus;

                bool none = (status.BatteryChargeStatus & BatteryChargeStatus.NoSystemBattery) != 0;
                if (none) return new BatteryInfo { HasBattery = false };

                var info = new BatteryInfo
                {
                    HasBattery = true,
                    OnMains = status.PowerLineStatus == PowerLineStatus.Online,
                    // BatteryLifePercent is 0..1, and reports 1.0 when it doesn't
                    // know — indistinguishable from a full battery, so it is only
                    // ever used as a percentage and never as a "we know" signal.
                    Percent = (int)Math.Round(status.BatteryLifePercent * 100)
                };

                int seconds = status.BatteryLifeRemaining;
                if (seconds > 0)
                {
                    var remaining = TimeSpan.FromSeconds(seconds);
                    if (remaining < Implausible) info.Remaining = remaining;
                }

                return info;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[battery] could not read power status: {ex.Message}");
                return new BatteryInfo { HasBattery = false };
            }
        }
    }
}
