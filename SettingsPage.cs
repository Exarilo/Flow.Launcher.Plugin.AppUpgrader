using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using Flow.Launcher.Plugin;

namespace Flow.Launcher.Plugin.AppUpgrader
{
    public partial class SettingsPage : UserControl
    {
        private readonly Settings _settings;
        private readonly PluginInitContext _context;

        public event EventHandler EnableUpgradeAllChanged;

        public bool EnableUpgradeAll
        {
            get => _settings.EnableUpgradeAll;
            set
            {
                if (_settings.EnableUpgradeAll != value)
                {
                    _settings.EnableUpgradeAll = value;
                    _context.API.SaveSettingJsonStorage<Settings>();
                    EnableUpgradeAllChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public ObservableCollection<string> ExcludedApps { get; } = new ObservableCollection<string>();

        public SettingsPage(PluginInitContext context, Settings settings)
        {
            InitializeComponent();

            _context = context;
            _settings = settings;
            DataContext = this;

            foreach (var app in _settings.ExcludedApps)
            {
                ExcludedApps.Add(app);
            }

            ExcludedApps.CollectionChanged += (s, e) =>
            {
                _settings.ExcludedApps = ExcludedApps.ToList();
                _context.API.SaveSettingJsonStorage<Settings>();
            };
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
            }
        }

        private void RemoveExclusion_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string appIdOrName)
            {
                ExcludedApps.Remove(appIdOrName);
            }
        }
    }
}
