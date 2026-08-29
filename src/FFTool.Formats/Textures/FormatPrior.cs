namespace FFTool.Formats;

/// <summary>
/// A learned prior over texture interpretations, built from the user's confirmed format
/// choices. Every time the user picks the correct interpretation in the "show all formats"
/// tool, a row is appended to <c>format_choices.csv</c>; this class reads those rows back and
/// feeds them into auto-detection, so the heuristic measurably improves with use.
///
/// The prior is <b>empty until the first correction</b> — with no data every bonus is 0, so
/// out-of-the-box detection is byte-for-byte unchanged. It grows as confirmations accumulate:
/// an exact (size, format, w, h) that has been confirmed before dominates ties for that size;
/// a format confirmed for that size (different dims) gets a smaller push; and a format that is
/// simply common across all confirmations gets a light global nudge.
/// </summary>
public sealed class FormatPrior
{
    private readonly Dictionary<(long Size, ImageFormat Fmt, int W, int H), int> _exact = new();
    private readonly Dictionary<long, Dictionary<ImageFormat, int>> _bySizeFormat = new();
    private readonly Dictionary<ImageFormat, int> _global = new();
    private int _total;

    /// <summary>How many confirmations this prior was built from (0 = neutral / no effect).</summary>
    public int SampleCount => _total;

    /// <summary>A prior with no data — every bonus is 0.</summary>
    public static readonly FormatPrior Empty = new();

    /// <summary>
    /// Support in [0, 1] for interpreting a blob of <paramref name="size"/> bytes as
    /// <paramref name="g"/>, from past confirmations. 0 when unseen (neutral).
    /// </summary>
    public double Bonus(long size, TextureGuess g)
    {
        if (_total == 0) return 0;

        // Strongest evidence: this exact interpretation was confirmed for this size before.
        if (_exact.TryGetValue((size, g.Format, g.Width, g.Height), out int ex) && ex > 0)
        {
            int sizeTotal = SizeTotal(size);
            return sizeTotal > 0 ? 0.5 + 0.5 * ex / sizeTotal : 1.0;
        }
        // Next: this FORMAT was confirmed for this size (dimensions may differ). Kept SMALL so it
        // never overrides an EXACT confirmation for the same size — a same-format candidate with a
        // higher crop score must not beat the dimensions the user actually confirmed.
        if (_bySizeFormat.TryGetValue(size, out var byFmt))
        {
            int f = byFmt.GetValueOrDefault(g.Format);
            int t = SizeTotal(size);
            if (t > 0 && f > 0) return 0.08 * f / t;
        }
        // Weakest: global frequency of this format across every confirmation.
        int gf = _global.GetValueOrDefault(g.Format);
        return gf > 0 ? 0.03 * gf / _total : 0;
    }

    private int SizeTotal(long size)
    {
        int t = 0;
        if (_bySizeFormat.TryGetValue(size, out var fm)) foreach (var v in fm.Values) t += v;
        return t;
    }

    private void Add(long size, ImageFormat fmt, int w, int h)
    {
        var key = (size, fmt, w, h);
        _exact[key] = _exact.GetValueOrDefault(key) + 1;
        var bf = _bySizeFormat.TryGetValue(size, out var d) ? d : (_bySizeFormat[size] = new());
        bf[fmt] = bf.GetValueOrDefault(fmt) + 1;
        _global[fmt] = _global.GetValueOrDefault(fmt) + 1;
        _total++;
    }

    /// <summary>Build a prior from a <c>format_choices.csv</c> written by the picker tool.</summary>
    public static FormatPrior Load(string csvPath)
    {
        var p = new FormatPrior();
        try
        {
            if (!File.Exists(csvPath)) return p;
            bool header = true;
            foreach (var line in File.ReadLines(csvPath))
            {
                if (header) { header = false; continue; }  // hash,blob_bytes,chosen_format,chosen_w,chosen_h,...
                var c = line.Split(',');
                if (c.Length < 5) continue;
                if (!long.TryParse(c[1], out long size)) continue;
                if (!Enum.TryParse<ImageFormat>(c[2], true, out var fmt) || fmt == ImageFormat.Unknown) continue;
                if (!int.TryParse(c[3], out int w) || !int.TryParse(c[4], out int h)) continue;
                if (w <= 0 || h <= 0) continue;
                p.Add(size, fmt, w, h);
            }
        }
        catch { /* locked/corrupt line → keep whatever parsed */ }
        return p;
    }

    // ---- process-wide instance, reloaded automatically when the log file changes ----
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MW4FFTool", "format_choices.csv");
    private static FormatPrior _current = Empty;
    private static long _stamp = -1;
    private static long _nextCheckTicks;               // throttle the disk stat on the hot path
    private static readonly object Gate = new();

    /// <summary>
    /// The current learned prior, transparently reloaded when the log changes. The change
    /// check is throttled (~2 s) so calling this per asset across a 100k-row preload costs
    /// nothing — between checks the cached instance is returned with no I/O.
    /// </summary>
    public static FormatPrior Current
    {
        get
        {
            long now = Environment.TickCount64;
            if (now < Volatile.Read(ref _nextCheckTicks)) return _current;
            Volatile.Write(ref _nextCheckTicks, now + 2000);
            try
            {
                long stamp = File.Exists(DefaultPath) ? File.GetLastWriteTimeUtc(DefaultPath).Ticks : 0;
                if (stamp != Volatile.Read(ref _stamp))
                    lock (Gate)
                        if (stamp != _stamp) { _current = Load(DefaultPath); Volatile.Write(ref _stamp, stamp); }
            }
            catch { /* fall back to last good */ }
            return _current;
        }
    }
}
