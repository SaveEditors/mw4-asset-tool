using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FFTool.Formats;
using FFTool.Formats.Kapi;

namespace FFTool.App;

/// <summary>
/// Decodes small texture thumbnails for the grid view. Results are cached in memory
/// AND on disk (keyed by asset hash), so re-scrolling and re-opening a package are
/// instant. A background prefetch decodes the whole filtered set ahead of the scroll
/// position so cells are ready before they come into view. Only image-shaped blobs
/// are attempted.
/// </summary>
public sealed class ThumbnailProvider
{
    public readonly record struct Thumb(BitmapSource? Image, double Score, TextureGuess? Guess = null);

    /// <summary>The interpretation the grid chose for an asset (so the inspector can match it).</summary>
    public bool TryGetChosenGuess(ulong key, out TextureGuess guess)
    {
        if (_cache.TryGetValue(key, out var t) && t.Guess is { } g) { guess = g; return true; }
        guess = default; return false;
    }

    private readonly KapiPackage _pkg;
    private readonly Dispatcher _ui;
    private readonly SemaphoreSlim _limit = new(Environment.ProcessorCount);
    private readonly ConcurrentDictionary<ulong, Thumb> _cache = new();
    private readonly object _gate = new();
    private readonly string _diskDir;
    private const int ThumbPx = 96;
    private const uint DiskMagic = 0x334D5754; // "TWM3" (v3 header carries the source fingerprint)

    private CancellationTokenSource? _prefetchCts;

    public ThumbnailProvider(KapiPackage pkg, Dispatcher ui)
    {
        _pkg = pkg; _ui = ui;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MW4FFTool", "thumbs", pkg.Set.Guid.ToString("x16"));
        _diskDir = root;
        try { Directory.CreateDirectory(_diskDir); } catch { }
    }

    /// <summary>Fast O(1) check: could this blob size be a texture at all?</summary>
    public static bool IsImageShaped(long size) => DimensionGuesser.CouldBeImage(size);

    /// <summary>Decode (or fetch cached) a thumbnail + its image-likelihood score.</summary>
    public async Task<Thumb> GetAsync(KapiAssetEntry e)
    {
        if (_cache.TryGetValue(e.Key, out var cached)) return cached;
        if (!IsImageShaped(e.DecompressedSize)) { var t = new Thumb(null, 0); _cache[e.Key] = t; return t; }

        await _limit.WaitAsync();
        try
        {
            if (_cache.TryGetValue(e.Key, out cached)) return cached;
            // Produce returns null on a TRANSIENT failure — do not cache those (allow retry).
            var thumb = await Task.Run(() => Produce(e));
            if (thumb is { } t) { _cache[e.Key] = t; return t; }
            return new Thumb(null, 0);
        }
        finally { _limit.Release(); }
    }

    /// <summary>
    /// Background-decode thumbnails for a whole set of entries so scrolling is smooth.
    /// Cancels any previous prefetch. <paramref name="onProgress"/> reports
    /// (done, total) periodically on a background thread.
    /// </summary>
    public void Prefetch(IReadOnlyList<KapiAssetEntry> entries, Action<int, int>? onProgress = null)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _prefetchCts?.Cancel();
            _prefetchCts?.Dispose();
            cts = _prefetchCts = new CancellationTokenSource();
        }
        var ct = cts.Token;
        int total = entries.Count, done = 0;
        _ = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(entries,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
                    async (e, token) =>
                    {
                        if (!_cache.ContainsKey(e.Key))
                        {
                            if (!IsImageShaped(e.DecompressedSize)) _cache[e.Key] = new Thumb(null, 0);
                            else
                            {
                                await _limit.WaitAsync(token);
                                try { if (!_cache.ContainsKey(e.Key) && Produce(e) is { } t) _cache[e.Key] = t; }
                                catch { }
                                finally { _limit.Release(); }
                            }
                        }
                        int d = Interlocked.Increment(ref done);
                        if (onProgress is not null && (d & 0xFF) == 0) onProgress(d, total);
                    }).ConfigureAwait(false);
                onProgress?.Invoke(total, total);
            }
            catch (OperationCanceledException) { }
            catch { /* prefetch is best-effort */ }
        }, ct);
    }

    public void StopPrefetch() { lock (_gate) _prefetchCts?.Cancel(); }

    /// <summary>
    /// Produce a thumbnail. Returns a Thumb (image or confirmed non-image) to cache, or
    /// null on a TRANSIENT failure (I/O/decoder) that should be retried, not cached.
    /// </summary>
    private Thumb? Produce(KapiAssetEntry e)
    {
        var disk = LoadDisk(e);
        if (disk is not null) return disk.Value;
        try
        {
            var blob = _pkg.Extract(e);
            // Scored multi-candidate decode (cheap via block subsampling) so the grid tile
            // and the inspector agree on the interpretation.
            var best = TextureDecoder.DecodeFastThumbnail(blob);
            if (best is not { } b) { SaveDisk(e, null, 0); return new Thumb(null, 0); }  // confirmed non-image
            var (w, h, bgra) = DownscaleFast(b.Image);   // manual box downscale + force opaque
            var bmp = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, bgra, w * 4);
            bmp.Freeze();
            SaveDisk(e, (w, h, bgra), b.Score, b.Guess);
            return new Thumb(bmp, b.Score, b.Guess);
        }
        catch { return null; }   // transient — don't cache, allow retry
    }

    // ── Disk cache (28-byte header) ──────────────────────────────────────────────
    //  0 magic u32 · 4 score f32 · 8 thumbW u16 · 10 thumbH u16
    // 12 guessFmt u8 · 13 pad · 14 guessW u16 · 16 guessH u16 · 18 pad
    // 20 srcCompressedSize u32 · 24 srcDecompressedSize u32   (source fingerprint)
    // then bgra[thumbW*thumbH*4]
    // The fingerprint lets a cached thumbnail auto-invalidate after a GAME UPDATE: if the
    // asset's compressed/decompressed size changed, the cache is a miss and it re-decodes.
    private const int DiskHeader = 28;
    private string DiskPath(ulong key) => Path.Combine(_diskDir, $"{key:x16}.twm");

    private void SaveDisk(KapiAssetEntry e, (int w, int h, byte[] bgra)? img, double score, TextureGuess? guess = null)
    {
        try
        {
            using var fs = File.Create(DiskPath(e.Key));
            Span<byte> hdr = stackalloc byte[DiskHeader];
            BinaryPrimitives.WriteUInt32LittleEndian(hdr, DiskMagic);
            BinaryPrimitives.WriteSingleLittleEndian(hdr[4..], (float)score);
            BinaryPrimitives.WriteUInt16LittleEndian(hdr[8..], (ushort)(img?.w ?? 0));
            BinaryPrimitives.WriteUInt16LittleEndian(hdr[10..], (ushort)(img?.h ?? 0));
            hdr[12] = (byte)(guess?.Format ?? ImageFormat.Unknown);
            BinaryPrimitives.WriteUInt16LittleEndian(hdr[14..], (ushort)(guess?.Width ?? 0));
            BinaryPrimitives.WriteUInt16LittleEndian(hdr[16..], (ushort)(guess?.Height ?? 0));
            BinaryPrimitives.WriteUInt32LittleEndian(hdr[20..], e.CompressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(hdr[24..], e.DecompressedSize);
            fs.Write(hdr);
            if (img is { } i) fs.Write(i.bgra);
        }
        catch { }
    }

    private Thumb? LoadDisk(KapiAssetEntry e)
    {
        try
        {
            var path = DiskPath(e.Key);
            if (!File.Exists(path)) return null;
            var data = File.ReadAllBytes(path);
            if (data.Length < DiskHeader || BinaryPrimitives.ReadUInt32LittleEndian(data) != DiskMagic) return null;
            // Source fingerprint must match the current asset, else the game updated it → re-decode.
            uint cs = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20));
            uint ds = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24));
            if (cs != e.CompressedSize || ds != e.DecompressedSize) return null;
            float score = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(4));
            int w = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
            int h = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10));
            var fmt = (ImageFormat)data[12];
            int gw = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
            int gh = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(16));
            TextureGuess? guess = fmt != ImageFormat.Unknown && gw > 0 && gh > 0
                ? new TextureGuess(fmt, gw, gh, false) : null;
            if (w == 0 || h == 0) return new Thumb(null, score, guess);  // cached non-image / no thumb
            int need = w * h * 4;
            if (data.Length < DiskHeader + need) return null;
            var bmp = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32,
                null, data.AsSpan(DiskHeader, need).ToArray(), w * 4);
            bmp.Freeze();
            return new Thumb(bmp, score, guess);
        }
        catch { return null; }
    }

    /// <summary>
    /// Thread-safe BOX-AVERAGE downscale to a small BGRA buffer with forced-opaque alpha.
    /// Averages every source pixel that maps to each destination pixel (proper area filter),
    /// so thumbnails are smooth and match the inspector preview instead of aliased nearest-
    /// neighbour sampling. One pass over the source, so it stays fast for prefetch.
    /// </summary>
    private static (int w, int h, byte[] bgra) DownscaleFast(DecodedImage img)
    {
        int sw = img.Width, sh = img.Height;
        int max = Math.Max(sw, sh);
        if (max <= ThumbPx)
        {
            // Already small — just force opaque, return as-is.
            var copy = (byte[])img.Bgra.Clone();
            for (int i = 3; i < copy.Length; i += 4) copy[i] = 255;
            return (sw, sh, copy);
        }
        double scale = (double)ThumbPx / max;
        int tw = Math.Max(1, (int)Math.Round(sw * scale)), th = Math.Max(1, (int)Math.Round(sh * scale));
        var dst = new byte[tw * th * 4];
        var src = img.Bgra;
        for (int y = 0; y < th; y++)
        {
            int sy0 = (int)((long)y * sh / th), sy1 = (int)((long)(y + 1) * sh / th);
            if (sy1 <= sy0) sy1 = sy0 + 1; if (sy1 > sh) sy1 = sh;
            int drow = y * tw * 4;
            for (int x = 0; x < tw; x++)
            {
                int sx0 = (int)((long)x * sw / tw), sx1 = (int)((long)(x + 1) * sw / tw);
                if (sx1 <= sx0) sx1 = sx0 + 1; if (sx1 > sw) sx1 = sw;
                long b = 0, g = 0, r = 0; int n = 0;
                for (int yy = sy0; yy < sy1; yy++)
                {
                    int srow = yy * sw * 4;
                    for (int xx = sx0; xx < sx1; xx++)
                    {
                        int so = srow + xx * 4;
                        b += src[so]; g += src[so + 1]; r += src[so + 2]; n++;
                    }
                }
                int doff = drow + x * 4;
                dst[doff] = (byte)(b / n); dst[doff + 1] = (byte)(g / n); dst[doff + 2] = (byte)(r / n); dst[doff + 3] = 255;
            }
        }
        return (tw, th, dst);
    }
}
