using System.Collections.Generic;

namespace Flow.Launcher.Plugin.AppUpgrader
{
    public class Settings
    {
        public bool EnableUpgradeAll { get; set; } = true;
        public bool SyncWithWingetPins { get; set; } = false;
        public int CacheExpirationMinutes { get; set; } = 15;
        public List<string> ExcludedApps { get; set; } = new List<string>();
    }
}
