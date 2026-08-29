namespace FFTool.Formats;

/// <summary>
/// Scores how much a decoded BGRA image looks like a REAL texture, used to (a) pick the
/// correct format/dimension interpretation and (b) reject non-image blobs that merely
/// happen to fit a BCn byte-length.
///
/// Signal: the FRACTION of pixels that are locally smooth. A correct texture decode has
/// large spatially-correlated regions (smooth), broken by some edges; a wrong format,
/// wrong dimensions, or a non-image blob decodes to high-frequency noise that is jagged
/// almost everywhere. Smooth-fraction separates these far better than a mean gradient
/// (which noise and real detail can share). Measured per channel (max), so magenta-style
/// garbage — smooth in luminance but noisy per channel — is correctly rejected. A single
/// dominant colour (degenerate fill) scores low so it never wins over real content.
/// </summary>
public static class ImageLikelihood
{
    private const int SmoothThreshold = 24;   // per-channel neighbour delta considered "smooth"

    public static double Score(DecodedImage img) => Score(img, false);

    /// <summary>
    /// Score a decode. When <paramref name="blockCompressed"/> is set, a wrong BCn format /
    /// dimension interpretation is additionally penalised: mis-decoding re-reads each 4×4 block
    /// out of alignment, which leaves the block piecewise-flat inside but discontinuous at its
    /// 4-pixel boundaries. A correct decode is just as smooth across boundaries as within them,
    /// so a high boundary-to-interior gradient ratio marks a wrong interpretation.
    /// </summary>
    public static double Score(DecodedImage img, bool blockCompressed)
    {
        int w = img.Width, h = img.Height;
        if (w < 4 || h < 4) return 0;
        var px = img.Bgra;
        int stride = w * 4;

        int step = Math.Max(1, (w * h) / 16384);
        int smooth = 0, sampled = 0;
        Span<int> hB = stackalloc int[16], hG = stackalloc int[16], hR = stackalloc int[16];
        int count = 0;

        for (int y = 0; y + 1 < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x + step < w; x += step)
            {
                int o = row + x * 4;
                int b = px[o], g = px[o + 1], r = px[o + 2];
                hB[b >> 4]++; hG[g >> 4]++; hR[r >> 4]++; count++;

                int oR = o + step * 4, oD = o + stride;
                int dR = Math.Max(Math.Abs(px[oR] - b), Math.Max(Math.Abs(px[oR + 1] - g), Math.Abs(px[oR + 2] - r)));
                int dD = Math.Max(Math.Abs(px[oD] - b), Math.Max(Math.Abs(px[oD + 1] - g), Math.Abs(px[oD + 2] - r)));
                int grad = Math.Max(dR, dD);
                if (grad <= SmoothThreshold) smooth++;
                sampled++;
            }
        }
        if (sampled == 0 || count == 0) return 0;

        // Degenerate single-colour fill → not useful content.
        double Dom(Span<int> hist) { int d = 0; foreach (var c in hist) if (c > d) d = c; return (double)d / count; }
        if (Dom(hB) > 0.985 && Dom(hG) > 0.985 && Dom(hR) > 0.985) return 0;

        double smoothFrac = (double)smooth / sampled;

        // Real textures: ~0.45–0.95 smooth. Noise / wrong decode: ~0.05–0.35.
        // Map to a score with the useful decision band around 0.4.
        if (!blockCompressed || w < 8 || h < 8) return smoothFrac;

        // Block-seam term: compare mean gradient ON 4-px block boundaries to gradient in the
        // block interior. Correct decode → ratio ≈ 1; wrong format/dims → boundaries seam (>1).
        long seam = 0; int seamN = 0; long inner = 0; int innerN = 0;
        for (int y = 1; y + 1 < h; y++)
        {
            int row = y * stride;
            for (int x = 1; x + 1 < w; x++)
            {
                int o = row + x * 4;
                int gx = Math.Max(Math.Abs(px[o] - px[o - 4]),
                         Math.Max(Math.Abs(px[o + 1] - px[o - 3]), Math.Abs(px[o + 2] - px[o - 2])));
                if (x % 4 == 0) { seam += gx; seamN++; } else { inner += gx; innerN++; }
            }
        }
        for (int y = 1; y + 1 < h; y++)
        {
            int row = y * stride;
            bool onSeam = y % 4 == 0;
            for (int x = 1; x + 1 < w; x++)
            {
                int o = row + x * 4, u = o - stride;
                int gy = Math.Max(Math.Abs(px[o] - px[u]),
                         Math.Max(Math.Abs(px[o + 1] - px[u + 1]), Math.Abs(px[o + 2] - px[u + 2])));
                if (onSeam) { seam += gy; seamN++; } else { inner += gy; innerN++; }
            }
        }
        if (seamN == 0 || innerN == 0) return smoothFrac;
        double seamAvg = (double)seam / seamN, innerAvg = (double)inner / innerN;
        double ratio = innerAvg > 1 ? seamAvg / innerAvg : 1.0;
        if (ratio <= 1.25) return smoothFrac;                 // no unusual seams — trust smoothness
        double penalty = Math.Max(0.55, 1.25 / ratio);        // cap the penalty so it only nudges
        return smoothFrac * penalty;
    }

    public static bool LooksLikeImage(DecodedImage img, double threshold = 0.45) =>
        Score(img) >= threshold;
}
