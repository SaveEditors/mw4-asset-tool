using System.Runtime.InteropServices;

namespace FFTool.Native;

/// <summary>
/// P/Invoke wrapper around Oodle (oo2core_8_win64.dll) — the compressor used by
/// modern CoD (.xsub) data blocks. Signatures verified end-to-end against the
/// game's own DLL via a compress+decompress round-trip (research/spike_oodle.py).
/// </summary>
public static class Oodle
{
    public const string LibraryName = "oo2core_8_win64";

    private static string? _resolvedPath;
    private static bool _resolverInstalled;
    private static readonly object Gate = new();

    /// <summary>
    /// Point the loader at a specific oo2core_8_win64.dll (e.g. the one shipped
    /// beside the game: ...\steamapps\common\game\oo2core_8_win64.dll).
    /// </summary>
    public static void UseLibrary(string dllPath)
    {
        if (!File.Exists(dllPath))
            throw new OodleMissingException(dllPath);

        lock (Gate)
        {
            _resolvedPath = dllPath;
            if (!_resolverInstalled)
            {
                NativeLibrary.SetDllImportResolver(typeof(Oodle).Assembly, Resolve);
                _resolverInstalled = true;
            }
        }
    }

    /// <summary>True once a valid oo2core DLL has been located.</summary>
    public static bool IsAvailable => _resolvedPath is not null && File.Exists(_resolvedPath);

    private static nint Resolve(string libraryName, System.Reflection.Assembly asm, DllImportSearchPath? path)
    {
        if (libraryName == LibraryName && _resolvedPath is not null)
            return NativeLibrary.Load(_resolvedPath);
        return nint.Zero;
    }

    /// <summary>
    /// Decompress an Oodle block. <paramref name="decompressedSize"/> must be the
    /// exact declared output size from the block header.
    /// </summary>
    public static byte[] Decompress(ReadOnlySpan<byte> compressed, int decompressedSize)
    {
        if (!IsAvailable)
            throw new OodleMissingException(_resolvedPath ?? LibraryName + ".dll");

        var dst = new byte[decompressedSize];
        var src = compressed.ToArray();
        long n = OodleLZ_Decompress(src, src.LongLength, dst, decompressedSize,
            fuzzSafe: 1, checkCrc: 0, verbosity: 0,
            decBufBase: nint.Zero, decBufSize: 0, fpCallback: nint.Zero, callbackUserData: nint.Zero,
            decoderMemory: nint.Zero, decoderMemorySize: 0, threadPhase: 3);

        if (n != decompressedSize)
            throw new InvalidDataException($"Oodle decompress returned {n}, expected {decompressedSize}.");
        return dst;
    }

    /// <summary>
    /// Non-throwing decompress for fuzz-safe scanning: returns the bytes only when the
    /// output length exactly matches <paramref name="decompressedSize"/>, else null.
    /// Used to locate valid Oodle block starts without exception overhead.
    /// </summary>
    public static byte[]? TryDecompress(ReadOnlySpan<byte> compressed, int decompressedSize)
    {
        if (!IsAvailable || decompressedSize <= 0) return null;
        var dst = new byte[decompressedSize];
        var src = compressed.ToArray();
        long n = OodleLZ_Decompress(src, src.LongLength, dst, decompressedSize,
            fuzzSafe: 1, checkCrc: 0, verbosity: 0,
            decBufBase: nint.Zero, decBufSize: 0, fpCallback: nint.Zero, callbackUserData: nint.Zero,
            decoderMemory: nint.Zero, decoderMemorySize: 0, threadPhase: 3);
        return n == decompressedSize ? dst : null;
    }

    // SINTa == 64-bit signed. Matches the signatures validated in spike_oodle.py.
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern long OodleLZ_Decompress(
        byte[] srcBuf, long srcLen, byte[] dstBuf, long dstLen,
        int fuzzSafe, int checkCrc, int verbosity,
        nint decBufBase, long decBufSize, nint fpCallback, nint callbackUserData,
        nint decoderMemory, long decoderMemorySize, int threadPhase);
}

public sealed class OodleMissingException(string path)
    : Exception($"Oodle library not found or not loadable: '{path}'. " +
                "Select a game directory that contains oo2core_8_win64.dll.");
