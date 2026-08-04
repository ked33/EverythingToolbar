using System;

namespace EverythingToolbar.Helpers
{
    public static class KeepaliveSettings
    {
        public const int DefaultIntervalSeconds = 60;
        public const int MinimumIntervalSeconds = 1;

        public static int NormalizeIntervalSeconds(int intervalSeconds)
        {
            return intervalSeconds < MinimumIntervalSeconds ? DefaultIntervalSeconds : intervalSeconds;
        }

        public static TimeSpan GetInterval(ISettings settings)
        {
            return TimeSpan.FromSeconds(NormalizeIntervalSeconds(settings.KeepaliveIntervalSeconds));
        }
    }
}
