using System.Collections.Generic;

namespace Flow.Launcher.Plugin.AppUpgrader
{
    public class Settings
    {
        public bool EnableUpgradeAll { get; set; } = false;
        public bool SyncWithWingetPins { get; set; } = false;
        public List<string> ExcludedApps { get; set; } = new List<string>();
    }
}
