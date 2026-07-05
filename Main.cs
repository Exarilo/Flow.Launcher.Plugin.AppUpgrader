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
        private Settings settings;
        internal PluginInitContext Context;
        private ConcurrentBag<UpgradableApp> allUpgradableApps;
        private ConcurrentBag<UpgradableApp> upgradableApps;
        private List<string> wingetPinnedAppIds = new List<string>();
        private ConcurrentDictionary<string, string> appIconPaths;
        private readonly ConcurrentDictionary<string, byte> _activeUpgrades = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _refreshSemaphore = new SemaphoreSlim(1, 1);
        private DateTime _lastRefreshTime = DateTime.MinValue;
        private const int COMMAND_TIMEOUT_SECONDS = 30;
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
            settings = Context.API.LoadSettingJsonStorage<Settings>();

            Application.Current.Dispatcher.Invoke(() =>
            {
                settingsPage = new SettingsPage(Context, settings);
                settingsPage.ExcludedApps.CollectionChanged += (s, e) => ApplyExclusionFilter();
            });

            ApplyExclusionFilter();

            await Task.Run(async () =>
            {
                try
                {
                    await RefreshUpgradableAppsAsync();
                }
                catch (Exception ex)
                {
                    Context.API.LogException("AppUpgrader", "Failed background refresh of upgradable apps on plugin initialization", ex);
                }
            });

            ThreadPool.SetMinThreads(Environment.ProcessorCount * 2, Environment.ProcessorCount * 2);
            await Task.CompletedTask;
        }

        public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
        {
            if (!IsWingetInstalled())
            {
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Windows Package Manager (winget) is not installed",
                        SubTitle = "Click here to open the Microsoft page for installing winget.",
                        IcoPath = "Images\\app.png",
                        Action = context =>
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo("https://learn.microsoft.com/windows/package-manager/winget/") { UseShellExecute = true });
                            }
                            catch (Exception ex)
                            {
                                Context.API.ShowMsg($"Failed to open browser: {ex.Message}");
                            }
                            return true;
                        }
                    },
                    new Result
                    {
                        Title = "Install winget automatically (asheroto/winget-install)",
                        SubTitle = "Click here to open the winget-install GitHub repository.",
                        IcoPath = "Images\\app.png",
                        Action = context =>
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo("https://github.com/asheroto/winget-install") { UseShellExecute = true });
                            }
                            catch (Exception ex)
                            {
                                Context.API.ShowMsg($"Failed to open browser: {ex.Message}");
                            }
                            return true;
                        }
                    }
                };
            }

            string filterTerm = query.Search?.Trim().ToLower();

            if (filterTerm == "refresh" || filterTerm == "r")
            {
                return new List<Result>
                {
                    new Result
                    {
                        Title = "Force check for updates",
                        SubTitle = "Clears cache and queries winget for any new available updates.",
                        IcoPath = "Images\\app.png",
                        Action = context =>
                        {
                            Task.Run(async () =>
                            {
                                try
                                {
                                    _lastRefreshTime = DateTime.MinValue;
                                    upgradableApps = null;
                                    allUpgradableApps = null;
                                    Context.API.ShowMsg("Checking for application updates... Please wait.");
                                    await RefreshUpgradableAppsAsync(force: true);
                                    Context.API.ShowMsg("Application updates list refreshed successfully!");
                                    Context.API.ChangeQuery(Context.CurrentPluginMetadata.ActionKeyword + " ", true);
                                }
                                catch (Exception ex)
                                {
                                    Context.API.ShowMsg($"Failed to refresh updates: {ex.Message}");
                                }
                            });
                            return true;
                        }
                    }
                };
            }

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

            var results = new List<Result>();

            if (settings.EnableUpgradeAll && string.IsNullOrEmpty(filterTerm))
            {
                results.Add(new Result
                {
                    Title = "Upgrade All Applications",
                    SubTitle = $"Upgrade all {upgradableApps.Count} applications listed below.",
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
                   DateTime.UtcNow - _lastRefreshTime > TimeSpan.FromMinutes(settings.CacheExpirationMinutes);
        }

        private async Task RefreshUpgradableAppsAsync(bool force = false)
        {
            if (!force && !ShouldRefreshCache())
                return;

            await _refreshSemaphore.WaitAsync();
            try
            {
                if (!force && !ShouldRefreshCache())
                    return;

                wingetPinnedAppIds = await GetPinnedAppIdsAsync();
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
            if (allUpgradableApps == null)
            {
                return;
            }

            var excludedApps = settings.ExcludedApps ?? new List<string>();
            var combinedExclusions = new HashSet<string>(excludedApps, StringComparer.OrdinalIgnoreCase);
            if (wingetPinnedAppIds != null)
            {
                foreach (var id in wingetPinnedAppIds)
                {
                    combinedExclusions.Add(id);
                }
            }

            if (combinedExclusions.Count == 0)
            {
                upgradableApps = new ConcurrentBag<UpgradableApp>(allUpgradableApps);
                return;
            }

            var filteredApps = allUpgradableApps
                .Where(app => !combinedExclusions.Any(excluded =>
                    app.Name.Contains(excluded, StringComparison.OrdinalIgnoreCase) ||
                    app.Id.Contains(excluded, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            upgradableApps = new ConcurrentBag<UpgradableApp>(filteredApps);
        }

        private async Task<List<string>> GetPinnedAppIdsAsync()
        {
            var pinnedIds = new List<string>();
            try
            {
                var output = await ExecuteWingetCommandInternalAsync("winget pin list");
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                var startIndex = Array.FindIndex(lines, line => DashLineRegex.IsMatch(line));
                if (startIndex == -1 || startIndex == 0) return pinnedIds;

                string headerLine = lines[startIndex - 1];
                var columns = GetColumnRanges(headerLine);

                if (columns.Count < 2) return pinnedIds;

                int idStart = columns[0].Start;
                int versionStart = columns[1].Start;

                for (int i = startIndex + 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (DashLineRegex.IsMatch(line)) break;

                    string id = SafeSubstring(line, idStart, versionStart - idStart).Trim();
                    if (!string.IsNullOrEmpty(id) && (id.Contains('.') || id.Contains('-')))
                    {
                        pinnedIds.Add(id);
                    }
                }
            }
            catch (Exception ex)
            {
                Context.API.LogException("AppUpgrader", "Failed to retrieve pinned application IDs from winget", ex);
            }
            return pinnedIds;
        }


        private async Task PerformUpgradeAsync(UpgradableApp app)
        {
            if (!_activeUpgrades.TryAdd(app.Id, 0))
            {
                Context.API.ShowMsg($"{app.Name} is already upgrading in the background!");
                return;
            }

            Context.API.ShowMsg($"Preparing to update {app.Name}... This may take a moment.");
            try
            {
                await ExecuteWingetCommandInternalAsync($"winget upgrade --id {app.Id} --silent --accept-source-agreements --accept-package-agreements");
                Context.API.ShowMsg($"{app.Name} upgraded successfully!");
            }
            catch (Exception ex)
            {
                Context.API.ShowMsg($"Failed to upgrade {app.Name}: {ex.Message}");
                Context.API.LogException("AppUpgrader", $"Failed to upgrade application {app.Name} (ID: {app.Id})", ex);
            }
            finally
            {
                _activeUpgrades.TryRemove(app.Id, out _);
            }

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

            await RefreshUpgradableAppsAsync(force: true);
        }

        public Control CreateSettingPanel()
        {

            return settingsPage;
        }

        private async Task<List<UpgradableApp>> GetUpgradableAppsAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(COMMAND_TIMEOUT_SECONDS));
            var output = await ExecuteWingetCommandInternalAsync("winget upgrade", cts.Token);
            return ParseWingetOutput(output);
        }

        private static List<UpgradableApp> ParseWingetOutput(string output)
        {
            var upgradableApps = new List<UpgradableApp>();
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (DashLineRegex.IsMatch(line))
                {
                    if (i == 0) continue;
                    string headerLine = lines[i - 1];

                    var columns = GetColumnRanges(headerLine);

                    // Name, Id, Version, Available
                    if (columns.Count < 4) continue;

                    int idStart = columns[1].Start;
                    int versionStart = columns[2].Start;
                    int availableStart = columns[3].Start;
                    int sourceStart = columns.Count >= 5 ? columns[4].Start : -1;

                    int j = i + 1;
                    while (j < lines.Length)
                    {
                        var rowLine = lines[j];
                        if (DashLineRegex.IsMatch(rowLine))
                        {
                            break;
                        }

                        try
                        {
                            string name = SafeSubstring(rowLine, 0, idStart).Trim();
                            string id = SafeSubstring(rowLine, idStart, versionStart - idStart).Trim();
                            string version = SafeSubstring(rowLine, versionStart, availableStart - versionStart).Trim();
                            string available = sourceStart != -1
                                ? SafeSubstring(rowLine, availableStart, sourceStart - availableStart).Trim()
                                : SafeSubstring(rowLine, availableStart).Trim();
                            string source = sourceStart != -1
                                ? SafeSubstring(rowLine, sourceStart).Trim()
                                : string.Empty;

                            // Validate that we parsed a real app row:
                            // 1. ID, version, available must not be empty
                            // 2. ID, version, available must not contain spaces
                            // 3. ID must contain at least a dot or a dash and contain alphanumeric characters
                            if (!string.IsNullOrEmpty(id) &&
                                !id.Contains(' ') &&
                                (id.Contains('.') || id.Contains('-')) &&
                                id.Any(char.IsLetterOrDigit) &&
                                !string.IsNullOrEmpty(version) &&
                                !version.Contains(' ') &&
                                !string.IsNullOrEmpty(available) &&
                                !available.Contains(' '))
                            {
                                var app = new UpgradableApp
                                {
                                    Name = name,
                                    Id = id,
                                    Version = version,
                                    AvailableVersion = available,
                                    Source = source
                                };

                                if (!upgradableApps.Any(x => x.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase)))
                                {
                                    upgradableApps.Add(app);
                                }
                            }
                        }
                        catch
                        {
                            // Skip malformed rows
                        }

                        j++;
                    }

                    // Move outer loop index to where we finished parsing this table
                    i = j - 1;
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

        private static string GetWingetPath()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var wingetPath = Path.Combine(localAppData, "Microsoft", "WindowsApps", "winget.exe");
            if (File.Exists(wingetPath))
            {
                return wingetPath;
            }
            return "winget.exe";
        }

        private static bool IsWingetInstalled()
        {
            string path = GetWingetPath();
            if (path != "winget.exe")
            {
                return File.Exists(path);
            }

            try
            {
                using var process = Process.Start(new ProcessStartInfo("winget.exe", "--version")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                process.WaitForExit(1000);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        internal static async Task<string> ExecuteWingetCommandInternalAsync(string command, CancellationToken cancellationToken = default)
        {
            string exePath = GetWingetPath();
            string arguments = command.StartsWith("winget ", StringComparison.OrdinalIgnoreCase)
                ? command.Substring(7)
                : command;

            var processInfo = new ProcessStartInfo(exePath, arguments)
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
                throw new InvalidOperationException("Failed to start winget process.");

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(cancellationToken);

            var error = errorTask.Result;
            if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
            {
                throw new InvalidOperationException($"winget exited with code {process.ExitCode}: {error}");
            }

            return outputTask.Result;
        }
        private static List<(int Start, int End)> GetColumnRanges(string headerLine)
        {
            var ranges = new List<(int Start, int End)>();
            int i = 0;
            int n = headerLine.Length;

            while (i < n)
            {
                while (i < n && headerLine[i] == ' ') i++;
                if (i >= n) break;

                int start = i;
                while (i < n)
                {
                    if (headerLine[i] == ' ' && i + 1 < n && headerLine[i + 1] == ' ')
                        break;
                    i++;
                }
                ranges.Add((start, i));
            }

            return ranges;
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