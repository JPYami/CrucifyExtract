using System.IO;

namespace CrucifyExtract.Services;

public sealed record DropEntry(string Path, bool IsFolder);

public static class PathExpander
{
    private const int MaxEntries = 400;

    public static List<DropEntry> Expand(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var results = new List<DropEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, bool isFolder)
        {
            if (results.Count >= MaxEntries) return;
            if (!seen.Add(path)) return;
            results.Add(new DropEntry(path, isFolder));
        }

        void AddArchive(string path)
        {
            if (!ArchiveExtractor.IsArchive(path)) return;

            var primary = ArchiveExtractor.PrimaryVolume(path);
            Add(primary, false);
        }

        void ScanFolder(string dir, int depth)
        {
            if (depth > 3 || results.Count >= MaxEntries) return;
            if (ct.IsCancellationRequested) return;

            if (HasToc(dir))
            {
                Add(dir, true);
                return;
            }

            try
            {
                var iface = Directory.EnumerateDirectories(dir)
                    .FirstOrDefault(d => Path.GetFileName(d).Equals("Interface", StringComparison.OrdinalIgnoreCase));
                if (iface != null)
                {
                    var addons = Directory.EnumerateDirectories(iface)
                        .FirstOrDefault(d => Path.GetFileName(d).Equals("AddOns", StringComparison.OrdinalIgnoreCase));
                    if (addons != null)
                    {
                        ScanFolder(addons, depth + 1);
                        return;
                    }
                }
            }
            catch
            {
                // ignored
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (ArchiveExtractor.IsArchive(file)) AddArchive(file);
                }
            }
            catch
            {
                // ignored
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(child);
                    if (name.StartsWith(".", StringComparison.Ordinal) ||
                        name.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase))
                        continue;
                    ScanFolder(child, depth + 1);
                }
            }
            catch
            {
                // ignored
            }
        }

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            if (File.Exists(raw)) AddArchive(raw);
            else if (Directory.Exists(raw)) ScanFolder(raw, 0);
        }

        return results;
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
}
