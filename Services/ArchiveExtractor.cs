using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace CrucifyExtract.Services;

public static class ArchiveExtractor
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rar", ".zip", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz"
    };

    public static bool IsArchive(string path)
    {
        try
        {
            return Extensions.Contains(Path.GetExtension(path));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns every volume of a split archive in order, or just the file itself.</summary>
    public static List<string> GetVolumes(string path)
    {
        var full = Path.GetFullPath(path);

        try
        {
            var detected = ArchiveFactory.GetFileParts(full)
                .Where(File.Exists)
                .Select(Path.GetFullPath)
                .ToList();

            if (detected.Count > 1) return detected;
        }
        catch
        {
            // fall back to our own naming rules
        }

        var dir = Path.GetDirectoryName(full);
        var name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(dir)) return new List<string> { full };

        try
        {
            var part = Regex.Match(name, @"^(?<b>.+?)\.part(?<n>\d+)\.rar$", RegexOptions.IgnoreCase);
            if (part.Success)
            {
                var stem = part.Groups["b"].Value;
                var volumes = new List<string>();
                for (var i = 1; i < 1000; i++)
                {
                    var candidate = Path.Combine(dir, $"{stem}.part{i}.rar");
                    if (!File.Exists(candidate)) break;
                    volumes.Add(candidate);
                }
                if (volumes.Count > 0) return volumes;
            }

            var legacy = Regex.Match(name, @"^(?<b>.+?)\.r(?<n>\d{2,3})$", RegexOptions.IgnoreCase);
            if (legacy.Success)
            {
                var stem = legacy.Groups["b"].Value;
                var first = Path.Combine(dir, stem + ".rar");
                if (File.Exists(first))
                {
                    var volumes = new List<string> { first };
                    for (var i = 0; i < 1000; i++)
                    {
                        var candidate = Path.Combine(dir, $"{stem}.r{i:00}");
                        if (!File.Exists(candidate)) break;
                        volumes.Add(candidate);
                    }
                    return volumes;
                }
            }
        }
        catch
        {
            // ignored
        }

        return new List<string> { full };
    }

    public static string PrimaryVolume(string path)
    {
        var volumes = GetVolumes(path);
        return volumes.Count > 0 ? volumes[0] : path;
    }

    public static void Extract(string archivePath, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);

        Exception? managedError = null;
        try
        {
            ExtractManaged(archivePath, destination, ct);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            managedError = ex;
        }

        try
        {
            if (string.Equals(Path.GetExtension(archivePath), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archivePath, destination, true);
                return;
            }
        }
        catch
        {
            // fall through
        }

        if (ExtractExternal(PrimaryVolume(archivePath), destination, ct)) return;

        throw new InvalidOperationException(Friendly(managedError), managedError);
    }

    private static string Friendly(Exception? error)
    {
        var message = error?.Message ?? "";

        if (message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
        {
            return "This archive is password protected. Remove the password and drop it again.";
        }

        if (message.Contains("corrupt", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("end of stream", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("crc", StringComparison.OrdinalIgnoreCase))
        {
            return "The archive looks incomplete or corrupt. Re-download it and try again.";
        }

        return string.IsNullOrWhiteSpace(message)
            ? "Couldn't read this archive."
            : "Couldn't read this archive: " + message;
    }

    private static void ExtractManaged(string archivePath, string destination, CancellationToken ct)
    {
        var volumes = GetVolumes(archivePath);
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        IArchive archive;
        if (volumes.Count > 1)
        {
            var parts = volumes.Select(v => new FileInfo(v)).ToList();
            archive = ArchiveFactory.OpenArchive(parts, new ReaderOptions());
        }
        else
        {
            archive = ArchiveFactory.OpenArchive(new FileInfo(Path.GetFullPath(archivePath)), new ReaderOptions());
        }

        using (archive)
        {
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.IsDirectory) continue;

                var key = (entry.Key ?? string.Empty).Replace('\\', '/').TrimStart('/');
                if (key.Length == 0) continue;
                if (key.Contains("..")) continue;
                if (key.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.Split('/').Any(p => p.StartsWith("._", StringComparison.Ordinal))) continue;

                var target = Path.GetFullPath(Path.Combine(root, key));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                using var input = entry.OpenEntryStream();
                using var output = File.Create(target);
                input.CopyTo(output, 81920);
            }
        }
    }

    private static bool ExtractExternal(string archivePath, string destination, CancellationToken ct)
    {
        foreach (var tool in ExternalTools())
        {
            ct.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(tool);
            var is7z = fileName.Contains("7z", StringComparison.OrdinalIgnoreCase);
            var isWinRar = fileName.Equals("WinRAR.exe", StringComparison.OrdinalIgnoreCase);

            var arguments = is7z
                ? $"x -y -o\"{destination}\" \"{archivePath}\""
                : $"x -o+ -y -p-{(isWinRar ? " -ibck" : "")} \"{archivePath}\" \"{destination}\\\"";

            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = tool,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(info);
                if (process == null) continue;

                _ = process.StandardOutput.ReadToEndAsync(ct);
                _ = process.StandardError.ReadToEndAsync(ct);
                process.WaitForExit(300000);

                if (process.HasExited && process.ExitCode == 0 &&
                    Directory.EnumerateFileSystemEntries(destination).Any())
                {
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // try the next tool
            }
        }

        return false;
    }

    private static IEnumerable<string> ExternalTools()
    {
        var candidates = new List<string>();

        void Add(string dir, params string[] names)
        {
            foreach (var name in names)
            {
                try
                {
                    var full = Path.Combine(dir, name);
                    if (File.Exists(full)) candidates.Add(full);
                }
                catch
                {
                    // ignored
                }
            }
        }

        string[] programDirs =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        foreach (var dir in programDirs.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            Add(Path.Combine(dir, "WinRAR"), "UnRAR.exe", "WinRAR.exe", "Rar.exe");
            Add(Path.Combine(dir, "7-Zip"), "7z.exe");
            Add(Path.Combine(dir, "NanaZip"), "NanaZipC.exe");
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
