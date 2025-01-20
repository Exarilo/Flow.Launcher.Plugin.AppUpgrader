using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.IO;
using Flow.Launcher.Plugin;

namespace Flow.Launcher.Plugin.AppUpgrader
{
    public partial class SettingsPage : UserControl
    {
        private ObservableCollection<SettingItem> settings { get; set; } = new ObservableCollection<SettingItem>();
        private readonly PluginInitContext context;

        public event EventHandler EnableUpgradeAllChanged;
        public EventHandler SettingLoaded;

        public bool EnableUpgradeAll
        {
            get => GetSetting("EnableUpgradeAll", false);
            set
            {
                var previousValue = GetSetting("EnableUpgradeAll", false);
                if (previousValue != value)
                {
                    UpdateSetting("EnableUpgradeAll", value);
                    SaveSettingsToFile();
                    EnableUpgradeAllChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public ObservableCollection<string> ExcludedApps { get; } = new ObservableCollection<string>();

        string SettingsPath => Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(context.CurrentPluginMetadata.PluginDirectory)),
            "Settings",
            "Plugins",
            "Flow.Launcher.Plugin.AppUpgrader",
            "Settings.json"
        );

        public SettingsPage(PluginInitContext context)
        {
            InitializeComponent();

            this.context = context;
            DataContext = this;

            LoadSettingsFromFile();
            LoadExcludedApps();
            this.Loaded += (sender, e) => SettingLoaded?.Invoke(this, EventArgs.Empty);
        }

        private void LoadExcludedApps()
        {
            var excludedApps = GetSetting("ExcludedApps", string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(app => app.Trim('"'))
                .ToList();

            ExcludedApps.Clear();
            foreach (var app in excludedApps)
            {
                ExcludedApps.Add(app);
            }
        }

        private void SaveExcludedApps()
        {
            var apps = string.Join(",", ExcludedApps);
            UpdateSetting("ExcludedApps", apps);
            SaveSettingsToFile();
        }

        private T GetSetting<T>(string key, T defaultValue)
        {
            var item = settings.FirstOrDefault(s => s.Key == key);
            if (item == null)
            {
                return defaultValue;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(item.Value);
            }
            catch
            {
                return defaultValue;
            }
        }

        private void UpdateSetting<T>(string key, T value)
        {
            var jsonValue = JsonSerializer.Serialize(value);
            var item = settings.FirstOrDefault(s => s.Key == key);

            if (item == null)
            {
                settings.Add(new SettingItem { Key = key, Value = jsonValue });
            }
            else
            {
                item.Value = jsonValue;
            }
        }

        private void SaveSettingsToFile()
        {
            try
            {
                var filePath = SettingsPath;
                var jsonSettings = JsonSerializer.Serialize(settings);

                var directory = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(filePath, jsonSettings);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving settings: {ex.Message}");
            }
        }

        private void LoadSettingsFromFile()
        {
            try
            {
                var filePath = SettingsPath;

                if (File.Exists(filePath))
                {
                    var jsonSettings = File.ReadAllText(filePath);
                    settings = JsonSerializer.Deserialize<ObservableCollection<SettingItem>>(jsonSettings);
                }
            }
            catch (Exception ex)
            {
            }
        }

        private void AddExclusionButton_Click(object sender, RoutedEventArgs e)
        {
            var appIdOrName = ExcludeAppTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(appIdOrName))
            {
                return;
            }

            if (!ExcludedApps.Any(x => x.Equals(appIdOrName, StringComparison.OrdinalIgnoreCase)))
            {
                ExcludedApps.Add(appIdOrName);
                ExcludeAppTextBox.Clear();
                SaveExcludedApps();
            }
        }

        private void RemoveExclusion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string appIdOrName)
            {
                ExcludedApps.Remove(appIdOrName);
                SaveExcludedApps();
            }
        }
    }
}
