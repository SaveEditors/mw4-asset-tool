using System.Text;

namespace FFTool.Formats;

/// <summary>Detected content of a decompressed asset blob.</summary>
public readonly record struct SniffResult(string Type, string Extension, bool IsText)
{
    public static readonly SniffResult Unknown = new("binary", "bin", false);
}

/// <summary>
/// Best-effort content identification of a decompressed asset blob by magic
/// bytes / structure. xsub blobs are usually engine-internal (→ "binary"), but
/// container formats (DDS, PNG, WAV, Ogg, Wwise, Bink, text scripts) are caught
/// so exports get a meaningful extension and users can spot images/audio.
/// </summary>
public static class AssetSniffer
{
    public static SniffResult Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 4)
        {
            uint m = (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24);
            switch (m)
            {
                case 0x20534444: return new("DDS image", "dds", false);       // 'DDS '
                case 0x474E5089: return new("PNG image", "png", false);       // \x89PNG
                case 0x46464952: return new("WAV/RIFF audio", "wav", false);  // 'RIFF'
                case 0x5367674F: return new("Ogg audio", "ogg", false);       // 'OggS'
                case 0x4B415041: return new("Wwise pack", "pck", false);      // 'AKPK'
                case 0x44484B42: return new("Wwise bank", "bnk", false);      // 'BKHD'
                case 0x4B434142: return new("Bink 2 video", "bk2", false);    // 'BACK'? (BIK/KB2)
                case 0x58455448: return new("HTEX texture", "htex", false);   // 'HTEX'
                case 0x54534163: return new("Cast model", "cast", false);     // 'cAST'
            }
            if (b[0] == 0x1A && b.Length >= 4 && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3)
                return new("Matroska/WebM", "mkv", false);
            if ((b[0] == 'K' && b[1] == 'B' && b[2] == '2') )
                return new("Bink video", "bik", false);
        }

        // Text heuristic: mostly printable ASCII/whitespace in the first chunk.
        int n = Math.Min(b.Length, 256), printable = 0;
        for (int i = 0; i < n; i++)
        {
            byte c = b[i];
            if (c == 9 || c == 10 || c == 13 || (c >= 32 && c < 127)) printable++;
        }
        if (n >= 8 && printable >= n * 0.95)
            return new("text / script", "txt", true);

        return SniffResult.Unknown;
    }

    /// <summary>
    /// Classify a blob into a coarse asset category using only its bytes (no .ff).
    /// Order: known magic → image-shaped (BCn size) → audio-like (16-bit PCM stats) →
    /// generic binary. This is what backs the browser's Type column when there is no
    /// authoritative name/type from a loaded name database.
    /// </summary>
    public static string ClassifyKind(ReadOnlySpan<byte> b, long declaredSize)
    {
        var s = Detect(b);
        if (s.Type != "binary")
        {
            if (s.Extension is "dds" or "png" or "htex") return "Image";
            if (s.Extension is "wav" or "ogg" or "pck" or "bnk") return "Sound";
            if (s.Extension is "bk2" or "bik" or "mkv") return "Video";
            if (s.Extension is "cast") return "Model";
            if (s.IsText) return "Text / script";
        }
        if (DimensionGuesser.CouldBeImage(declaredSize)) return "Image";
        if (LooksLikeAudio(b)) return "Sound (likely)";
        return "Binary data";
    }

    /// <summary>
    /// Heuristic: raw 16-bit PCM audio has low sample-to-sample delta on average
    /// (waveforms are locally smooth) and near-zero DC, unlike compressed/random data.
    /// </summary>
    private static bool LooksLikeAudio(ReadOnlySpan<byte> b)
    {
        int samples = Math.Min(b.Length / 2, 8192);
        if (samples < 512) return false;
        long absDelta = 0; int prev = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(b[i * 2] | (b[i * 2 + 1] << 8));
            if (i > 0) absDelta += Math.Abs(s - prev);
            prev = s;
        }
        double meanDelta = (double)absDelta / (samples - 1);
        // Smooth 16-bit audio: consecutive samples differ modestly relative to full 16-bit range.
        return meanDelta is > 20 and < 3000;
    }

    /// <summary>A compact hex+ascii preview of the first bytes, for the inspector.</summary>
    public static string HexPreview(ReadOnlySpan<byte> b, int maxBytes = 128)
    {
        int n = Math.Min(b.Length, maxBytes);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i += 16)
        {
            sb.Append($"{i:x4}  ");
            int row = Math.Min(16, n - i);
            for (int j = 0; j < 16; j++)
                sb.Append(j < row ? $"{b[i + j]:x2} " : "   ");
            sb.Append(' ');
            for (int j = 0; j < row; j++)
            {
                byte c = b[i + j];
                sb.Append(c is >= 32 and < 127 ? (char)c : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
