using K4os.Compression.LZ4;
using FFTool.Native;

namespace FFTool.Formats.Kapi;

/// <summary>Thrown when an asset object cannot be extracted with the standard path.</summary>
public sealed class KapiExtractException(string message) : Exception(message);

/// <summary>
/// Reads and decompresses asset objects from a package's ordered .xsub data
/// files. Offsets from the hash table are global across the concatenated data
/// files. Object layout (Greyhound XSUBCacheV3): cache-id u64 @+2, block count
/// u8 @+22, then <see cref="XsubBlock"/>[count]; each block seeks to
/// BlockOffset (relative to the object), reads CompressedSize and decompresses
/// into the result at DecompressedOffset.
/// </summary>
public sealed class XsubDataReader : IDisposable
{
    // The asset offset is bit-packed: high bits = data-file index, low 30 bits = the
    // byte offset within that file (each .xsub is < 1 GiB = 2^30). The files must be
    // supplied in data-file-table index order so index N maps to _paths[N].
    private const int LocalBits = 30;
    private const ulong LocalMask = (1UL << LocalBits) - 1;   // 0x3FFFFFFF

    private readonly string?[] _paths;
    private readonly long[] _sizes;
    private readonly FileStream?[] _streams;
    private readonly object _gate = new();

    // Paths are positioned by TRUE data-file index; a slot may be null when that
    // .xsub is not installed. Sizes are -1 for missing slots.
    public XsubDataReader(IReadOnlyList<string?> xsubPaths)
    {
        _paths = xsubPaths.ToArray();
        _sizes = _paths.Select(p => p is not null && File.Exists(p) ? new FileInfo(p).Length : -1).ToArray();
        _streams = new FileStream?[_paths.Length];
        TotalSize = _sizes.Where(s => s > 0).Sum();
    }

    public long TotalSize { get; }

    /// <summary>True when the packed offset resolves to a byte inside an installed data file.</summary>
    public bool InRange(ulong offset)
    {
        ulong file = offset >> LocalBits;          // validate as ulong before narrowing
        long local = (long)(offset & LocalMask);
        return file < (ulong)_paths.Length && _paths[(int)file] is not null && local < _sizes[(int)file];
    }

    private (int file, long local) Locate(ulong offset)
    {
        ulong fileU = offset >> LocalBits;
        long local = (long)(offset & LocalMask);
        if (fileU >= (ulong)_paths.Length || _paths[(int)fileU] is null || local >= _sizes[(int)fileU])
            throw new KapiExtractException($"Offset 0x{offset:x} outside installed data files.");
        return ((int)fileU, local);
    }

    private FileStream Stream(int i)
    {
        lock (_gate)
            return _streams[i] ??= new FileStream(_paths[i]!, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1 << 16, FileOptions.RandomAccess);
    }

    /// <summary>Read the raw object header bytes at an entry's offset (for RE/diagnostics).</summary>
    public byte[] ReadObjectHeader(KapiAssetEntry e, int n = 48)
    {
        var (file, local) = Locate(e.Offset);
        var fs = Stream(file);
        var buf = new byte[n];
        lock (_gate) { fs.Position = local; int r = fs.Read(buf, 0, n); if (r < n) Array.Resize(ref buf, r); }
        return buf;
    }

    /// <summary>True if the object at this entry is wrapped (cache-id matches the key).</summary>
    public bool IsWrapped(KapiAssetEntry e)
    {
        var h = ReadObjectHeader(e, 10);
        return h.Length >= 10 && BitConverter.ToUInt64(h, 2) == e.Key;
    }

    /// <summary>Extract and decompress one asset into its raw bytes.</summary>
    public byte[] Extract(KapiAssetEntry e)
    {
        var (file, local) = Locate(e.Offset);
        var fs = Stream(file);

        Span<byte> head = stackalloc byte[23];
        lock (_gate)
        {
            fs.Position = local;
            fs.ReadExactly(head);
        }

        // Three on-disk layouts (all handled — no asset is skipped):
        //  1. Wrapped: cache-id (u64 @+2) == key → block-table object (below).
        //  2. Raw:     compressed == decompressed size → asset bytes stored inline.
        //  3. Headerless-compressed: a bare Oodle/LZ4 stream at the offset.
        ulong cacheId = BitConverter.ToUInt64(head[2..10]);
        if (cacheId != e.Key)
            return ExtractUnwrapped(fs, local, e);

        int blockCount = head[22];   // u8: 1..255
        if (blockCount == 0)
            throw new KapiExtractException("Object declares zero blocks.");

        var tbl = new byte[blockCount * XsubBlock.Size];
        lock (_gate)
        {
            fs.Position = local + 23;
            fs.ReadExactly(tbl);
        }

        long total = e.DecompressedSize;
        if (total is <= 0 or > 512 * 1024 * 1024)   // guard against corrupt metadata
            throw new KapiExtractException($"Implausible decompressed size {total}.");
        var result = new byte[total];

        for (int i = 0; i < blockCount; i++)
        {
            var blk = XsubBlock.Read(tbl.AsSpan(i * XsubBlock.Size));
            long doff = blk.DecompressedOffset;      // use long to avoid overflow in the bounds check
            long ds = blk.DecompressedSize;
            if (doff < 0 || ds < 0 || doff + ds > total)
                throw new KapiExtractException("Block exceeds declared decompressed size.");

            var cdata = new byte[blk.CompressedSize];
            lock (_gate)
            {
                fs.Position = local + blk.BlockOffset;
                fs.ReadExactly(cdata);
            }

            switch (blk.Compression)
            {
                case (byte)CompAlgo.None:
                    Array.Copy(cdata, 0, result, doff, ds);
                    break;
                case (byte)CompAlgo.Lz4:
                    int n = LZ4Codec.Decode(cdata, 0, cdata.Length, result, (int)doff, (int)ds);
                    if (n != ds) throw new KapiExtractException($"LZ4 decoded {n}, expected {ds}.");
                    break;
                case (byte)CompAlgo.Oodle:
                    var d = Oodle.Decompress(cdata, (int)ds);
                    Array.Copy(d, 0, result, doff, ds);
                    break;
                default:
                    throw new KapiExtractException($"Unknown compression 0x{blk.Compression:x}.");
            }
        }
        return result;
    }

    /// <summary>
    /// Handle the non-wrapped layouts: raw inline bytes (compressed==decompressed)
    /// or a bare Oodle/LZ4 stream (no per-object block table).
    /// </summary>
    private byte[] ExtractUnwrapped(FileStream fs, long local, KapiAssetEntry e)
    {
        int csize = (int)e.CompressedSize;
        int dsize = (int)e.DecompressedSize;
        if (csize <= 0 || dsize <= 0)
            throw new KapiExtractException($"Bad sizes c={csize} d={dsize} at 0x{e.Offset:x}.");

        var comp = new byte[csize];
        lock (_gate) { fs.Position = local; fs.ReadExactly(comp); }

        // 2. Stored raw / uncompressed.
        if (csize == dsize) return comp;

        // 3. Bare Oodle stream.
        try
        {
            var d = Oodle.Decompress(comp, dsize);
            if (d.Length == dsize) return d;
        }
        catch { /* fall through to LZ4 */ }

        // 3b. Bare LZ4 stream.
        try
        {
            var outBuf = new byte[dsize];
            int n = LZ4Codec.Decode(comp, 0, comp.Length, outBuf, 0, dsize);
            if (n == dsize) return outBuf;
        }
        catch { /* unresolved */ }

        throw new KapiExtractException(
            $"Unrecognized asset layout at 0x{e.Offset:x} (c={csize}, d={dsize}).");
    }

    public void Dispose()
    {
        lock (_gate)
            foreach (var s in _streams) s?.Dispose();
    }
}
