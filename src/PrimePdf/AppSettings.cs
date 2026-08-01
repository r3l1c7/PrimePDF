using System.IO;
using System.Text.Json;

namespace PrimePdf;

/// <summary>Small preferences file kept beside the saved signature in %AppData%\PrimePdf.</summary>
public sealed class AppSettings
{
    /// <summary>True once the user has been asked about making this the default PDF app.</summary>
    public bool AskedAboutDefaultApp { get; set; }

    /// <summary>Set when the user said "don't ask me again", so we never nag.</summary>
    public bool DeclinedDefaultApp { get; set; }

    /// <summary>Remembers the "Bigger text" choice between sessions.</summary>
    public double UiScale { get; set; } = 1.0;

    // ------------------------------------------------------------ persistence

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrimePdf");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable settings must never stop the app from starting.
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Preferences are a convenience; failing to store them is not worth an error.
        }
    }
}
