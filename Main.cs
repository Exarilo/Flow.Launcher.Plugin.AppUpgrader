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
using System.Windows.Controls;
using System.Windows;
namespace Flow.Launcher.Plugin.AppUpgrader
{
    public class AppUpgrader : IAsyncPlugin, ISettingProvider
    {
        private SettingsPage settingsPage;
        internal PluginInitContext Context;
        private ConcurrentBag<UpgradableApp> allUpgradableApps;
        private ConcurrentBag<UpgradableApp> upgradableApps;
        private ConcurrentDictionary<string, string> appIconPaths;
        private readonly SemaphoreSlim _refreshSemaphore = new SemaphoreSlim(1, 1);
        private DateTime _lastRefreshTime = DateTime.MinValue;
        private const int CACHE_EXPIRATION_MINUTES = 15;
        private const int COMMAND_TIMEOUT_SECONDS = 10;
        private static readonly Regex AppLineRegex = new Regex(
            @"^(.+?)\s+(\S+)\s+(\S+)\s+(\S+)(?:\s+(\S+))?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase,
            TimeSpan.FromMilliseconds(500)
        );
        private static readonly Regex DashLineRegex = new Regex(
            @"^-+$",
            RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(500)
        );

        public async Task InitAsync(PluginInitContext context)
        {
            Context = context;
            appIconPaths = new ConcurrentDictionary<string, string>();

            Application.Current.Dispatcher.Invoke(() =>
            {
                settingsPage = new SettingsPage(Context);
                settingsPage.SettingLoaded += async (s, e) =>
                {
                    settingsPage.ExcludedApps.CollectionChanged += (s, e) => ApplyExclusionFilter();
                    RemoveExcludedAppsFromUpgradableList();
                };
            });
            Task.Run(async () =>
            {
                try
                {
                    await RefreshUpgradableAppsAsync();
                }
                catch (Exception ex) { }
            });

            ThreadPool.SetMinThreads(Environment.ProcessorCount * 2, Environment.ProcessorCount * 2);
            await Task.CompletedTask;
        }


        private void RemoveExcludedAppsFromUpgradableList()
        {
            var excludedApps = settingsPage.ExcludedApps;

            if (upgradableApps == null || excludedApps == null || !excludedApps.Any())
            {
                return;
            }

            var updatedApps = upgradableApps
                .Where(app => !excludedApps.Any(excludedApp =>
                    app.Name.Contains(excludedApp, StringComparison.OrdinalIgnoreCase) ||
                    app.Id.Contains(excludedApp, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            upgradableApps = new ConcurrentBag<UpgradableApp>(updatedApps);
        }

        private void ExcludedApps_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {

            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add ||
                e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
            {
                RemoveExcludedAppsFromUpgradableList();
            }
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

            var results = new List<Result>();

            if (settingsPage.EnableUpgradeAll)
            {
                results.Add(new Result
                {
                    Title = "Upgrade All Applications",
                    SubTitle = "Upgrade all apps listed below.",
                    IcoPath = "Images\\app.png",
                    Action = context =>
                    {
                        Task.Run(async () =>
                        {
                            try
                            {
                                foreach (var app in upgradableApps)
                                {
                                    await PerformUpgradeAsync(app);
                                }
                            }
                            catch (Exception ex) { }
                        });
                        return true;
                    }
                });
            }
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

            results.AddRange(await Task.WhenAll(tasks));

            return results;
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

                // 1. Try Registry Uninstall paths (very fast & precise)
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
                                    var cleanedPath = path.Trim('\"', ' ');
                                    if (cleanedPath.Contains(","))
                                    {
                                        cleanedPath = cleanedPath.Split(',')[0].Trim();
                                    }

                                    if (File.Exists(cleanedPath))
                                    {
                                        return cleanedPath;
                                    }
                                    if (Directory.Exists(cleanedPath))
                                    {
                                        try
                                        {
                                            var iconInDir = Directory.GetFiles(cleanedPath, "*.exe")
                                                .Concat(Directory.GetFiles(cleanedPath, "*.ico"))
                                                .FirstOrDefault(f => possibleNames.Any(name =>
                                                    Path.GetFileNameWithoutExtension(f).Contains(name, StringComparison.OrdinalIgnoreCase)));

                                            if (iconInDir != null)
                                            {
                                                return iconInDir;
                                            }
                                        }
                                        catch {}
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

                // 2. Try Start Menu shortcuts (shallow structure)
                var startMenuPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
                var userStartMenuPath = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
                var shortcutFiles = await Task.Run(() =>
                {
                    var files = new List<string>();
                    foreach (var smp in new[] { startMenuPath, userStartMenuPath })
                    {
                        if (Directory.Exists(smp))
                        {
                            try
                            {
                                files.AddRange(Directory.GetFiles(smp, "*.lnk", SearchOption.AllDirectories));
                            }
                            catch {}
                        }
                    }
                    return files.Where(f => possibleNames.Any(name =>
                            Path.GetFileNameWithoutExtension(f).Contains(name, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                });

                if (shortcutFiles.Any())
                {
                    var result = shortcutFiles.First();
                    appIconPaths.TryAdd(appId, result);
                    return result;
                }

                // 3. Last Resort: Directory check with limited depth (no heavy recursion)
                var searchPaths = new List<string>
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
                };

                foreach (var basePath in searchPaths)
                {
                    if (!Directory.Exists(basePath)) continue;

                    var directories = await Task.Run(() => 
                    {
                        try
                        {
                            return Directory.GetDirectories(basePath, "*", SearchOption.TopDirectoryOnly);
                        }
                        catch { return Array.Empty<string>(); }
                    });

                    var possibleDirs = directories.Where(dir => possibleNames.Any(name =>
                        Path.GetFileName(dir).Contains(name, StringComparison.OrdinalIgnoreCase)));

                    foreach (var dir in possibleDirs)
                    {
                        var iconFiles = new List<string>();
                        try
                        {
                            await Task.Run(() =>
                            {
                                // Only scan top level files, avoid deep AllDirectories recursion
                                iconFiles.AddRange(Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly));
                                iconFiles.AddRange(Directory.GetFiles(dir, "*.ico", SearchOption.TopDirectoryOnly));
                                iconFiles.AddRange(Directory.GetFiles(dir, "*.lnk", SearchOption.TopDirectoryOnly));
                                
                                // Scan 1 level deep
                                foreach (var subDir in Directory.GetDirectories(dir))
                                {
                                    iconFiles.AddRange(Directory.GetFiles(subDir, "*.exe", SearchOption.TopDirectoryOnly));
                                    iconFiles.AddRange(Directory.GetFiles(subDir, "*.ico", SearchOption.TopDirectoryOnly));
                                }
                            });
                        }
                        catch {}

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
            }
            catch (Exception) { }

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
                allUpgradableApps = new ConcurrentBag<UpgradableApp>(apps);
                ApplyExclusionFilter();
                _lastRefreshTime = DateTime.UtcNow;
            }
            finally
            {
                _refreshSemaphore.Release();
            }
        }
        private void ApplyExclusionFilter()
        {
            var excludedApps = settingsPage.ExcludedApps;

            if (excludedApps == null || !excludedApps.Any())
            {
                upgradableApps = new ConcurrentBag<UpgradableApp>(allUpgradableApps);
                return;
            }

            var filteredApps = allUpgradableApps
                .Where(app => !excludedApps.Any(excludedApp =>
                    app.Name.Contains(excludedApp, StringComparison.OrdinalIgnoreCase) ||
                    app.Id.Contains(excludedApp, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            upgradableApps = new ConcurrentBag<UpgradableApp>(filteredApps);
        }


        private async Task PerformUpgradeAsync(UpgradableApp app)
        {
            Context.API.ShowMsg($"Preparing to update {app.Name}... This may take a moment.");
            await ExecuteWingetCommandAsync($"winget upgrade --id {app.Id} --silent --accept-source-agreements --accept-package-agreements");

            if (allUpgradableApps != null)
            {
                var updatedAllApps = allUpgradableApps.Where(a => a.Id != app.Id).ToList();
                allUpgradableApps = new ConcurrentBag<UpgradableApp>(updatedAllApps);
            }

            if (upgradableApps != null)
            {
                var updatedApps = upgradableApps.Where(a => a.Id != app.Id).ToList();
                upgradableApps = new ConcurrentBag<UpgradableApp>(updatedApps);
            }

            await RefreshUpgradableAppsAsync();
        }

        public Control CreateSettingPanel()
        {

            return settingsPage;
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
            if (startIndex == -1 || startIndex == 0) return upgradableApps;

            string headerLine = lines[startIndex - 1];
            int idStart = headerLine.IndexOf("Id", StringComparison.OrdinalIgnoreCase);
            int versionStart = headerLine.IndexOf("Version", StringComparison.OrdinalIgnoreCase);
            int availableStart = headerLine.IndexOf("Available", StringComparison.OrdinalIgnoreCase);
            if (availableStart == -1)
                availableStart = headerLine.IndexOf("New", StringComparison.OrdinalIgnoreCase);
            int sourceStart = headerLine.IndexOf("Source", StringComparison.OrdinalIgnoreCase);

            // Make sure we have valid start indices. If headers are completely different or missing,
            // fallback to regex parsing.
            bool useIndexParsing = idStart != -1 && versionStart != -1 && availableStart != -1;

            for (int i = startIndex + 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                UpgradableApp app = null;

                if (useIndexParsing)
                {
                    try
                    {
                        string name = SafeSubstring(line, 0, idStart).Trim();
                        string id = SafeSubstring(line, idStart, versionStart - idStart).Trim();
                        string version = SafeSubstring(line, versionStart, availableStart - versionStart).Trim();
                        string available = sourceStart != -1 
                            ? SafeSubstring(line, availableStart, sourceStart - availableStart).Trim()
                            : SafeSubstring(line, availableStart).Trim();
                        string source = sourceStart != -1 
                            ? SafeSubstring(line, sourceStart).Trim() 
                            : string.Empty;

                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id))
                        {
                            app = new UpgradableApp
                            {
                                Name = name,
                                Id = id,
                                Version = version,
                                AvailableVersion = available,
                                Source = source
                            };
                        }
                    }
                    catch
                    {
                        app = null;
                    }
                }

                // Fallback to Regex if index parsing failed or was disabled
                if (app == null)
                {
                    var match = AppLineRegex.Match(line.Trim());
                    if (match.Success)
                    {
                        app = new UpgradableApp
                        {
                            Name = match.Groups[1].Value.Trim(),
                            Id = match.Groups[2].Value,
                            Version = match.Groups[3].Value,
                            AvailableVersion = match.Groups[4].Value,
                            Source = match.Groups.Count > 5 ? match.Groups[5].Value : string.Empty
                        };
                    }
                }

                if (app != null && (app.Id.Contains('.') || app.Id.Contains('-')))
                {
                    upgradableApps.Add(app);
                }
            }
            return upgradableApps;
        }

        private static string SafeSubstring(string text, int start, int length = -1)
        {
            if (start >= text.Length) return string.Empty;
            if (length == -1) return text.Substring(start);
            if (start + length > text.Length) return text.Substring(start);
            return text.Substring(start, length);
        }

        private static async Task<string> ExecuteWingetCommandAsync(string command, CancellationToken cancellationToken = default)
        {
            var processInfo = new ProcessStartInfo("cmd.exe", "/c " + command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
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