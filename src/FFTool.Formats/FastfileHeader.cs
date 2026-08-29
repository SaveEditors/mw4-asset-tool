using System.Buffers.Binary;
using System.Text;

namespace FFTool.Formats;

/// <summary>Parsed structural info from an IWffa100 fastfile header (read-only view).</summary>
public sealed class FastfileInfo
{
    public required string Magic { get; init; }
    public required int Version { get; init; }
    public required int Compression { get; init; }
    public required long DeclaredSize { get; init; }
    public required long FileSize { get; init; }
    public required int TafaBlocks { get; init; }
    public required bool HasInnerStream { get; init; }

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Magic:        {Magic}");
        sb.AppendLine($"Version:      {Version}");
        sb.AppendLine($"Compression:  0x{Compression:x2} (Oodle)");
        sb.AppendLine($"Declared sz:  {DeclaredSize:N0} bytes (decompressed zone)");
        sb.AppendLine($"File size:    {FileSize:N0} bytes");
        sb.AppendLine($"TAFA blocks:  {TafaBlocks}");
        sb.AppendLine($"Container:    Oodle-compressed (decompressable)");
        sb.AppendLine($"Asset names:  hash-referenced (resolved by the game at runtime)");
        return sb.ToString();
    }
}

/// <summary>Reads the readable header of an IWffa100 fastfile (structure only; the inner
/// zone stream is encrypted and cannot be decoded statically).</summary>
public static class FastfileHeader
{
    public static FastfileInfo? TryRead(string path)
    {
        try
        {
            long fileSize = new FileInfo(path).Length;
            var buf = new byte[Math.Min(fileSize, 4096)];
            using (var fs = File.OpenRead(path)) fs.ReadExactly(buf, 0, buf.Length);

            var magic = Encoding.ASCII.GetString(buf, 0, 8);
            if (!magic.StartsWith("IWff")) return null;

            int version = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(8));
            int compression = buf.Length > 0x12 ? buf[0x12] : 0;
            long declared = buf.Length > 0x18 ? BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0x14)) : 0;

            // Count TAFA tagged blocks and detect the inner IWffs100 stream in the header window.
            int tafa = CountAscii(buf, "TAFA");
            bool inner = IndexOfAscii(buf, "IWffs100") >= 0 || IndexOfAscii(buf, "IWC") >= 0;

            return new FastfileInfo
            {
                Magic = magic.TrimEnd('\0'),
                Version = version,
                Compression = compression,
                DeclaredSize = declared,
                FileSize = fileSize,
                TafaBlocks = tafa,
                HasInnerStream = inner,
            };
        }
        catch { return null; }
    }

    private static int CountAscii(byte[] b, string tag)
    {
        var t = Encoding.ASCII.GetBytes(tag);
        int count = 0, i = 0;
        while ((i = IndexOfAscii(b, tag, i)) >= 0) { count++; i += t.Length; }
        return count;
    }

    private static int IndexOfAscii(byte[] b, string tag, int start = 0)
    {
        var t = Encoding.ASCII.GetBytes(tag);
        for (int i = start; i <= b.Length - t.Length; i++)
        {
            bool m = true;
            for (int j = 0; j < t.Length; j++) if (b[i + j] != t[j]) { m = false; break; }
            if (m) return i;
        }
        return -1;
    }
}
