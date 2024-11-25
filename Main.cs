using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Flow.Launcher.Plugin;
using Microsoft.Win32;

namespace Flow.Launcher.Plugin.AppUpgrader
{
    public class AppUpgrader : IAsyncPlugin
    {
        internal PluginInitContext Context;
        private ConcurrentBag<UpgradableApp> upgradableApps;
        private ConcurrentDictionary<string, string> appIconPaths;
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
            appIconPaths = new ConcurrentDictionary<string, string>();

            Task.Run(async () =>
            {
                try
                {
                    await RefreshUpgradableAppsAsync();
                }
                catch (Exception ex){}
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

            var tasks = upgradableApps.AsParallel()
                .WithDegreeOfParallelism(Environment.ProcessorCount)
                .Where(app => string.IsNullOrEmpty(filterTerm) ||
                             app.Name.ToLower().Contains(filterTerm))
                .Select(async app => new Result
                {
                    Title = $"Upgrade {app.Name}",
                    SubTitle = $"From {app.Version} to {app.AvailableVersion}",
                    IcoPath = await GetAppIconPath(app.Id, app.Name),
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
                    }
                });

            return (await Task.WhenAll(tasks)).ToList();
        }

        private async Task<string> GetAppIconPath(string appId, string appName)
        {
            if (appIconPaths.TryGetValue(appId, out string cachedPath))
            {
                return cachedPath;
            }

            try
            {
                var cleanAppName = new string(appName.TakeWhile(c => c != ' ').ToArray()).ToLowerInvariant();
                var possibleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            cleanAppName,
            appName.ToLowerInvariant(),
            appId.ToLowerInvariant()
        };

                if (appName.Contains(" "))
                {
                    possibleNames.Add(appName.Replace(" ", "").ToLowerInvariant());
                    possibleNames.Add(string.Join(".", appName.Split(' ')).ToLowerInvariant());
                }

                var searchPaths = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
            @"C:\Program Files\WindowsApps"
        };

                foreach (var basePath in searchPaths)
                {
                    if (!Directory.Exists(basePath)) continue;

                    var directories = await Task.Run(() => Directory.GetDirectories(basePath, "*", SearchOption.TopDirectoryOnly));
                    var possibleDirs = directories.Where(dir => possibleNames.Any(name =>
                        Path.GetFileName(dir).Contains(name, StringComparison.OrdinalIgnoreCase)));

                    foreach (var dir in possibleDirs)
                    {
                        var iconFiles = new List<string>();
                        try
                        {
                            await Task.Run(() =>
                            {
                                iconFiles.AddRange(Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories));
                                iconFiles.AddRange(Directory.GetFiles(dir, "*.ico", SearchOption.AllDirectories));
                                iconFiles.AddRange(Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories));
                            });
                        }
                        catch (UnauthorizedAccessException) { continue; }

                        foreach (var file in iconFiles)
                        {
                            var fileName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                            if (possibleNames.Any(name => fileName.Contains(name)))
                            {
                                appIconPaths.TryAdd(appId, file);
                                return file;
                            }
                        }
                    }
                }
                var registryResult = await Task.Run(() =>
                {
                    var registryPaths = new[]
                    {
                        $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{cleanAppName}.exe",
                        $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{appId}",
                        $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{appId}",
                        $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{cleanAppName}",
                        $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{cleanAppName}"
                    };

                    foreach (var regPath in registryPaths)
                    {
                        using (var key = Registry.LocalMachine.OpenSubKey(regPath))
                        using (var userKey = Registry.CurrentUser.OpenSubKey(regPath))
                        {
                            foreach (var regKey in new[] { key, userKey })
                            {
                                if (regKey == null) continue;

                                var paths = new[]
                                {
                            regKey.GetValue("DisplayIcon") as string,
                            regKey.GetValue("InstallLocation") as string,
                            regKey.GetValue(null) as string
                        };

                                foreach (var path in paths.Where(p => !string.IsNullOrEmpty(p)))
                                {
                                    if (File.Exists(path))
                                    {
                                        return path;
                                    }
                                    if (Directory.Exists(path))
                                    {
                                        var iconInDir = Directory.GetFiles(path, "*.exe")
                                            .Concat(Directory.GetFiles(path, "*.ico"))
                                            .FirstOrDefault(f => possibleNames.Any(name =>
                                                Path.GetFileNameWithoutExtension(f).Contains(name, StringComparison.OrdinalIgnoreCase)));

                                        if (iconInDir != null)
                                        {
                                            return iconInDir;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    return null;
                });

                if (registryResult != null)
                {
                    appIconPaths.TryAdd(appId, registryResult);
                    return registryResult;
                }

                var startMenuPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
                var shortcutFiles = await Task.Run(() =>
                    Directory.GetFiles(startMenuPath, "*.lnk", SearchOption.AllDirectories)
                        .Where(f => possibleNames.Any(name =>
                            Path.GetFileNameWithoutExtension(f).Contains(name, StringComparison.OrdinalIgnoreCase)))
                        .ToList());

                if (shortcutFiles.Any())
                {
                    var result = shortcutFiles.First();
                    appIconPaths.TryAdd(appId, result);
                    return result;
                }
            }
            catch (Exception ex){}

            return "Images\\app.png";
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
                _lastRefreshTime = DateTime.UtcNow;
            }
            catch (Exception ex){}
            finally
            {
                _refreshSemaphore.Release();
            }
        }

        private async Task PerformUpgradeAsync(UpgradableApp app)
        {
            Context.API.ShowMsg($"Preparing to update {app.Name}... This may take a moment.");
            await ExecuteWingetCommandAsync($"winget upgrade --id {app.Id} -i");

             if (upgradableApps != null)
            {
                var updatedApps = upgradableApps.Where(a => a.Id != app.Id).ToList();
                upgradableApps = new ConcurrentBag<UpgradableApp>(updatedApps);
            }

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

            for (int i = startIndex + 1; i < lines.Length; i++)
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