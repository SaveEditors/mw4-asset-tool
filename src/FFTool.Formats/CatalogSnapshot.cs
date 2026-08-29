using System.Buffers.Binary;
using System.Text;
using FFTool.Formats.Kapi;

namespace FFTool.Formats;

/// <summary>One asset's identity + size fingerprint in a snapshot.</summary>
public readonly record struct SnapAsset(ulong Key, uint CompressedSize, uint DecompressedSize);

/// <summary>One package's assets in a snapshot.</summary>
public sealed class SnapshotPackage
{
    public ulong Guid { get; init; }
    public string Name { get; init; } = "";
    public int Version { get; init; }
    public int SubVersion { get; init; }
    public SnapAsset[] Assets { get; init; } = [];
}

/// <summary>
/// A point-in-time catalog of every package and asset in a game install — asset key plus its
/// compressed/decompressed sizes. Reads only the .xpak indices (no decompression), so it is
/// fast, and comparing two snapshots shows exactly what a game update added, removed, or
/// changed. Written as a compact binary file.
/// </summary>
public sealed class CatalogSnapshot
{
    public const uint Magic = 0x50414E53;   // "SNAP"
    private const uint FormatVersion = 1;

    public string GameDir { get; init; } = "";
    public DateTimeOffset TakenUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<SnapshotPackage> Packages { get; init; } = [];

    public long TotalAssets => Packages.Sum(p => (long)p.Assets.Length);

    /// <summary>Read every package index in <paramref name="gameDir"/> into a snapshot.</summary>
    public static CatalogSnapshot Capture(string gameDir)
    {
        var snap = new CatalogSnapshot { GameDir = gameDir, TakenUtc = DateTimeOffset.UtcNow };
        foreach (var set in KapiReader.DiscoverPackages(gameDir))
        {
            try
            {
                using var pkg = KapiPackage.Open(set);
                var assets = new SnapAsset[pkg.Entries.Count];
                for (int i = 0; i < assets.Length; i++)
                {
                    var e = pkg.Entries[i];
                    assets[i] = new SnapAsset(e.Key, e.CompressedSize, e.DecompressedSize);
                }
                snap.Packages.Add(new SnapshotPackage
                {
                    Guid = set.Guid,
                    Name = set.XpakPath is { } p ? Path.GetFileNameWithoutExtension(p) : set.Guid.ToString("x16"),
                    Version = set.Version,
                    SubVersion = set.SubVersion,
                    Assets = assets,
                });
            }
            catch { /* skip unreadable packages (e.g. a mid-update, locked, or new-format file) */ }
        }
        return snap;
    }

    public void Save(string path)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs, Encoding.UTF8);
        w.Write(Magic); w.Write(FormatVersion);
        w.Write(GameDir);
        w.Write(TakenUtc.UtcTicks);
        w.Write(Packages.Count);
        foreach (var p in Packages)
        {
            w.Write(p.Guid); w.Write(p.Name); w.Write(p.Version); w.Write(p.SubVersion);
            w.Write(p.Assets.Length);
            foreach (var a in p.Assets) { w.Write(a.Key); w.Write(a.CompressedSize); w.Write(a.DecompressedSize); }
        }
    }

    public static CatalogSnapshot Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs, Encoding.UTF8);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("Not a catalog snapshot file.");
        _ = r.ReadUInt32();                       // format version (only 1 today)
        string dir = r.ReadString();
        var taken = new DateTimeOffset(r.ReadInt64(), TimeSpan.Zero);
        int pkgs = r.ReadInt32();
        var list = new List<SnapshotPackage>(pkgs);
        for (int i = 0; i < pkgs; i++)
        {
            ulong guid = r.ReadUInt64(); string name = r.ReadString(); int ver = r.ReadInt32(); int sub = r.ReadInt32();
            int n = r.ReadInt32();
            var assets = new SnapAsset[n];
            for (int j = 0; j < n; j++) assets[j] = new SnapAsset(r.ReadUInt64(), r.ReadUInt32(), r.ReadUInt32());
            list.Add(new SnapshotPackage { Guid = guid, Name = name, Version = ver, SubVersion = sub, Assets = assets });
        }
        return new CatalogSnapshot { GameDir = dir, TakenUtc = taken, Packages = list };
    }

    /// <summary>Compare an OLD snapshot to a NEW one and report added/removed/changed assets.</summary>
    public static CatalogDiff Compare(CatalogSnapshot old, CatalogSnapshot @new)
    {
        var diff = new CatalogDiff { OldTakenUtc = old.TakenUtc, NewTakenUtc = @new.TakenUtc };
        // Match packages by NAME (the .xpak stem) — package GUIDs are NOT unique across a set.
        static Dictionary<string, SnapshotPackage> ByName(CatalogSnapshot s)
        {
            var d = new Dictionary<string, SnapshotPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in s.Packages) d[p.Name] = p;   // last wins if a name repeats
            return d;
        }
        var oldByName = ByName(old);
        var newByName = ByName(@new);

        foreach (var p in newByName.Values) if (!oldByName.ContainsKey(p.Name)) diff.AddedPackages.Add($"{p.Name} ({p.Assets.Length} assets)");
        foreach (var p in oldByName.Values) if (!newByName.ContainsKey(p.Name)) diff.RemovedPackages.Add($"{p.Name} ({p.Assets.Length} assets)");

        foreach (var np in newByName.Values)
        {
            if (!oldByName.TryGetValue(np.Name, out var op)) continue;
            var oldMap = new Dictionary<ulong, SnapAsset>(op.Assets.Length);
            foreach (var a in op.Assets) oldMap[a.Key] = a;
            var newKeys = new HashSet<ulong>(np.Assets.Length);

            var pd = new PackageDiff { Name = np.Name };
            foreach (var a in np.Assets)
            {
                newKeys.Add(a.Key);
                if (!oldMap.TryGetValue(a.Key, out var o)) pd.AddedKeys.Add(a.Key);
                else if (o.CompressedSize != a.CompressedSize || o.DecompressedSize != a.DecompressedSize) pd.ChangedKeys.Add(a.Key);
            }
            foreach (var a in op.Assets) if (!newKeys.Contains(a.Key)) pd.RemovedKeys.Add(a.Key);

            if (pd.AddedKeys.Count + pd.RemovedKeys.Count + pd.ChangedKeys.Count > 0) diff.Packages.Add(pd);
        }
        return diff;
    }
}

/// <summary>Per-package asset changes between two snapshots.</summary>
public sealed class PackageDiff
{
    public string Name { get; init; } = "";
    public List<ulong> AddedKeys { get; } = [];
    public List<ulong> RemovedKeys { get; } = [];
    public List<ulong> ChangedKeys { get; } = [];
}

/// <summary>The result of comparing two catalog snapshots.</summary>
public sealed class CatalogDiff
{
    public DateTimeOffset OldTakenUtc { get; init; }
    public DateTimeOffset NewTakenUtc { get; init; }
    public List<string> AddedPackages { get; } = [];
    public List<string> RemovedPackages { get; } = [];
    public List<PackageDiff> Packages { get; } = [];

    public int TotalAdded => Packages.Sum(p => p.AddedKeys.Count);
    public int TotalRemoved => Packages.Sum(p => p.RemovedKeys.Count);
    public int TotalChanged => Packages.Sum(p => p.ChangedKeys.Count);
    public bool AnyChange => AddedPackages.Count + RemovedPackages.Count + Packages.Count > 0;

    /// <summary>A human-readable diff report; <paramref name="maxKeysPerList"/> caps key listings.</summary>
    public string ToReport(int maxKeysPerList = 200)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Catalog diff  {OldTakenUtc:u}  ->  {NewTakenUtc:u}");
        sb.AppendLine($"  added assets:   {TotalAdded}");
        sb.AppendLine($"  removed assets: {TotalRemoved}");
        sb.AppendLine($"  changed assets: {TotalChanged}");
        if (AddedPackages.Count > 0) sb.AppendLine($"  new packages:     {string.Join(", ", AddedPackages)}");
        if (RemovedPackages.Count > 0) sb.AppendLine($"  removed packages: {string.Join(", ", RemovedPackages)}");
        if (!AnyChange) sb.AppendLine("  (no changes)");

        foreach (var p in Packages.OrderByDescending(p => p.AddedKeys.Count + p.RemovedKeys.Count + p.ChangedKeys.Count))
        {
            sb.AppendLine();
            sb.AppendLine($"[{p.Name}]  +{p.AddedKeys.Count} added  -{p.RemovedKeys.Count} removed  ~{p.ChangedKeys.Count} changed");
            void Dump(string label, List<ulong> keys)
            {
                if (keys.Count == 0) return;
                sb.AppendLine($"  {label}:");
                foreach (var k in keys.Take(maxKeysPerList)) sb.AppendLine($"    0x{k:x16}");
                if (keys.Count > maxKeysPerList) sb.AppendLine($"    … and {keys.Count - maxKeysPerList} more");
            }
            Dump("added", p.AddedKeys);
            Dump("changed", p.ChangedKeys);
            Dump("removed", p.RemovedKeys);
        }
        return sb.ToString();
    }
}
