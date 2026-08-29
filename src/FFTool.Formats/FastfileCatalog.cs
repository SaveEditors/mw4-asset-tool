using System.Text.RegularExpressions;

namespace FFTool.Formats;

/// <summary>A parsed game content package (from a readable .ff/.fp fastfile name).</summary>
public sealed record ContentEntry(
    string FileName, string Category, string Content, string Detail, string Locale, long Size)
{
    /// <summary>Human-readable size for the UI.</summary>
    public string SizeText
    {
        get
        {
            double d = Size; string[] u = ["B", "KB", "MB", "GB", "TB"]; int i = 0;
            while (d >= 1024 && i < u.Length - 1) { d /= 1024; i++; }
            return $"{d:0.#} {u[i]}";
        }
    }
}

/// <summary>
/// Reads the readable fastfile (.ff) names on disk and parses them into a friendly,
/// categorized content catalog — the only human-meaningful naming available statically
/// (individual asset names live in the encrypted .ff). Gives map/mode/reward names like
/// "Tumen", "Zodiac", "Actionpark", "Meltdown".
/// </summary>
public static partial class FastfileCatalog
{
    [GeneratedRegex(@"^(eng|ens|ww|srv|techsets)_")] private static partial Regex LocalePrefix();
    [GeneratedRegex(@"_[0-9a-f]{6,}.*$")] private static partial Regex HashSuffix();
    [GeneratedRegex(@"_-?\d+(_-?\d+)*(_tr)?$")] private static partial Regex CoordSuffix();

    // Friendly names for known content stems.
    private static readonly (string key, string name)[] KnownMaps =
    [
        ("tumen", "Tumen"), ("actionpark", "Actionpark"), ("mobility_trials", "Mobility Trials"),
        ("meltdown", "Meltdown (Campaign)"), ("zodiac", "Zodiac"), ("avalon", "Avalon"),
    ];

    public static IReadOnlyList<ContentEntry> Scan(string gameDir)
    {
        var list = new List<ContentEntry>();
        foreach (var path in Directory.EnumerateFiles(gameDir, "*.ff"))
        {
            var file = Path.GetFileName(path);
            long size = new FileInfo(path).Length;

            string locale = "";
            var m = LocalePrefix().Match(file);
            string body = file;
            if (m.Success) { locale = LocaleName(m.Groups[1].Value); body = file[m.Length..]; }
            body = Path.GetFileNameWithoutExtension(body);

            string stem = CoordSuffix().Replace(HashSuffix().Replace(body, ""), "");
            var (category, content, detail) = Classify(stem, body);
            list.Add(new ContentEntry(file, category, content, detail, locale, size));
        }
        return list;
    }

    private static string LocaleName(string p) => p switch
    {
        "eng" => "English", "ens" => "Spanish", "ww" => "Worldwide",
        "srv" => "Server", "techsets" => "Shaders", _ => p,
    };

    private static (string category, string content, string detail) Classify(string stem, string body)
    {
        string s = stem.ToLowerInvariant();

        // Map/mission content: mp_rex_<map>_..., sp_rex_<map>_...
        foreach (var (key, name) in KnownMaps)
            if (s.Contains(key))
            {
                string cat = s.StartsWith("sp_") ? "Campaign map" : "Multiplayer map";
                if (s.Contains("exfil") || s.Contains("infil") || s.Contains("_br")) cat = "Battle Royale";
                if (s.StartsWith("mp_rew_")) cat = "Reward / Event";
                return (cat, name, District(s));
            }

        if (s.StartsWith("mp_rew_")) return ("Reward / Event", Title(s.Replace("mp_rew_", "")), "");
        if (s.StartsWith("mp_rex_")) return ("Multiplayer map", Title(s.Replace("mp_rex_", "")), District(s));
        if (s.StartsWith("sp_rex_")) return ("Campaign", Title(s.Replace("sp_rex_", "")), District(s));
        if (s.StartsWith("mp_exfil") || s.StartsWith("mp_infil")) return ("Battle Royale", Title(s), "");
        if (s.Contains("frontend")) return ("Frontend / UI", "Frontend", body);
        if (s.StartsWith("global")) return ("Global (shared)", "Global", body);
        if (s.StartsWith("ingame")) return ("In-game (shared)", "In-game", body);
        if (s.StartsWith("code") || s.Contains("reloadable")) return ("Engine code", "Code", body);
        if (s.StartsWith("mtx")) return ("Store / cosmetics", "MTX", body);
        if (s == "boot") return ("Boot", "Boot", "");
        if (s.Contains("mp26") || s.Contains("wz26") || s.Contains("sp26") || s.Contains("codhq"))
            return ("Package", stem, "");
        return ("Other", Title(s), "");
    }

    private static string District(string s)
    {
        int i = s.IndexOf("_district");
        if (i > 0)
        {
            var before = s[..i];
            int j = before.LastIndexOf('_');
            return "District: " + Title(j >= 0 ? before[(j + 1)..] : before);
        }
        if (s.Contains("loading")) return "Loading screen";
        if (s.Contains("_cg")) return "Cutscene / geo";
        if (s.Contains("_ai")) return "AI subset";
        if (s.Contains("_entities")) return "Entities";
        return "";
    }

    private static string Title(string s) =>
        string.Join(' ', s.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpper(w[0]) + w[1..]));
}
