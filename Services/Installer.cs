using System.IO;

namespace CrucifyExtract.Services;

public sealed class InstallResult
{
    public bool Ok { get; init; }
    public string Summary { get; init; } = "";
    public string? Error { get; init; }
    public List<AddonPackage> Packages { get; init; } = new();
    public List<string> InstalledPaths { get; init; } = new();
    public int Skipped { get; init; }
    public bool Replaced { get; init; }
}

public static class Installer
{
    public static InstallResult Install(
        string source,
        bool isFolder,
        string addonsDir,
        bool overwrite,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        string? staging = null;

        try
        {
            Directory.CreateDirectory(addonsDir);

            string root;
            if (isFolder)
            {
                root = source;
            }
            else
            {
                progress?.Report(0.02);
                staging = Path.Combine(Path.GetTempPath(), "Crucify.Extract", Guid.NewGuid().ToString("n"));
                Directory.CreateDirectory(staging);
                ArchiveExtractor.Extract(source, staging, ct);
                root = staging;
            }

            ct.ThrowIfCancellationRequested();

            var packages = AddonAnalyzer.Analyze(root, Path.GetFileNameWithoutExtension(source), ct);
            if (packages.Count == 0)
            {
                return new InstallResult
                {
                    Ok = false,
                    Error = "No .toc files found \u2014 this doesn't look like an add-on."
                };
            }

            var total = Math.Max(1, packages.Sum(p => p.Files));
            var copied = 0;
            var installed = new List<string>();
            var skippedNames = new List<string>();
            var replaced = false;

            foreach (var package in packages)
            {
                ct.ThrowIfCancellationRequested();

                var target = Path.Combine(addonsDir, Sanitize(package.Name));

                if (Directory.Exists(target) || File.Exists(target))
                {
                    if (!overwrite)
                    {
                        skippedNames.Add(package.Name);
                        continue;
                    }

                    DeleteDirectory(target);
                    replaced = true;
                }

                CopyDirectory(package.Dir, target, ref copied, total, progress, ct);
                installed.Add(target);
            }

            progress?.Report(1);

            var summary = installed.Count switch
            {
                0 => skippedNames.Count > 0
                    ? "Already installed \u2014 left untouched"
                    : "Nothing to install",
                1 => $"Installed {packages.First(p => installed.Any(i => i.EndsWith(Sanitize(p.Name))))}",
                _ => $"Installed {installed.Count} add-ons"
            };

            if (installed.Count == 1)
            {
                var only = packages.FirstOrDefault(p => installed[0].EndsWith(Sanitize(p.Name)));
                if (only != null) summary = $"Installed {only.Name}";
            }

            if (replaced) summary += " \u00b7 replaced";

            return new InstallResult
            {
                Ok = installed.Count > 0,
                Summary = summary,
                Packages = packages,
                InstalledPaths = installed,
                Skipped = skippedNames.Count,
                Replaced = replaced,
                Error = installed.Count == 0 ? "Already installed \u2014 enable \u201cReplace existing add-ons\u201d to overwrite." : null
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new InstallResult { Ok = false, Error = ex.Message };
        }
        finally
        {
            if (staging != null) TryDelete(staging);
        }
    }

    private static void CopyDirectory(
        string from,
        string to,
        ref int copied,
        int total,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(to);

        foreach (var file in Directory.EnumerateFiles(from))
        {
            ct.ThrowIfCancellationRequested();
            var destination = Path.Combine(to, Path.GetFileName(file));

            try
            {
                File.Copy(file, destination, true);
            }
            catch (IOException)
            {
                // Give the file a moment in case an antivirus / indexer holds it.
                Thread.Sleep(120);
                File.Copy(file, destination, true);
            }

            copied++;
            progress?.Report(Math.Min(1, copied / (double)total));
        }

        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            ct.ThrowIfCancellationRequested();
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)), ref copied, total, progress, ct);
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        clean = clean.TrimEnd('.');
        return string.IsNullOrWhiteSpace(clean) ? "Addon" : clean;
    }

    private static void DeleteDirectory(string path)
    {
        if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return;

                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                Directory.Delete(path, true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(150);
            }
        }

        Directory.Delete(path, true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch
        {
            // temp cleanup is best effort
        }
    }
}
