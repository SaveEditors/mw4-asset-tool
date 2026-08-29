namespace FFTool.Formats;

/// <summary>Texture pixel formats we attempt for IW10 assets.</summary>
public enum ImageFormat
{
    Unknown = 0,
    R8G8B8A8,   // uncompressed 32bpp RGBA (icons/UI)
    R8,         // uncompressed 8bpp single channel (masks/luts)
    R8G8,       // uncompressed 16bpp two channel
    BC1,        // DXT1
    BC2,        // DXT3
    BC3,        // DXT5
    BC4,        // ATI1 / single channel
    BC5,        // ATI2 / normal maps
    BC6H,       // HDR
    BC7,        // high-quality RGBA
}

public static class ImageFormatInfo
{
    /// <summary>Bytes per pixel for uncompressed formats, else 0.</summary>
    public static int BytesPerPixel(this ImageFormat f) => f switch
    {
        ImageFormat.R8G8B8A8 => 4,
        ImageFormat.R8G8 => 2,
        ImageFormat.R8 => 1,
        _ => 0,
    };

    /// <summary>Bytes per 4x4 block (BCn only), else 0.</summary>
    public static int BlockBytes(this ImageFormat f) => f switch
    {
        ImageFormat.BC1 or ImageFormat.BC4 => 8,
        ImageFormat.BC2 or ImageFormat.BC3 or ImageFormat.BC5
            or ImageFormat.BC6H or ImageFormat.BC7 => 16,
        _ => 0,
    };

    public static bool IsBlockCompressed(this ImageFormat f) => f.BlockBytes() > 0;
    public static bool IsUncompressed(this ImageFormat f) => f.BytesPerPixel() > 0;

    /// <summary>Size in bytes of a single mip level.</summary>
    public static long MipSize(this ImageFormat f, int w, int h)
    {
        if (f.IsUncompressed()) return (long)w * h * f.BytesPerPixel();
        int bw = Math.Max(1, (w + 3) / 4), bh = Math.Max(1, (h + 3) / 4);
        return (long)bw * bh * f.BlockBytes();
    }
}
