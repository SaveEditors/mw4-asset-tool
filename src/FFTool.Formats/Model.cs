namespace FFTool.Formats;

/// <summary>High-level asset category (grows as handlers are added).</summary>
public enum AssetType { Unknown = 0, Image, Model, Anim, Sound, Material, Techset, RawFile }

/// <summary>xsub block compression flag (verified: 0x0 none, 0x3 LZ4, 0x6 Oodle).</summary>
public enum CompAlgo { None = 0x0, Lz4 = 0x3, Oodle = 0x6 }

/// <summary>
/// One catalog entry. <see cref="Name"/> is null until (later) fastfile name
/// resolution; assets are always addressable by <see cref="AssetHash"/>.
/// </summary>
public sealed record AssetRecord
{
    public required ulong PackageGuid { get; init; }
    public required ulong AssetHash { get; init; }
    public string? Name { get; init; }
    public AssetType Type { get; init; } = AssetType.Unknown;

    // Location within the paired data (.xsub) set.
    public int DataFileIndex { get; init; }
    public ulong DataOffset { get; init; }
    public uint CompressedSize { get; init; }
    public uint DecompressedSize { get; init; }
    public CompAlgo Compression { get; init; }

    // Texture-specific (nullable until decoded/known).
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? MipCount { get; init; }
    public ImageFormat? ImageFormat { get; init; }

    public string DisplayName => Name ?? $"0x{AssetHash:x16}";
}

/// <summary>A parsed KAPI package set: one .xpak index and its paired .xsub data files.</summary>
public sealed class PackageSet
{
    public required ulong Guid { get; init; }
    public string? XpakPath { get; init; }
    public required IReadOnlyList<string> XsubPaths { get; init; }
    public int Version { get; init; }
    public int SubVersion { get; init; }
    public long AssetCount { get; set; }
}
