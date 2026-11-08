using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CrucifyExtract.Services;

public static class SelfTest
{
    public static void Run(string[] args)
    {
        var archive = Value(args, "--archive");
        var folder = Value(args, "--folder");
        var dest = Value(args, "--dest") ?? Path.Combine(Path.GetTempPath(), "crucify-selftest");
        var overwrite = !args.Contains("--no-overwrite", StringComparer.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew();

        try
        {
            if (string.IsNullOrWhiteSpace(archive) && string.IsNullOrWhiteSpace(folder))
            {
                Emit(new { ok = false, error = "Provide --archive <file> or --folder <dir>" });
                return;
            }

            var source = archive ?? folder!;
            var result = Installer.Install(source, string.IsNullOrWhiteSpace(archive), dest, overwrite, null,
                CancellationToken.None);

            var files = result.InstalledPaths
                .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                .Select(f => Path.GetRelativePath(dest, f))
                .OrderBy(f => f)
                .ToList();

            Emit(new
            {
                ok = result.Ok,
                summary = result.Summary,
                error = result.Error,
                skipped = result.Skipped,
                replaced = result.Replaced,
                addons = result.Packages.Select(p => new { p.Name, p.Title, p.Version, p.Files }).ToList(),
                files,
                ms = watch.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            Emit(new { ok = false, error = ex.ToString(), ms = watch.ElapsedMilliseconds });
        }
    }

    public static void Detect(string[] args)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var clients = GameLocator.Detect();
            Emit(new
            {
                ok = clients.Count > 0,
                count = clients.Count,
                clients = clients.Select(c => new { root = c.Root, addons = c.AddonsPath, source = c.Source }).ToList(),
                ms = watch.ElapsedMilliseconds
            });
        }
        catch (Exception ex)
        {
            Emit(new { ok = false, error = ex.ToString(), ms = watch.ElapsedMilliseconds });
        }
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void Emit(object payload)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        Console.Out.Flush();
    }
}
