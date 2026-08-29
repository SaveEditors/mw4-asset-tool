using System.Buffers.Binary;

namespace FFTool.Formats;

/// <summary>
/// Writes a valid DDS file (DX10 header) from raw BCn/uncompressed block data +
/// dimensions + format. This is the usable, lossless image-extraction path for
/// modern CoD textures (which ship as headerless BCn blobs, not .iwi): the output
/// opens directly in Photoshop, GIMP, Paint.NET, DirectXTex, etc.
/// </summary>
public static class DdsWriter
{
    private const uint DDS_MAGIC = 0x20534444;      // "DDS "
    private const uint DDSD_CAPS = 0x1, DDSD_HEIGHT = 0x2, DDSD_WIDTH = 0x4,
                       DDSD_PIXELFORMAT = 0x1000, DDSD_LINEARSIZE = 0x80000, DDSD_MIPMAPCOUNT = 0x20000;
    private const uint DDPF_FOURCC = 0x4;
    private const uint DDSCAPS_TEXTURE = 0x1000;
    private const uint FOURCC_DX10 = 0x30315844;    // "DX10"

    // DXGI_FORMAT values for the DX10 header.
    private static uint Dxgi(ImageFormat f) => f switch
    {
        ImageFormat.BC1 => 71,   // BC1_UNORM
        ImageFormat.BC2 => 74,   // BC2_UNORM
        ImageFormat.BC3 => 77,   // BC3_UNORM
        ImageFormat.BC4 => 80,   // BC4_UNORM
        ImageFormat.BC5 => 83,   // BC5_UNORM
        ImageFormat.BC6H => 95,  // BC6H_UF16
        ImageFormat.BC7 => 98,   // BC7_UNORM
        ImageFormat.R8G8B8A8 => 28, // R8G8B8A8_UNORM
        ImageFormat.R8G8 => 49,  // R8G8_UNORM
        ImageFormat.R8 => 61,    // R8_UNORM
        _ => 0,
    };

    /// <summary>Build a DDS byte array from the raw base-mip data of an interpretation.</summary>
    private const uint DDSD_PITCH = 0x8;

    public static byte[] Build(ReadOnlySpan<byte> baseMip, TextureGuess guess)
    {
        uint dxgi = Dxgi(guess.Format);
        if (dxgi == 0) throw new NotSupportedException($"No DXGI mapping for {guess.Format}.");
        if (guess.Width <= 0 || guess.Height <= 0)
            throw new ArgumentException($"Invalid dimensions {guess.Width}x{guess.Height}.");
        long expected = guess.Format.MipSize(guess.Width, guess.Height);
        if (baseMip.Length != expected)
            throw new ArgumentException($"baseMip is {baseMip.Length} bytes, expected {expected} for {guess}.");

        int headerSize = 4 + 124 + 20; // magic + DDS_HEADER + DDS_HEADER_DXT10
        var outp = new byte[headerSize + baseMip.Length];

        void W(int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(outp.AsSpan(off), v);

        // Uncompressed formats use PITCH (row bytes); block-compressed use LINEARSIZE (surface bytes).
        bool uncompressed = guess.Format.IsUncompressed();
        uint flags = DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT |
                     (uncompressed ? DDSD_PITCH : DDSD_LINEARSIZE);
        uint pitchOrLinear = uncompressed
            ? (uint)(guess.Width * guess.Format.BytesPerPixel())         // row pitch
            : (uint)expected;                                            // surface linear size

        W(0, DDS_MAGIC);
        // DDS_HEADER (124 bytes) starts at offset 4
        int h = 4;
        W(h + 0, 124);                                  // dwSize
        W(h + 4, flags);
        W(h + 8, (uint)guess.Height);
        W(h + 12, (uint)guess.Width);
        W(h + 16, pitchOrLinear);
        W(h + 20, 0);                                   // depth
        W(h + 24, 1);                                   // mipCount
        // pixel format (32 bytes) at h+72
        int pf = h + 72;
        W(pf + 0, 32);                                  // dwSize
        W(pf + 4, DDPF_FOURCC);                         // flags
        W(pf + 8, FOURCC_DX10);                         // fourCC = DX10
        W(h + 104, DDSCAPS_TEXTURE);                    // caps
        // DDS_HEADER_DXT10 (20 bytes) at offset 4+124 = 128
        int dx = 4 + 124;
        W(dx + 0, dxgi);                                // dxgiFormat
        W(dx + 4, 3);                                   // resourceDimension = TEXTURE2D
        W(dx + 8, 0);                                   // miscFlag
        W(dx + 12, 1);                                  // arraySize
        W(dx + 16, 0);                                  // miscFlags2

        baseMip.CopyTo(outp.AsSpan(headerSize));
        return outp;
    }

    /// <summary>Build a DDS from an asset blob, using only the base mip of the interpretation.</summary>
    public static byte[] FromBlob(ReadOnlySpan<byte> blob, TextureGuess guess)
    {
        int need = (int)guess.Format.MipSize(guess.Width, guess.Height);
        if (blob.Length < need) throw new InvalidDataException("Blob too small for interpretation.");
        return Build(blob[..need], guess);
    }
}
