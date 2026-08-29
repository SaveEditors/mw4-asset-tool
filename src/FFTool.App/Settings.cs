using System.IO;
using System.Text.Json;

namespace FFTool.App;

/// <summary>Persisted user preferences (JSON under %LOCALAPPDATA%\MW4FFTool).</summary>
public sealed class Settings
{
    public string? LastGameDir { get; set; }
    public double WindowWidth { get; set; } = 1360;
    public double WindowHeight { get; set; } = 760;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MW4FFTool");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* corrupt settings → defaults */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* non-fatal */ }
    }
}
