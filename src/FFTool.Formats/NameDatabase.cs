using System.Globalization;
using System.Text.Json;

namespace FFTool.Formats;

/// <summary>Resolved metadata for an asset hash.</summary>
public readonly record struct AssetName(string Name, string? Type);

/// <summary>
/// Maps 64-bit asset keys → real names/types, imported from any source that knows
/// them (Cordycep/Saluki asset logs, community hash lists, or a manual CSV). Matching
/// is by the asset KEY only, so it's independent of the hash algorithm the game uses.
///
/// Accepted inputs:
///  • CSV / TXT: one entry per line, "hash,name[,type]" — hash as 0x-hex or decimal.
///    Also tolerates whitespace/comma/tab/colon separators and "name = hash" ordering.
///  • JSON: an object { "0x..": "name", ... } or an array of { hash, name, type }.
/// </summary>
public sealed class NameDatabase
{
    private readonly Dictionary<ulong, AssetName> _map = new();

    public int Count => _map.Count;

    public bool TryGet(ulong key, out AssetName name) => _map.TryGetValue(key, out name);

    public void AddOrUpdate(ulong key, string name, string? type = null) => _map[key] = new AssetName(name, type);

    /// <summary>Load a names file (CSV/TXT/JSON, auto-detected). Returns entries added.</summary>
    public int LoadFile(string path)
    {
        var text = File.ReadAllText(path);
        int before = _map.Count;
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            LoadJson(text);
        else
            LoadDelimited(text);
        return _map.Count - before;
    }

    private void LoadDelimited(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;

            var parts = line.Split([',', '\t', ';', ':', '='], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            // Find the field that parses as a hash; the other main field is the name.
            ulong key = 0; int keyIdx = -1;
            for (int i = 0; i < parts.Length; i++)
                if (TryParseHash(parts[i], out key)) { keyIdx = i; break; }
            if (keyIdx < 0) continue;

            string name = parts[keyIdx == 0 ? 1 : 0];
            string? type = parts.Length >= 3 ? parts[^1] != name && parts[^1] != parts[keyIdx] ? parts[^1] : null : null;
            if (name.Length > 0) _map[key] = new AssetName(name, type);
        }
    }

    private void LoadJson(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in root.EnumerateObject())
                if (TryParseHash(p.Name, out var key) && p.Value.ValueKind == JsonValueKind.String)
                    _map[key] = new AssetName(p.Value.GetString()!, null);
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in root.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                ulong key = 0; string? name = null, type = null;
                foreach (var f in el.EnumerateObject())
                {
                    var n = f.Name.ToLowerInvariant();
                    if (n is "hash" or "key" or "id" && TryParseHashJson(f.Value, out var k)) key = k;
                    else if (n is "name" or "asset" && f.Value.ValueKind == JsonValueKind.String) name = f.Value.GetString();
                    else if (n is "type" && f.Value.ValueKind == JsonValueKind.String) type = f.Value.GetString();
                }
                if (key != 0 && name is not null) _map[key] = new AssetName(name, type);
            }
        }
    }

    private static bool TryParseHashJson(JsonElement e, out ulong key)
    {
        key = 0;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetUInt64(out key)) return true;
        return e.ValueKind == JsonValueKind.String && TryParseHash(e.GetString()!, out key);
    }

    public static bool TryParseHash(string s, out ulong key)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out key);
        // bare hex if it contains a-f, else decimal
        if (s.Length is >= 8 and <= 16 && s.AsSpan().IndexOfAnyExcept("0123456789abcdefABCDEF") < 0
            && s.AsSpan().IndexOfAnyInRange('a', 'f') >= 0)
            return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out key);
        return ulong.TryParse(s, out key);
    }
}
