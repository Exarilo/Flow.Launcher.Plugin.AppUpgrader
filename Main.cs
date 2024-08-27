using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Flow.Launcher.Plugin;

namespace Flow.Launcher.Plugin.AppUpgrader
{
    public class AppUpgrader : IAsyncPlugin
    {
        internal PluginInitContext Context;
        private List<UpgradableApp> upgradableApps;

        public async Task InitAsync(PluginInitContext context)
        {
            Context = context;
            upgradableApps = await GetUpgradableAppsAsync();
        }

        public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
        {
            var results = new List<Result>();
            string keyword = query.FirstSearch.Trim().ToLower();
            if (keyword.Equals("up") || keyword.Equals("upgrade"))
            {
                foreach (var app in upgradableApps.ToList()) 
                {
                    results.Add(new Result
                    {
                        Title = $"Upgrade {app.Name}",
                        SubTitle = $"From {app.Version} to {app.AvailableVersion}",
                        Action = context =>
                        {
                            _ = PerformUpgradeAsync(app);
                            return true;
                        },
                        IcoPath = "Images\\app.png"
                    });
                }
            }

            return results;
        }


        private async Task PerformUpgradeAsync(UpgradableApp app)
        {
            Context.API.ShowMsg($"Attempting to update {app.Name}...");
            await ExecuteWingetCommandAsync($"winget upgrade --id {app.Id} -i");
            upgradableApps = await GetUpgradableAppsAsync();
        }

        private async Task<List<UpgradableApp>> GetUpgradableAppsAsync()
        {
            var output = await ExecuteWingetCommandAsync("winget upgrade");
            return ParseWingetOutput(output);
        }

        private List<UpgradableApp> ParseWingetOutput(string output)
        {
            var upgradableApps = new List<UpgradableApp>();
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            // Find the header line
            int startIndex = Array.FindIndex(lines, line => Regex.IsMatch(line, @"^-+$"));
            if (startIndex == -1) return upgradableApps;

            // Analyze each line after the header line, ignoring the last line (which is the number of upgrade available)
            for (int i = startIndex + 1; i < lines.Length - 1; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var match = Regex.Match(line, @"^(.+?)\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)$");
                if (match.Success)
                {
                    var app = new UpgradableApp
                    {
                        Name = match.Groups[1].Value.Trim(),
                        Id = match.Groups[2].Value,
                        Version = match.Groups[3].Value,
                        AvailableVersion = match.Groups[4].Value,
                        Source = match.Groups[5].Value
                    };

                    if (app.Id.Contains(".") || app.Id.Contains("-"))
                    {
                        upgradableApps.Add(app);
                    }
                }
            }

            return upgradableApps;
        }

        private async Task<string> ExecuteWingetCommandAsync(string command)
        {
            var processInfo = new ProcessStartInfo("cmd.exe", "/c " + command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var output = new StringBuilder();

            using (var process = new Process())
            {
                process.StartInfo = processInfo;

                process.OutputDataReceived += (sender, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                process.ErrorDataReceived += (sender, e) => { if (e.Data != null) output.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await Task.Run(() => process.WaitForExit());
            }

            return output.ToString();
        }
    }

    public class UpgradableApp
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public string Version { get; set; }
        public string AvailableVersion { get; set; }
        public string Source { get; set; }
    }
}
