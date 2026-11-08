using System.IO;
using System.Text.Json;

namespace CrucifyExtract.Services;

public sealed class Settings
{
    public string? GamePath { get; set; }
    public bool Overwrite { get; set; } = true;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Crucify.Extract");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // Corrupt settings should never stop the app from starting.
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Non-fatal.
        }
    }
}
