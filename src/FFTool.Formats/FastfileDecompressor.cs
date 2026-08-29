using System.Buffers.Binary;
using FFTool.Native;

namespace FFTool.Formats;

/// <summary>Result of decompressing a fastfile's Oodle-compressed zone.</summary>
public sealed class DecompressedZone
{
    public required byte[] Data { get; init; }
    public required int BlockCount { get; init; }
    public required long DeclaredSize { get; init; }
    public bool Complete => DeclaredSize == 0 || Data.LongLength >= DeclaredSize;
}

/// <summary>
/// Decompresses an IWffa100 fastfile's inner zone. IMPORTANT CORRECTION: the .ff
/// container is Oodle-compressed, NOT encrypted — the zone reconstructs exactly
/// (verified: mp_rew_zodiac.ff → 82,649,772 bytes == its declared size). Asset
/// name strings *inside* the zone are hash-referenced and resolved by the game's
/// own string table at runtime, so decompression yields the binary asset graph
/// (structs, dimensions, raw data) but not human names.
///
/// The zone is a sequence of Oodle blocks (≤0x10000 decompressed each). Block
/// framing includes a per-block hash, so we locate each block empirically via
/// Oodle's fuzz-safe validation and walk to the declared size.
/// </summary>
public static class FastfileDecompressor
{
    private const int BlockSize = 0x10000;

    public static DecompressedZone Decompress(string path, IProgress<double>? progress = null,
                                              CancellationToken ct = default)
    {
        var file = File.ReadAllBytes(path);
        if (file.Length < 0x18 || file[0] != (byte)'I' || file[1] != (byte)'W')
            throw new InvalidDataException("Not an IWff fastfile.");

        long declared = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(0x14));

        // Locate the first Oodle block (scan a small header window).
        int pos = -1;
        for (int off = 0x30; off < 0x400 && off < file.Length - 16; off++)
        {
            if (TryBlock(file, off, BlockSize, out _) ||
                (declared is > 0 and <= BlockSize && TryBlock(file, off, (int)declared, out _)))
            { pos = off; break; }
        }
        if (pos < 0) throw new InvalidDataException("No Oodle block found (unrecognized fastfile).");

        int capacity = declared > 0 ? (int)Math.Min(declared, int.MaxValue) : (int)Math.Min((long)file.Length * 8, 1 << 30);
        using var outMs = new MemoryStream(capacity);
        int blocks = 0;
        while (pos < file.Length - 16 && (declared == 0 || outMs.Length < declared))
        {
            ct.ThrowIfCancellationRequested();
            int want = declared > 0 ? (int)Math.Min(BlockSize, declared - outMs.Length) : BlockSize;
            if (!TryBlock(file, pos, want, out var block) &&
                !TryBlock(file, pos, BlockSize, out block))
                break;
            // Never exceed the declared zone size (a full 64KiB fallback block may overrun the tail).
            int writeLen = declared > 0 ? (int)Math.Min(block!.Length, declared - outMs.Length) : block!.Length;
            outMs.Write(block!, 0, writeLen);
            blocks++;
            if (declared > 0 && outMs.Length >= declared) break;
            if (progress is not null && declared > 0) progress.Report((double)outMs.Length / declared);

            // Find the next block start (contiguous — scan forward, fuzz-safe).
            int next = -1;
            int remaining = declared > 0 ? (int)Math.Min(BlockSize, declared - outMs.Length) : BlockSize;
            for (int step = 8; step < 0x20000 && pos + step < file.Length - 16; step += 4)
            {
                if (TryBlock(file, pos + step, remaining, out _)) { next = pos + step; break; }
            }
            if (next < 0) break;
            pos = next;
        }

        return new DecompressedZone
        {
            Data = outMs.ToArray(),
            BlockCount = blocks,
            DeclaredSize = declared,
        };
    }

    private static bool TryBlock(byte[] file, int offset, int decompSize, out byte[]? result)
    {
        result = null;
        if (decompSize <= 0 || offset < 0 || offset >= file.Length) return false;
        int srcLen = Math.Min(file.Length - offset, decompSize + 0x1000);
        result = Oodle.TryDecompress(file.AsSpan(offset, srcLen), decompSize);
        return result is not null;
    }
}
