using System.IO;
using System.Text.Json;

namespace FFTool.App;

/// <summary>Persisted user preferences (JSON under %LOCALAPPDATA%\MW4FFTool).</summary>
public sealed class Settings
{
    public string? LastGameDir { get; set; }
    public double WindowWidth { get; set; } = 1360;
    public double WindowHeight { get; set; } = 760;
    // Nullable so "unset" serializes as JSON null — double.NaN throws in System.Text.Json,
    // which would make Save() silently fail and drop ALL settings on a fresh install.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }
    public string? LastPackage { get; set; }
    public string? ThumbSizeName { get; set; }
    public bool GridMode { get; set; }
    public List<string> RecentFolders { get; set; } = [];

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
