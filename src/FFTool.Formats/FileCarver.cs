using System.Buffers.Binary;

namespace FFTool.Formats;

/// <summary>A validated embedded file found inside a buffer (zone or blob).</summary>
public readonly record struct CarvedFile(string Kind, string Extension, long Offset, long Length);

/// <summary>
/// Scans a decompressed buffer for genuine embedded standard-format files
/// (DDS, PNG, Ogg, RIFF/WAV, Bink video, Wwise pack/bank) using magic + structural
/// validation, so coincidental 3-byte matches are rejected. This is the honest
/// "extract GSC / IWI / video / etc. if they exist" tool: on modern CoD (IW10) the
/// asset data is engine-internal, so most such scans legitimately find nothing —
/// the carver only reports files whose headers actually validate.
/// </summary>
public static class FileCarver
{
    public static IReadOnlyList<CarvedFile> Scan(ReadOnlySpan<byte> data, int max = 100000)
    {
        var found = new List<CarvedFile>();
        for (int i = 0; i + 16 <= data.Length && found.Count < max; i++)
        {
            var c = TryAt(data, i);
            if (c is { } f) { found.Add(f); i += (int)Math.Max(0, f.Length - 1); }
        }
        return found;
    }

    private static CarvedFile? TryAt(ReadOnlySpan<byte> d, int i)
    {
        uint m32 = BinaryPrimitives.ReadUInt32LittleEndian(d[i..]);

        // DDS — validate dwSize == 124 at +4. Length = header + linearSize(+20) covering all mips.
        if (m32 == 0x20534444 && Read(d, i + 4) == 124)
        {
            uint linear = Read(d, i + 20);        // dwPitchOrLinearSize (base surface)
            uint mips = Math.Max(1, Read(d, i + 28)); // dwMipMapCount
            bool dx10 = Read(d, i + 84) == 0x30315844;
            long header = 4 + 124 + (dx10 ? 20 : 0);
            long len = header + (long)linear * mips;  // upper-bound estimate
            return new("DDS image", "dds", i, Clamp(len, d.Length - i));
        }
        // PNG — validate full signature + IHDR chunk type, then walk to IEND for exact length.
        if (m32 == 0x474E5089 && d.Length > i + 16 &&
            d[i + 4] == 0x0D && d[i + 5] == 0x0A && d[i + 6] == 0x1A && d[i + 7] == 0x0A &&
            d[i + 12] == 'I' && d[i + 13] == 'H' && d[i + 14] == 'D' && d[i + 15] == 'R')
        {
            long len = FindPngEnd(d, i);
            if (len > 0) return new("PNG image", "png", i, len);
        }
        // OggS — validate version byte 0.
        if (m32 == 0x5367674F && d[i + 4] == 0)
            return new("Ogg audio", "ogg", i, 0); // length walked by consumer
        // RIFF/WAV — validate 'WAVE' at +8 and a sane chunk size.
        if (m32 == 0x46464952 && d.Length > i + 12 &&
            BinaryPrimitives.ReadUInt32LittleEndian(d[(i + 8)..]) == 0x45564157)
        {
            long len = 8 + Read(d, i + 4);
            return new("WAV audio", "wav", i, Clamp(len, d.Length - i));
        }
        // Bink2 video — 'KB2' + valid frame-size field (avoid 3-byte coincidences).
        if (d[i] == (byte)'K' && d[i + 1] == (byte)'B' && d[i + 2] == (byte)'2' && d.Length > i + 44)
        {
            long len = Read(d, i + 4) + 8;
            if (len > 1024 && len < d.Length - i + 1) return new("Bink2 video", "bk2", i, len);
        }
        // Wwise pack ("AKPK" = 0x4B504B41) / bank ("BKHD" = 0x44484B42) — validate size field.
        if (m32 == 0x4B504B41) { long len = Read(d, i + 8) + 8; if (len > 16 && len <= d.Length - i) return new("Wwise pack", "pck", i, len); }
        if (m32 == 0x44484B42) { long len = Read(d, i + 4) + 8; if (len > 16 && len <= d.Length - i) return new("Wwise bank", "bnk", i, len); }

        return null;
    }

    private static uint Read(ReadOnlySpan<byte> d, int o) =>
        o + 4 <= d.Length ? BinaryPrimitives.ReadUInt32LittleEndian(d[o..]) : 0;

    private static long Clamp(long len, long remaining) => Math.Max(0, Math.Min(len, remaining));

    private static long FindPngEnd(ReadOnlySpan<byte> d, int start)
    {
        // Walk chunks to IEND for an exact length, bounds-checking every step (no overflow).
        long p = start + 8;
        while (p + 8 <= d.Length)
        {
            uint clen = BinaryPrimitives.ReadUInt32BigEndian(d[(int)p..]);
            if (clen > (uint)d.Length) return -1;         // corrupt chunk length → reject
            bool iend = d[(int)p + 4] == 'I' && d[(int)p + 5] == 'E' && d[(int)p + 6] == 'N' && d[(int)p + 7] == 'D';
            p += 12L + clen;                              // long arithmetic → no negative wrap
            if (p > d.Length) return -1;                  // extends past buffer → reject
            if (iend) return p - start;
        }
        return -1;                                        // no IEND → not a valid embedded PNG
    }
}
