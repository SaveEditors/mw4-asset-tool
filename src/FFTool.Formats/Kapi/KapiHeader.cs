using System.Buffers.Binary;

namespace FFTool.Formats.Kapi;

/// <summary>KAPI package kind (header +0x10).</summary>
public enum KapiType { Unknown = 0, Index = 2, Data = 3, Cdn = 4 }

/// <summary>
/// KAPI (XPAK/XSUB) v3 header. Field offsets verified against real cod26/MW4
/// files (see research/FINDINGS.md and research/spike_xsub.py):
///   +0x00 'KAPI' · +0x04 u16 ver=3 · +0x06 u16 subver=23 · +0x10 u32 type
///   +0x18 u64 self file-size · +0x20 u64 GUID · +0x28 u64 paired-xsub count.
/// </summary>
public readonly record struct KapiHeader
{
    public const uint MagicLe = 0x4950414B; // 'KAPI' little-endian
    public const int Size = 0x30;

    public ushort Version { get; init; }
    public ushort SubVersion { get; init; }
    public KapiType Type { get; init; }
    public ulong FileSize { get; init; }
    public ulong Guid { get; init; }
    public ulong DataFileCount { get; init; }

    public static bool TryParse(ReadOnlySpan<byte> b, out KapiHeader header)
    {
        header = default;
        if (b.Length < Size) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(b) != MagicLe) return false;

        header = new KapiHeader
        {
            Version       = BinaryPrimitives.ReadUInt16LittleEndian(b[0x04..]),
            SubVersion    = BinaryPrimitives.ReadUInt16LittleEndian(b[0x06..]),
            Type          = (KapiType)BinaryPrimitives.ReadUInt32LittleEndian(b[0x10..]),
            FileSize      = BinaryPrimitives.ReadUInt64LittleEndian(b[0x18..]),
            Guid          = BinaryPrimitives.ReadUInt64LittleEndian(b[0x20..]),
            DataFileCount = BinaryPrimitives.ReadUInt64LittleEndian(b[0x28..]),
        };
        return true;
    }
}

/// <summary>
/// XSUBHeaderV2 tail fields located at +0x788 (after Magic..Size and the
/// UnknownHashes[1896] region). Verified against real cod26 files:
/// HashCount·HashSize == HashCount*0x14; HashOffset is the asset table start.
/// </summary>
public readonly record struct KapiIndexHeader
{
    public const int TailOffset = 0x788;
    public const int FullSize = 0x800;

    public ulong FileCount { get; init; }
    public ulong DataOffset { get; init; }
    public ulong DataSize { get; init; }
    public ulong HashCount { get; init; }
    public ulong HashOffset { get; init; }
    public ulong HashSize { get; init; }
    public ulong IndexCount { get; init; }
    public ulong IndexOffset { get; init; }
    public ulong IndexSize { get; init; }

    public static KapiIndexHeader Read(ReadOnlySpan<byte> full)
    {
        var t = full[TailOffset..];
        return new KapiIndexHeader
        {
            FileCount  = BinaryPrimitives.ReadUInt64LittleEndian(t),
            DataOffset = BinaryPrimitives.ReadUInt64LittleEndian(t[8..]),
            DataSize   = BinaryPrimitives.ReadUInt64LittleEndian(t[16..]),
            HashCount  = BinaryPrimitives.ReadUInt64LittleEndian(t[24..]),
            HashOffset = BinaryPrimitives.ReadUInt64LittleEndian(t[32..]),
            HashSize   = BinaryPrimitives.ReadUInt64LittleEndian(t[40..]),
            // 48=Unknown3, 56=UnknownOffset, 64=Unknown4
            IndexCount  = BinaryPrimitives.ReadUInt64LittleEndian(t[72..]),
            IndexOffset = BinaryPrimitives.ReadUInt64LittleEndian(t[80..]),
            IndexSize   = BinaryPrimitives.ReadUInt64LittleEndian(t[88..]),
        };
    }
}

/// <summary>
/// XSUBHashEntryV2 (0x14 bytes): one asset. Offset/size unpacking verified
/// end-to-end (research/spike_extract.py): Ex == total decompressed size.
/// </summary>
public readonly record struct KapiAssetEntry(ulong Key, ulong PackedInfo, uint Ex)
{
    public const int Size = 0x14;

    public ulong Offset => (PackedInfo >> 32) << 7;
    public uint CompressedSize => (uint)((PackedInfo >> 1) & 0x3FFFFFFF);
    public uint DecompressedSize => Ex;

    public static KapiAssetEntry Read(ReadOnlySpan<byte> b) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(b),
        BinaryPrimitives.ReadUInt64LittleEndian(b[8..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[16..]));
}

/// <summary>XSUBBlockV2 (0x15 bytes, packed) — verified layout.</summary>
public readonly record struct XsubBlock(
    byte Compression, uint CompressedSize, uint DecompressedSize,
    uint BlockOffset, uint DecompressedOffset, uint Unknown)
{
    public const int Size = 0x15;

    public static XsubBlock Read(ReadOnlySpan<byte> b) => new(
        b[0],
        BinaryPrimitives.ReadUInt32LittleEndian(b[1..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[5..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[9..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[13..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[17..]));
}
