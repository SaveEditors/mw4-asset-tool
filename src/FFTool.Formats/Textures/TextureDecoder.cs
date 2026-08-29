using BCnEncoder.Decoder;
using BCnEncoder.Shared;

namespace FFTool.Formats;

/// <summary>Decoded image: BGRA32 pixels ready for a WPF WriteableBitmap.</summary>
public sealed class DecodedImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Bgra { get; init; }   // width*height*4, BGRA order
}

/// <summary>Decodes raw BCn block data (headerless) into BGRA pixels.</summary>
public static class TextureDecoder
{
    private static readonly BcDecoder Decoder = new();

    private static CompressionFormat? Map(ImageFormat f) => f switch
    {
        ImageFormat.BC1 => CompressionFormat.Bc1,
        ImageFormat.BC2 => CompressionFormat.Bc2,
        ImageFormat.BC3 => CompressionFormat.Bc3,
        ImageFormat.BC4 => CompressionFormat.Bc4,
        ImageFormat.BC5 => CompressionFormat.Bc5,
        ImageFormat.BC6H => CompressionFormat.Bc6U,
        ImageFormat.BC7 => CompressionFormat.Bc7,
        _ => null,
    };

    /// <summary>
    /// Decode the base mip of <paramref name="blob"/> as the given interpretation.
    /// Only the first <c>MipSize(w,h)</c> bytes are used (mip chains store the
    /// base mip first).
    /// </summary>
    public static DecodedImage Decode(ReadOnlySpan<byte> blob, TextureGuess guess)
    {
        int need = (int)guess.Format.MipSize(guess.Width, guess.Height);
        if (blob.Length < need)
            throw new InvalidDataException($"Blob {blob.Length} < needed {need} for {guess}.");

        // Uncompressed formats: interpret bytes directly (no BCn decode).
        if (guess.Format.IsUncompressed())
            return DecodeUncompressed(blob[..need], guess);

        var fmt = Map(guess.Format) ?? throw new NotSupportedException($"{guess.Format} unsupported.");
        var baseMip = blob[..need].ToArray();
        ColorRgba32[] pixels = Decoder.DecodeRaw(baseMip, guess.Width, guess.Height, fmt);

        var bgra = new byte[guess.Width * guess.Height * 4];
        switch (guess.Format)
        {
            case ImageFormat.BC4:   // single channel → grayscale (roughness/AO/mask/height)
                for (int i = 0; i < pixels.Length; i++)
                {
                    byte v = pixels[i].r; int o = i * 4;
                    bgra[o] = v; bgra[o + 1] = v; bgra[o + 2] = v; bgra[o + 3] = 255;
                }
                break;
            case ImageFormat.BC5:   // two channel (normal map XY) → reconstruct Z into blue
                for (int i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i]; int o = i * 4;
                    double nx = p.r / 127.5 - 1.0, ny = p.g / 127.5 - 1.0;
                    double nz = Math.Sqrt(Math.Max(0.0, 1.0 - nx * nx - ny * ny));
                    bgra[o] = (byte)((nz * 0.5 + 0.5) * 255); bgra[o + 1] = p.g; bgra[o + 2] = p.r; bgra[o + 3] = 255;
                }
                break;
            default:                // BC1/2/3/7/6H → full colour
                for (int i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i]; int o = i * 4;
                    bgra[o] = p.b; bgra[o + 1] = p.g; bgra[o + 2] = p.r; bgra[o + 3] = p.a;
                }
                break;
        }
        return new DecodedImage { Width = guess.Width, Height = guess.Height, Bgra = bgra };
    }

    /// <summary>
    /// Find the most image-like interpretation of a blob. Scores several
    /// candidates; returns the best guess + its decoded image, or null if none
    /// looks like a real image.
    /// </summary>
    public static (TextureGuess Guess, DecodedImage Image)? BestGuess(ReadOnlySpan<byte> blob, double minScore = 0.35, int maxCandidates = DefaultCandidates)
    {
        var pick = PickBestGuess(blob, maxCandidates, minScore);
        if (pick is not { } p) return null;
        try { return (p.Guess, Decode(blob, p.Guess)); } catch { return null; }
    }

    /// <summary>
    /// Decide the correct (format, dimensions) for a blob by scoring each candidate on a
    /// small CONTIGUOUS centre crop — fast (bounded work regardless of image size) and
    /// accurate (real spatial structure, so the smoothness metric is valid, unlike block
    /// subsampling which scrambles adjacency). This single decision is shared by the grid
    /// thumbnail and the inspector so they always agree.
    /// </summary>
    public static (TextureGuess Guess, double Score)? PickBestGuess(
        ReadOnlySpan<byte> blob, int maxCandidates = 24, double minScore = 0.0, FormatPrior? prior = null)
    {
        prior ??= FormatPrior.Current;
        long size = blob.Length;
        TextureGuess bestG = default, fallbackG = default;
        double bestCombined = -1, bestLikelihood = -1, maxLikelihood = -1;
        bool haveFallback = false;
        foreach (var g in DimensionGuesser.Guess(size, maxCandidates))
        {
            var crop = CropDecode(blob, g, 96);
            if (crop is null) continue;
            if (!haveFallback) { haveFallback = true; fallbackG = g; }

            double s = ImageLikelihood.Score(crop, g.Format.IsBlockCompressed());
            if (s > maxLikelihood) maxLikelihood = s;

            // Pixel evidence decides between UNSEEN interpretations; the fixed format prior only
            // breaks near-ties there. All exact-size candidates are scored (there are only a few)
            // rather than stopping at the first high scorer — otherwise candidate ordering, not
            // pixels, picks the winner when several interpretations look smooth (e.g. a 2:1
            // texture vs a square one of the same byte length).
            //
            // The LEARNED prior is different: a size the user has explicitly confirmed in the
            // "show all formats" tool gets a strong multiplicative boost, so a confirmed
            // interpretation overrides the pixel score even when a WRONG interpretation happens
            // to look smoother in the centre crop (the common failure — a transpose whose flat
            // centre scores high). With no confirmations the boost is 1.0, so out-of-the-box
            // detection is unchanged.
            double combined = s * (1 + 0.15 * BasePrior(g.Format)) * (1 + 3.0 * prior.Bonus(size, g));
            if (combined > bestCombined) { bestCombined = combined; bestG = g; bestLikelihood = s; }
        }
        if (maxLikelihood >= minScore && maxLikelihood >= 0) return (bestG, bestLikelihood);
        if (minScore <= 0 && haveFallback) return (fallbackG, Math.Max(0, bestLikelihood));
        return null;
    }

    /// <summary>
    /// Fixed prior likelihood (0..1) of each format on this engine generation, used only to
    /// break near-ties when the pixel signal cannot (e.g. the same colour image is a valid
    /// BC1 / BC3 / BC7 decode). BC7 dominates modern IW10 colour textures; BC2 is rare.
    /// </summary>
    private static double BasePrior(ImageFormat f) => f switch
    {
        ImageFormat.BC7 => 1.00,
        ImageFormat.BC1 => 0.70,
        ImageFormat.BC5 => 0.60,
        ImageFormat.BC3 => 0.55,
        ImageFormat.R8G8B8A8 => 0.50,
        ImageFormat.BC4 => 0.45,
        ImageFormat.BC6H => 0.30,
        ImageFormat.R8 => 0.25,
        ImageFormat.R8G8 => 0.25,
        ImageFormat.BC2 => 0.15,
        _ => 0.15,
    };

    /// <summary>
    /// Image-likelihood (0..1) of interpreting <paramref name="blob"/> as <paramref name="g"/>,
    /// measured on a contiguous centre crop, or null if the blob is too short for it. Set
    /// <paramref name="blockAwarePenalty"/> to include the BCn block-seam penalty.
    /// </summary>
    public static double? ScoreInterpretation(ReadOnlySpan<byte> blob, TextureGuess g, bool blockAwarePenalty = true)
    {
        var crop = CropDecode(blob, g, 96);
        return crop is null ? null : ImageLikelihood.Score(crop, blockAwarePenalty && g.Format.IsBlockCompressed());
    }

    /// <summary>
    /// Decode a small contiguous centre crop of an interpretation (≤ maxPx) at full quality,
    /// for scoring. Returns null if the blob is too short for this interpretation.
    /// </summary>
    private static DecodedImage? CropDecode(ReadOnlySpan<byte> blob, TextureGuess g, int maxPx)
    {
        try
        {
            if (g.Format.IsUncompressed())
            {
                int bpp = g.Format.BytesPerPixel();
                long need = (long)g.Width * g.Height * bpp;
                if (blob.Length < need) return null;
                int cw = Math.Min(g.Width, maxPx), ch = Math.Min(g.Height, maxPx);
                int x0 = (g.Width - cw) / 2, y0 = (g.Height - ch) / 2;
                var crop = new byte[cw * ch * bpp];
                for (int y = 0; y < ch; y++)
                    blob.Slice(((y0 + y) * g.Width + x0) * bpp, cw * bpp).CopyTo(crop.AsSpan(y * cw * bpp));
                return DecodeUncompressed(crop, g with { Width = cw, Height = ch });
            }

            var fmt = Map(g.Format);
            if (fmt is null) return null;
            int bb = g.Format.BlockBytes();
            int bw = Math.Max(1, g.Width / 4), bh = Math.Max(1, g.Height / 4);
            if ((long)bw * bh * bb > blob.Length) return null;
            int cbw = Math.Min(bw, maxPx / 4), cbh = Math.Min(bh, maxPx / 4);
            int bx0 = (bw - cbw) / 2, by0 = (bh - cbh) / 2;
            var packed = new byte[cbw * cbh * bb];
            for (int by = 0; by < cbh; by++)
                blob.Slice(((by0 + by) * bw + bx0) * bb, cbw * bb).CopyTo(packed.AsSpan(by * cbw * bb));
            return DecodeBcnPacked(packed, cbw * 4, cbh * 4, g.Format, fmt.Value);
        }
        catch { return null; }
    }

    /// <summary>Decode a packed BCn buffer to BGRA with the per-format channel mapping.</summary>
    private static DecodedImage DecodeBcnPacked(byte[] packed, int w, int h, ImageFormat format, CompressionFormat fmt)
    {
        ColorRgba32[] pixels = Decoder.DecodeRaw(packed, w, h, fmt);
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i]; int o = i * 4;
            switch (format)
            {
                case ImageFormat.BC4: bgra[o] = bgra[o + 1] = bgra[o + 2] = p.r; bgra[o + 3] = 255; break;
                case ImageFormat.BC5:
                    double nx = p.r / 127.5 - 1.0, ny = p.g / 127.5 - 1.0;
                    double nz = Math.Sqrt(Math.Max(0.0, 1.0 - nx * nx - ny * ny));
                    bgra[o] = (byte)((nz * 0.5 + 0.5) * 255); bgra[o + 1] = p.g; bgra[o + 2] = p.r; bgra[o + 3] = 255; break;
                default: bgra[o] = p.b; bgra[o + 1] = p.g; bgra[o + 2] = p.r; bgra[o + 3] = p.a; break;
            }
        }
        return new DecodedImage { Width = w, Height = h, Bgra = bgra };
    }

    /// <summary>Decode the most image-like interpretation, or null if not image-like.</summary>
    public static DecodedImage? TryDecodeBest(ReadOnlySpan<byte> blob, double minScore = 0.35, int maxCandidates = 8)
        => BestGuess(blob, minScore, maxCandidates)?.Image;

    /// <summary>
    /// FAST thumbnail decode: decode only the SINGLE best geometric guess (no multi-candidate
    /// scoring loop) — ~5× faster for grid preloading over 100k+ assets. Returns the guess +
    /// decoded image + its likelihood score, or null if the size fits no BCn/uncompressed layout.
    /// Accuracy trade-off vs BestGuess is covered by the manual "show all formats" override.
    /// </summary>
    public const int DefaultCandidates = 24;

    public static (TextureGuess Guess, DecodedImage Image, double Score)? DecodeFastThumbnail(
        ReadOnlySpan<byte> blob, int targetPx = 96, int maxCandidates = DefaultCandidates)
    {
        // Decide format/dims with the SAME accurate scorer the inspector uses (contiguous
        // centre crop) so the grid tile and preview always agree — then decode the winner.
        // For quality WITHOUT decoding the whole (possibly 4K) texture: render a moderate
        // ~256 px intermediate — dense block sampling, not the sparse every-Nth-block used
        // before — which the caller then box-averages down to the tile size. That intermediate
        // is 4–16× cheaper to decode than the full image yet looks like the preview after the
        // area-filter downscale (the old direct-to-96 subsample was sparse and blocky). Small
        // textures are decoded fully (already cheap).
        const int Intermediate = 256;
        var pick = PickBestGuess(blob, maxCandidates);
        if (pick is not { } p) return null;
        try
        {
            var img = p.Guess.Format.IsBlockCompressed() && Math.Max(p.Guess.Width, p.Guess.Height) > Intermediate
                ? SubsampleBlocks(blob, p.Guess, Intermediate)
                : Decode(blob, p.Guess);
            return img is not null ? (p.Guess, img, p.Score) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Decode a downsampled version of a large BCn texture by selecting every Nth 4x4
    /// block, so only ~(targetPx/4)² blocks are decoded regardless of source size.
    /// </summary>
    private static DecodedImage? SubsampleBlocks(ReadOnlySpan<byte> blob, TextureGuess g, int targetPx)
    {
        var fmt = Map(g.Format);
        if (fmt is null) return null;
        int bb = g.Format.BlockBytes();
        int blocksW = Math.Max(1, g.Width / 4), blocksH = Math.Max(1, g.Height / 4);
        long need = (long)blocksW * blocksH * bb;
        if (blob.Length < need) return null;

        // Scale BOTH dimensions by the SAME factor so the aspect ratio is preserved — the
        // longer side maps to targetPx/4 blocks, the shorter side shrinks proportionally.
        // (Clamping each dimension independently squished non-square textures — e.g. a tall
        // 256×512 poster collapsed to a 96×96 square that looked nothing like the full preview.)
        int cap = Math.Max(1, targetPx / 4);
        int maxBlocks = Math.Max(blocksW, blocksH);
        int outBw, outBh;
        if (maxBlocks <= cap) { outBw = blocksW; outBh = blocksH; }
        else
        {
            outBw = Math.Max(1, (int)Math.Round((double)blocksW * cap / maxBlocks));
            outBh = Math.Max(1, (int)Math.Round((double)blocksH * cap / maxBlocks));
        }
        var packed = new byte[outBw * outBh * bb];
        for (int by = 0; by < outBh; by++)
        {
            int sby = (int)((long)by * blocksH / outBh);
            for (int bx = 0; bx < outBw; bx++)
            {
                int sbx = (int)((long)bx * blocksW / outBw);
                int srcBlock = (sby * blocksW + sbx) * bb;
                int dstBlock = (by * outBw + bx) * bb;
                blob.Slice(srcBlock, bb).CopyTo(packed.AsSpan(dstBlock));
            }
        }
        int ow = outBw * 4, oh = outBh * 4;
        ColorRgba32[] pixels = Decoder.DecodeRaw(packed, ow, oh, fmt.Value);
        var bgra = new byte[ow * oh * 4];
        // Reuse the per-format channel mapping by delegating to a tiny inline copy.
        for (int i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i]; int o = i * 4;
            switch (g.Format)
            {
                case ImageFormat.BC4: bgra[o] = bgra[o + 1] = bgra[o + 2] = p.r; bgra[o + 3] = 255; break;
                case ImageFormat.BC5:
                    double nx = p.r / 127.5 - 1.0, ny = p.g / 127.5 - 1.0;
                    double nz = Math.Sqrt(Math.Max(0.0, 1.0 - nx * nx - ny * ny));
                    bgra[o] = (byte)((nz * 0.5 + 0.5) * 255); bgra[o + 1] = p.g; bgra[o + 2] = p.r; bgra[o + 3] = 255; break;
                default: bgra[o] = p.b; bgra[o + 1] = p.g; bgra[o + 2] = p.r; bgra[o + 3] = p.a; break;
            }
        }
        return new DecodedImage { Width = ow, Height = oh, Bgra = bgra };
    }

    /// <summary>Interpret raw bytes as an uncompressed image (R8G8B8A8 / R8G8 / R8).</summary>
    private static DecodedImage DecodeUncompressed(ReadOnlySpan<byte> data, TextureGuess g)
    {
        int w = g.Width, h = g.Height, px = w * h;
        var bgra = new byte[px * 4];
        switch (g.Format)
        {
            case ImageFormat.R8G8B8A8:
                for (int i = 0; i < px; i++)
                {
                    int s = i * 4, o = i * 4;
                    bgra[o] = data[s + 2]; bgra[o + 1] = data[s + 1]; bgra[o + 2] = data[s]; bgra[o + 3] = data[s + 3];
                }
                break;
            case ImageFormat.R8G8:   // two channel → treat like a normal map (reconstruct blue)
                for (int i = 0; i < px; i++)
                {
                    int s = i * 2, o = i * 4;
                    double nx = data[s] / 127.5 - 1.0, ny = data[s + 1] / 127.5 - 1.0;
                    double nz = Math.Sqrt(Math.Max(0.0, 1.0 - nx * nx - ny * ny));
                    bgra[o] = (byte)((nz * 0.5 + 0.5) * 255); bgra[o + 1] = data[s + 1]; bgra[o + 2] = data[s]; bgra[o + 3] = 255;
                }
                break;
            default:                 // R8 → grayscale
                for (int i = 0; i < px; i++)
                {
                    byte v = data[i]; int o = i * 4;
                    bgra[o] = v; bgra[o + 1] = v; bgra[o + 2] = v; bgra[o + 3] = 255;
                }
                break;
        }
        return new DecodedImage { Width = w, Height = h, Bgra = bgra };
    }
}
