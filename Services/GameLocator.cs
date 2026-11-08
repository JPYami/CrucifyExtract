using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CrucifyExtract.Services;

public sealed record DetectedClient(string Root, string AddonsPath, string Source);

public static class GameLocator
{
    private static readonly Regex NameHint = new(
        @"wow|warcraft|forever|crucify",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows", "$recycle.bin", "system volume information", "recovery", "perflogs", "msocache",
        "windows.old", "config.msi", "$windows.~bt", "$windows.~ws", "appdata", "programdata",
        "node_modules", ".git", ".vs", "packages", "cache", "temp", "tmp", "installer",
        "driverstore", "winsxs", "assembly", "servicing", "logs", "crashdumps", "bin", "obj"
    };

    /// <summary>
    /// Turns whatever the user pointed at (client root, Interface, AddOns, or an add-on folder)
    /// into the AddOns path that archives should be installed into.
    /// </summary>
    public static string? ResolveAddons(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        path = path.Trim().Trim('"');
        if (!Directory.Exists(path)) return null;

        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (name.Equals("AddOns", StringComparison.OrdinalIgnoreCase)) return path;
        if (name.Equals("Interface", StringComparison.OrdinalIgnoreCase)) return Path.Combine(path, "AddOns");

        if (File.Exists(Path.Combine(path, "Wow.exe")) ||
            Directory.Exists(Path.Combine(path, "Interface")) ||
            Directory.Exists(Path.Combine(path, "Data")))
        {
            var iface = Path.Combine(path, "Interface");
            return Directory.Exists(iface) || File.Exists(Path.Combine(path, "Wow.exe"))
                ? Path.Combine(iface, "AddOns")
                : null;
        }

        try
        {
            if (Directory.EnumerateFiles(path, "*.toc").Any()) return path;
        }
        catch
        {
            // ignored
        }

        return null;
    }

    public static bool IsClient(string dir)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, "Wow.exe"))) return true;
            return Directory.Exists(Path.Combine(dir, "Data")) &&
                   Directory.Exists(Path.Combine(dir, "Interface"));
        }
        catch
        {
            return false;
        }
    }

    public static List<DetectedClient> Detect(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var found = new List<DetectedClient>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? dir, string source)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                dir = Path.GetFullPath(dir.Trim().Trim('"')).TrimEnd('\\');
            }
            catch
            {
                return;
            }
            if (!Directory.Exists(dir)) return;
            if (!seen.Add(dir)) return;
            if (!IsClient(dir)) return;

            found.Add(new DetectedClient(dir, Path.Combine(dir, "Interface", "AddOns"), source));
        }

        progress?.Report("Checking installed programs\u2026");
        foreach (var loc in RegistryLocations()) Consider(loc, "registry");

        progress?.Report("Checking common install folders\u2026");
        foreach (var root in Roots())
        {
            ct.ThrowIfCancellationRequested();
            Consider(root, "known");

            foreach (var one in ChildDirs(root))
            {
                if (NameHint.IsMatch(one)) Consider(one, "name-match");

                foreach (var two in ChildDirs(one))
                {
                    if (NameHint.IsMatch(two)) Consider(two, "name-match");
                }
            }
        }

        if (found.Count == 0)
        {
            progress?.Report("Scanning your drives\u2026");
            DeepScan(Consider, progress, ct);
        }

        progress?.Report(found.Count > 0 ? "Client found" : "No client found");

        return found
            .OrderByDescending(c => Directory.Exists(c.AddonsPath))
            .ThenByDescending(c => Directory.Exists(Path.Combine(c.Root, "Data")))
            .ToList();
    }

    private static IEnumerable<string> RegistryLocations()
    {
        var results = new List<string>();
        var views = new[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32)
        };

        foreach (var (hive, view) in views)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null) continue;

                foreach (var sub in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var key = uninstall.OpenSubKey(sub);
                        if (key == null) continue;

                        var display = key.GetValue("DisplayName") as string ?? "";
                        var location = key.GetValue("InstallLocation") as string ?? "";
                        var icon = key.GetValue("DisplayIcon") as string ?? "";
                        var haystack = display + " " + location;

                        if (!NameHint.IsMatch(haystack)) continue;

                        if (!string.IsNullOrWhiteSpace(location)) results.Add(location);

                        if (!string.IsNullOrWhiteSpace(icon))
                        {
                            var cleaned = icon.Trim().Trim('"');
                            var comma = cleaned.LastIndexOf(',');
                            if (comma > 0 && cleaned.Length > comma + 1 && char.IsDigit(cleaned[comma + 1]))
                                cleaned = cleaned[..comma];
                            var iconDir = Path.GetDirectoryName(cleaned.Trim('"'));
                            if (!string.IsNullOrWhiteSpace(iconDir)) results.Add(iconDir);
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
            catch
            {
                // ignored
            }
        }

        return results;
    }

    private static IEnumerable<string> Roots()
    {
        var roots = new List<string>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                roots.Add(drive.RootDirectory.FullName);
            }
            catch
            {
                // ignored
            }
        }

        void Add(Environment.SpecialFolder folder)
        {
            try
            {
                var p = Environment.GetFolderPath(folder);
                if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) roots.Add(p);
            }
            catch
            {
                // ignored
            }
        }

        Add(Environment.SpecialFolder.ProgramFiles);
        Add(Environment.SpecialFolder.ProgramFilesX86);
        Add(Environment.SpecialFolder.UserProfile);
        Add(Environment.SpecialFolder.DesktopDirectory);
        Add(Environment.SpecialFolder.MyDocuments);
        Add(Environment.SpecialFolder.LocalApplicationData);

        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> ChildDirs(string dir)
    {
        try
        {
            var list = new List<string>();
            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                if (list.Count >= 400) break;
                var name = Path.GetFileName(child);
                if (SkipNames.Contains(name)) continue;
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch
                {
                    continue;
                }
                list.Add(child);
            }
            return list;
        }
        catch
        {
            return new List<string>();
        }
    }

    private static void DeepScan(Action<string?, string> consider, IProgress<string>? progress,
        CancellationToken ct)
    {
        var queue = new Queue<(string Dir, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew();

        foreach (var root in Roots())
        {
            if (visited.Add(root)) queue.Enqueue((root, 0));
        }

        var scanned = 0;
        while (queue.Count > 0)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(28)) break;
            if (scanned > 80000) break;
            if (ct.IsCancellationRequested) return;

            var (dir, depth) = queue.Dequeue();
            scanned++;

            if (IsClient(dir))
            {
                consider(dir, "scan");
                continue;
            }

            if (depth >= 4) continue;

            foreach (var child in ChildDirs(dir))
            {
                if (!visited.Add(child)) continue;
                queue.Enqueue((child, depth + 1));
            }

            if (scanned % 400 == 0) progress?.Report($"Scanning\u2026 {Path.GetFileName(dir)}");
        }
    }
}
