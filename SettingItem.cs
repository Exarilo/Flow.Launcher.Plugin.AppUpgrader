using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Flow.Launcher.Plugin.AppUpgrader
{
    public class SettingItem
    {
        public string Key { get; set; }
        public string Value { get; set; }
    }
}
