using System;
using System.Collections.Concurrent;
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
        private ConcurrentBag<UpgradableApp> upgradableApps;
        private readonly SemaphoreSlim _refreshSemaphore = new SemaphoreSlim(1, 1);
        private DateTime _lastRefreshTime = DateTime.MinValue;
        private const int CACHE_EXPIRATION_MINUTES = 15;
        private const int COMMAND_TIMEOUT_SECONDS = 10;
        private static readonly Regex AppLineRegex = new Regex(
            @"^(.+?)\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(500) 
        );
        private static readonly Regex DashLineRegex = new Regex(
            @"^-+$",
            RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(500) 
        );
        public Task InitAsync(PluginInitContext context)
        {
            Context = context;
            Task.Run(async () =>
            {
                try
                {
                    await RefreshUpgradableAppsAsync();
                }
                catch (Exception ex) { }
            });

            ThreadPool.SetMinThreads(Environment.ProcessorCount * 2, Environment.ProcessorCount * 2);
            return Task.CompletedTask;
        }

        public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
        {
            if (ShouldRefreshCache())
            {
                await RefreshUpgradableAppsAsync();
            }

            if (upgradableApps == null || !upgradableApps.Any())
            {
                return new List<Result>
                {
                    new Result
                    {
                        Title = "No updates available",
                        SubTitle = "All applications are up-to-date.",
                        IcoPath = "Images\\app.png"
                    }
                };
            }

            string filterTerm = query.Search?.Trim().ToLower();

            return upgradableApps.AsParallel()
                .WithDegreeOfParallelism(Environment.ProcessorCount)
                .Where(app => string.IsNullOrEmpty(filterTerm) ||
                             app.Name.ToLower().Contains(filterTerm))
                .Select(app => new Result
                {
                    Title = $"Upgrade {app.Name}",
                    SubTitle = $"From {app.Version} to {app.AvailableVersion}",
                    Action = context =>
                    {
                        Task.Run(async () =>
                        {
                            try
                            {
                                await PerformUpgradeAsync(app);
                            }
                            catch (Exception ex)
                            {
                                Context.API.ShowMsg($"Upgrade failed: {ex.Message}");
                            }
                        });
                        return true;
                    },
                    IcoPath = "Images\\app.png"
                })
                .ToList();
        }


        private bool ShouldRefreshCache()
        {
            return upgradableApps == null ||
                   DateTime.UtcNow - _lastRefreshTime > TimeSpan.FromMinutes(CACHE_EXPIRATION_MINUTES);
        }

        private async Task RefreshUpgradableAppsAsync()
        {
            if (!ShouldRefreshCache())
                return;

            await _refreshSemaphore.WaitAsync();
            try
            {
                if (!ShouldRefreshCache())
                    return;

                var apps = await GetUpgradableAppsAsync();
                upgradableApps = new ConcurrentBag<UpgradableApp>(apps);
                _lastRefreshTime = DateTime.Now;
            }
            catch (Exception ex) { }
            finally
            {
                _refreshSemaphore.Release();
            }
        }


        private async Task PerformUpgradeAsync(UpgradableApp app)
        {
            Context.API.ShowMsg($"Preparing to update {app.Name}... This may take a moment.");
            await ExecuteWingetCommandAsync($"winget upgrade --id {app.Id} -i");
            upgradableApps = new ConcurrentBag<UpgradableApp>(upgradableApps.Where(a => a.Id != app.Id));
            await RefreshUpgradableAppsAsync();
        }


        private async Task<List<UpgradableApp>> GetUpgradableAppsAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(COMMAND_TIMEOUT_SECONDS));
            var output = await ExecuteWingetCommandAsync("winget upgrade", cts.Token);
            return ParseWingetOutput(output);
        }

        private static List<UpgradableApp> ParseWingetOutput(string output)
        {
            var upgradableApps = new List<UpgradableApp>();
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            var startIndex = Array.FindIndex(lines, line => DashLineRegex.IsMatch(line));
            if (startIndex == -1) return upgradableApps;

            for (int i = startIndex + 1; i < lines.Length - 1; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var match = AppLineRegex.Match(line);
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

                    if (app.Id.Contains('.') || app.Id.Contains('-'))
                    {
                        upgradableApps.Add(app);
                    }
                }
            }
            return upgradableApps;
        }

        private static async Task<string> ExecuteWingetCommandAsync(string command, CancellationToken cancellationToken = default)
        {
            var processInfo = new ProcessStartInfo("cmd.exe", "/c " + command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processInfo);
            if (process == null)
                throw new InvalidOperationException("Failed to start process.");

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            if (!string.IsNullOrEmpty(error))
            {
                throw new InvalidOperationException(error);
            }

            return output;
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