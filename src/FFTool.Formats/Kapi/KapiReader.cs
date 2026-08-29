using System.Text.RegularExpressions;

namespace FFTool.Formats.Kapi;

/// <summary>
/// Discovers and opens KAPI packages. Pairing of an .xpak index with its .xsub
/// data files is done by filename stem (e.g. "codhq.xpak" ↔ "codhq-00000.xsub"),
/// which is the reliable real-world grouping; the header GUID is also captured.
/// </summary>
public sealed partial class KapiReader
{
    [GeneratedRegex(@"-\d{5}$")] private static partial Regex DataSuffix();

    public static KapiHeader ReadHeader(string path)
    {
        Span<byte> buf = stackalloc byte[KapiHeader.Size];
        using var fs = File.OpenRead(path);
        int read = fs.ReadAtLeast(buf, KapiHeader.Size, throwOnEndOfStream: false);
        if (read < KapiHeader.Size || !KapiHeader.TryParse(buf, out var h))
            throw new InvalidDataException($"Not a KAPI package: {path}");
        return h;
    }

    /// <summary>Stem used to pair index and data files ("codhq-00001" → "codhq").</summary>
    public static string StemOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return DataSuffix().Replace(name, string.Empty);
    }

    /// <summary>Group all .xpak/.xsub in a directory into package sets.</summary>
    public static IReadOnlyList<PackageSet> DiscoverPackages(string directory)
    {
        var xpaks = Directory.EnumerateFiles(directory, "*.xpak");
        var xsubs = Directory.EnumerateFiles(directory, "*.xsub");

        var byStem = new Dictionary<string, (string? xpak, List<string> xsub)>(StringComparer.OrdinalIgnoreCase);

        foreach (var x in xpaks)
        {
            var stem = StemOf(x);
            var e = byStem.TryGetValue(stem, out var v) ? v : (null, new List<string>());
            e.xpak = x;
            byStem[stem] = e;
        }
        foreach (var s in xsubs)
        {
            var stem = StemOf(s);
            var e = byStem.TryGetValue(stem, out var v) ? v : (null, new List<string>());
            e.xsub.Add(s);
            byStem[stem] = e;
        }

        var sets = new List<PackageSet>();
        foreach (var (_, e) in byStem)
        {
            var probe = e.xpak ?? (e.xsub.Count > 0 ? e.xsub[0] : null);
            if (probe is null) continue;

            KapiHeader h;
            try { h = ReadHeader(probe); }
            catch { continue; } // skip non-KAPI / malformed (NFR-005: graceful)

            e.xsub.Sort(StringComparer.OrdinalIgnoreCase);
            sets.Add(new PackageSet
            {
                Guid = h.Guid,
                XpakPath = e.xpak,
                XsubPaths = e.xsub,
                Version = h.Version,
                SubVersion = h.SubVersion,
            });
        }
        return sets;
    }
}
