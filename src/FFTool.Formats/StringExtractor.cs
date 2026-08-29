using System.Text;

namespace FFTool.Formats;

/// <summary>Extracts human-readable strings from decompressed asset bytes.</summary>
public static class StringExtractor
{
    /// <summary>All printable ASCII runs of length >= <paramref name="minLen"/>.</summary>
    public static IReadOnlyList<string> Extract(ReadOnlySpan<byte> data, int minLen = 4, int max = 5000)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        foreach (byte b in data)
        {
            if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            else
            {
                if (sb.Length >= minLen) { result.Add(sb.ToString()); if (result.Count >= max) break; }
                sb.Clear();
            }
        }
        if (sb.Length >= minLen && result.Count < max) result.Add(sb.ToString());
        return result;
    }

    /// <summary>True if the bytes contain <paramref name="query"/> as printable ASCII (case-insensitive).</summary>
    public static bool Contains(ReadOnlySpan<byte> data, string query)
    {
        if (query.Length == 0) return false;
        // Scan printable runs and test each; avoids matching across non-printable gaps.
        int start = -1;
        for (int i = 0; i <= data.Length; i++)
        {
            bool printable = i < data.Length && data[i] >= 0x20 && data[i] < 0x7F;
            if (printable) { if (start < 0) start = i; }
            else if (start >= 0)
            {
                if (RunContains(data.Slice(start, i - start), query)) return true;
                start = -1;
            }
        }
        return false;
    }

    private static bool RunContains(ReadOnlySpan<byte> run, string q)
    {
        if (run.Length < q.Length) return false;
        for (int i = 0; i + q.Length <= run.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < q.Length; j++)
            {
                int c = run[i + j]; int qc = q[j];
                if (c >= 'A' && c <= 'Z') c += 32;
                if (qc >= 'A' && qc <= 'Z') qc += 32;
                if (c != qc) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }
}
