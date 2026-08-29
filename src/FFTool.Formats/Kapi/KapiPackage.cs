namespace FFTool.Formats.Kapi;

/// <summary>
/// A loaded KAPI package: its asset hash table (from the .xpak index) plus a
/// data reader over the paired .xsub files. Enumerate <see cref="Entries"/> and
/// call <see cref="Extract"/> to pull raw asset bytes.
/// </summary>
public sealed class KapiPackage : IDisposable
{
    private readonly XsubDataReader _data;

    private KapiPackage(PackageSet set, KapiIndexHeader index,
                        IReadOnlyList<KapiAssetEntry> entries, XsubDataReader data)
    {
        Set = set; Index = index; Entries = entries; _data = data;
    }

    public PackageSet Set { get; }
    public KapiIndexHeader Index { get; }
    public IReadOnlyList<KapiAssetEntry> Entries { get; }

    /// <summary>Load a package's hash table and open its data files.</summary>
    public static KapiPackage Open(PackageSet set)
    {
        if (set.XpakPath is null)
            throw new InvalidOperationException("Package has no .xpak index to read.");
        if (set.XsubPaths.Count == 0)
            throw new InvalidOperationException("Package has no .xsub data files.");

        using var fs = new FileStream(set.XpakPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var full = new byte[KapiIndexHeader.FullSize];
        fs.ReadExactly(full);
        var index = KapiIndexHeader.Read(full);

        // Order the .xsub data files by the xpak's data-file INDEX TABLE (not filename).
        // The table sits at 0x800: one 14-byte entry per data file {GUID u64, u32, index u16}.
        byte[] dfTable = [];
        if (KapiHeader.TryParse(full, out var kh) && kh.DataFileCount is > 0 and <= 4096)
        {
            dfTable = new byte[(int)kh.DataFileCount * 14];
            fs.Position = 0x800;
            fs.ReadExactly(dfTable);
        }
        var orderedXsub = OrderDataFiles(set, dfTable);

        var entries = new List<KapiAssetEntry>((int)index.HashCount);
        if (index.HashCount > 0)
        {
            var table = new byte[index.HashCount * (ulong)KapiAssetEntry.Size];
            fs.Position = (long)index.HashOffset;
            fs.ReadExactly(table);
            for (int i = 0; i < (int)index.HashCount; i++)
                entries.Add(KapiAssetEntry.Read(table.AsSpan(i * KapiAssetEntry.Size)));
        }

        var data = new XsubDataReader(orderedXsub);
        return new KapiPackage(set, index, entries, data);
    }

    /// <summary>
    /// Order the .xsub paths by the data-file table (14-byte entries: {GUID u64, u32,
    /// index u16}), matching each file by the GUID in its header (+0x20). Falls back to
    /// filename order if the table is missing or any file is unresolved.
    /// </summary>
    private static IReadOnlyList<string> OrderDataFiles(PackageSet set, byte[] table)
    {
        try
        {
            const int entrySize = 14;
            if (table.Length < entrySize) return set.XsubPaths;

            var guidToIndex = new Dictionary<ulong, int>();
            for (int o = 0; o + entrySize <= table.Length; o += entrySize)
            {
                ulong guid = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(o));
                ushort idx = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(o + 12));
                guidToIndex[guid] = idx;
            }
            if (guidToIndex.Count == 0) return set.XsubPaths;

            // Position each file at its TRUE data-file index; leave gaps null so the
            // packed fileIndex (offset >> 30) always resolves to the correct file even
            // when some .xsub files are not installed.
            int maxIdx = -1;
            var byIndex = new Dictionary<int, string>();
            foreach (var path in set.XsubPaths)
            {
                ulong g = KapiReader.ReadHeader(path).Guid;
                if (!guidToIndex.TryGetValue(g, out var idx)) return set.XsubPaths; // unresolved → fallback
                byIndex[idx] = path;
                if (idx > maxIdx) maxIdx = idx;
            }
            var ordered = new string?[maxIdx + 1];
            foreach (var (idx, path) in byIndex) ordered[idx] = path;
            return ordered;
        }
        catch { return set.XsubPaths; }
    }

    /// <summary>Extract one asset's raw (decompressed) bytes.</summary>
    public byte[] Extract(KapiAssetEntry e) => _data.Extract(e);

    /// <summary>Raw object-header bytes (diagnostics / RE).</summary>
    public byte[] ReadObjectHeader(KapiAssetEntry e, int n = 48) => _data.ReadObjectHeader(e, n);

    /// <summary>Whether the object is wrapped (has the cache-id header + block table).</summary>
    public bool IsWrapped(KapiAssetEntry e) => _data.IsWrapped(e);

    /// <summary>False when the entry's offset points beyond the installed data (CDN-streamed).</summary>
    public bool IsOnDisk(KapiAssetEntry e) => _data.InRange(e.Offset);

    public void Dispose() => _data.Dispose();
}
