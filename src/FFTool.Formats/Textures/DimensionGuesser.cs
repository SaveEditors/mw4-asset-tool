namespace FFTool.Formats;

/// <summary>A candidate interpretation of a headerless BCn blob.</summary>
public readonly record struct TextureGuess(ImageFormat Format, int Width, int Height, bool HasMips)
{
    public long BaseMipBytes => Format.MipSize(Width, Height);
    public override string ToString() => $"{Format} {Width}×{Height}{(HasMips ? " +mips" : "")}";
}

/// <summary>
/// xsub image blobs are raw BCn block data with no embedded dimensions (those
/// live in the .ff). This enumerates plausible (format,w,h) interpretations
/// whose byte length matches the blob exactly, ranked so the most likely
/// (square, common format) comes first — enabling a best-effort preview.
/// </summary>
public static class DimensionGuesser
{
    // Order = prior likelihood (most common IW10 texture formats first). The final pick
    // is decided by decode quality, so this only affects which are tried within `max`.
    private static readonly ImageFormat[] Formats =
    [
        ImageFormat.BC7, ImageFormat.BC1, ImageFormat.BC3, ImageFormat.BC5, ImageFormat.BC4,
        ImageFormat.R8G8B8A8, ImageFormat.BC2, ImageFormat.R8, ImageFormat.R8G8, ImageFormat.BC6H,
    ];

    private static long MipChain(ImageFormat f, int w, int h)
    {
        long total = 0;
        while (true)
        {
            total += f.MipSize(w, h);
            if (w == 1 && h == 1) break;
            w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
        }
        return total;
    }

    /// <summary>Ranked candidate interpretations for a blob of the given byte length.</summary>
    public static IReadOnlyList<TextureGuess> Guess(long size, int max = 24)
    {
        var hits = new List<TextureGuess>();
        foreach (var f in Formats)
        {
            for (int lw = 2; lw <= 13; lw++)      // 4 .. 8192
            for (int lh = 2; lh <= 13; lh++)
            {
                int w = 1 << lw, h = 1 << lh;
                if (f.MipSize(w, h) == size) hits.Add(new(f, w, h, false));
                else if (MipChain(f, w, h) == size) hits.Add(new(f, w, h, true));
            }
        }

        // Rank candidates by PRIOR likelihood (which to try first). The final pick is
        // decided by decode quality (ImageLikelihood), so this only needs to surface the
        // correct interpretation within the first `max` tries. Common textures are BC7/BC1,
        // with aspect ratios near 1:1, 2:1 or 1:2 — rank those first.
        int Rank(ImageFormat f) => Array.IndexOf(Formats, f);
        double AspectPenalty(TextureGuess g)
        {
            double a = Math.Abs(Math.Log2((double)g.Width / g.Height)); // 0=square,1=2:1,2=4:1
            return a;
        }
        return hits
            .OrderBy(AspectPenalty)                       // squarish / 2:1 first
            .ThenBy(g => Rank(g.Format))                  // BC7, BC1, BC3, BC5, BC4, BC6H
            .ThenByDescending(g => (long)g.Width * g.Height)
            .Take(max)
            .ToList();
    }

    // Memoize the best guess per byte-size — many assets share the same size, so this
    // collapses per-row work (used when building 100k+ asset rows).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, TextureGuess?> BestCache = new();

    /// <summary>The single best guess, or null if the size fits no BCn layout. Memoized by size.</summary>
    public static TextureGuess? Best(long size) =>
        BestCache.GetOrAdd(size, s => Guess(s, 1) is [var g, ..] ? g : (TextureGuess?)null);

    private static bool IsPow2(long n) => n > 0 && (n & (n - 1)) == 0;

    /// <summary>
    /// O(1) existence check: could a blob of this size be a single-mip BCn texture?
    /// A BCn image is blockBytes × (w/4) × (h/4) with w,h powers of two, so size/8 or
    /// size/16 must be a power of two within the max block-grid (≤ 2048×2048 blocks).
    /// Used for fast per-row classification without full enumeration.
    /// </summary>
    public static bool CouldBeImage(long size)
    {
        const long maxBlocks = 2048L * 2048L;   // up to 8192×8192 (BCn block grid)
        const long maxPixels = 8192L * 8192L;   // uncompressed pixel count
        // BCn: size/8 or size/16 is a power of two block-count.
        foreach (int bb in stackalloc[] { 8, 16 })
        {
            if (size % bb != 0) continue;
            long n = size / bb;
            if (IsPow2(n) && n <= maxBlocks) return true;
        }
        // Uncompressed: size/bpp is a power-of-two pixel count (w,h powers of two ⇒ product pow2).
        foreach (int bpp in stackalloc[] { 4, 2, 1 })
        {
            if (size % bpp != 0) continue;
            long n = size / bpp;
            if (IsPow2(n) && n >= 16 && n <= maxPixels) return true;
        }
        return false;
    }
}
