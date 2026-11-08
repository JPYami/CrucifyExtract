using System.IO;
using System.Text.RegularExpressions;

namespace CrucifyExtract.Services;

public sealed record AddonPackage(string Name, string Dir, string? Title, string? Version, int Files);

public static class AddonAnalyzer
{
    private static readonly Regex ColorCode = new(@"\|c[0-9a-fA-F]{8}|\|r|\|T[^|]*\|t", RegexOptions.Compiled);

    public static List<AddonPackage> Analyze(string root, string fallbackName, CancellationToken ct = default)
    {
        var scanRoot = root;

        var iface = FindChildDir(root, "Interface");
        if (iface != null)
        {
            var addons = FindChildDir(iface, "AddOns");
            if (addons != null) scanRoot = addons;
        }

        var tocDirs = new List<string>();
        Walk(scanRoot, 0, tocDirs, ct);

        if (tocDirs.Count == 0)
        {
            var tops = ChildDirs(scanRoot);
            if (tops.Count == 1)
            {
                tocDirs.Add(tops[0]);
            }
            else if (HasFiles(scanRoot))
            {
                tocDirs.Add(scanRoot);
            }
            else
            {
                return new List<AddonPackage>();
            }
        }

        var packages = new List<AddonPackage>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootFull = Path.GetFullPath(root).TrimEnd('\\');

        foreach (var dir in tocDirs)
        {
            ct.ThrowIfCancellationRequested();

            var dirFull = Path.GetFullPath(dir).TrimEnd('\\');
            var name = Path.GetFileName(dirFull);

            if (string.IsNullOrWhiteSpace(name) || dirFull.Equals(rootFull, StringComparison.OrdinalIgnoreCase))
                name = fallbackName;

            name = name.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = "Addon";

            var unique = name;
            var counter = 2;
            while (!used.Add(unique)) unique = $"{name} ({counter++})";

            var (title, version) = ReadToc(dir);
            packages.Add(new AddonPackage(unique, dir, title, version, CountFiles(dir)));
        }

        return packages;
    }

    private static void Walk(string dir, int depth, List<string> tocDirs, CancellationToken ct)
    {
        if (depth > 6) return;
        if (ct.IsCancellationRequested) return;

        if (HasToc(dir))
        {
            tocDirs.Add(dir);
            return;
        }

        foreach (var child in ChildDirs(dir))
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.StartsWith(".", StringComparison.Ordinal)) continue;
            Walk(child, depth + 1, tocDirs, ct);
        }
    }

    private static bool HasToc(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.toc").Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool HasFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir).Any();
        }
        catch
        {
            return false;
        }
    }

    private static List<string> ChildDirs(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static string? FindChildDir(string parent, string name)
    {
        try
        {
            return Directory.EnumerateDirectories(parent)
                .FirstOrDefault(d => Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static int CountFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count();
        }
        catch
        {
            return 1;
        }
    }

    private static (string? Title, string? Version) ReadToc(string dir)
    {
        string? title = null;
        string? version = null;

        try
        {
            var toc = Directory.EnumerateFiles(dir, "*.toc").FirstOrDefault();
            if (toc == null) return (null, null);

            foreach (var line in File.ReadLines(toc).Take(80))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("## Title:", StringComparison.OrdinalIgnoreCase))
                    title = CleanText(trimmed[9..].Trim());
                else if (trimmed.StartsWith("## Version:", StringComparison.OrdinalIgnoreCase))
                    version = CleanText(trimmed[11..].Trim());
            }
        }
        catch
        {
            // ignored
        }

        return (string.IsNullOrWhiteSpace(title) ? null : title,
            string.IsNullOrWhiteSpace(version) ? null : version);
    }

    private static string CleanText(string value) => ColorCode.Replace(value, "").Trim();
}
