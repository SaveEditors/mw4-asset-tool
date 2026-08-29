using System.Security.Cryptography;
using System.Text;

namespace FFTool.Formats;

/// <summary>
/// Silently tracks game updates. On each launch the app calls <see cref="CheckAndRecord"/>,
/// which cheaply fingerprints the install (package file names/sizes/timestamps — no reads). If
/// the fingerprint changed since last time, it captures a full catalog snapshot, diffs it
/// against the previous snapshot, and archives BOTH the snapshot and the diff — so every update
/// is preserved automatically without the user ever running a command.
///
/// Store: <c>%LOCALAPPDATA%\MW4FFTool\snapshots\</c>
///   latest.snap / latest.fingerprint  — rolling baseline
///   snapshot_&lt;utc&gt;.snap             — archived catalog for each update
///   diff_&lt;utc&gt;.txt                  — what that update changed
/// </summary>
public static class UpdateTracker
{
    public enum Kind { Unchanged, FirstRun, Updated }

    public sealed record Result(Kind Kind, CatalogDiff? Diff, long AssetCount, string? DiffReportPath, string StoreDir);

    public static string StoreDir
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                 "MW4FFTool", "snapshots");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static string FingerprintPath => Path.Combine(StoreDir, "latest.fingerprint");
    private static string LatestSnapPath => Path.Combine(StoreDir, "latest.snap");
    private static string PreviousSnapPath => Path.Combine(StoreDir, "previous.snap");

    /// <summary>
    /// The changes from the most recent game update (current install vs the version before it),
    /// available on ANY launch until the next update — so a modder can always filter to "new /
    /// changed since the last patch". Null when there is no prior version to compare against.
    /// </summary>
    public static CatalogDiff? LastUpdateChanges()
    {
        try
        {
            if (!File.Exists(PreviousSnapPath) || !File.Exists(LatestSnapPath)) return null;
            return CatalogSnapshot.Compare(CatalogSnapshot.Load(PreviousSnapPath), CatalogSnapshot.Load(LatestSnapPath));
        }
        catch { return null; }
    }

    /// <summary>Cheap install version: hash of every package file's name, size and write time.</summary>
    public static string InstallFingerprint(string gameDir)
    {
        var files = Directory.EnumerateFiles(gameDir, "*.xpak")
            .Concat(Directory.EnumerateFiles(gameDir, "*.xsub"))
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var f in files)
            sb.Append(f.Name).Append('|').Append(f.Length).Append('|').Append(f.LastWriteTimeUtc.Ticks).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>
    /// Detect whether the install changed since last run; if so, snapshot + diff + archive.
    /// Safe to call on every import; returns quickly (<see cref="Kind.Unchanged"/>) when nothing changed.
    /// </summary>
    public static Result CheckAndRecord(string gameDir)
    {
        string fp = InstallFingerprint(gameDir);
        string? prevFp = File.Exists(FingerprintPath) ? File.ReadAllText(FingerprintPath).Trim() : null;
        if (prevFp == fp && File.Exists(LatestSnapPath))
            return new Result(Kind.Unchanged, null, 0, null, StoreDir);

        var snap = CatalogSnapshot.Capture(gameDir);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        snap.Save(Path.Combine(StoreDir, $"snapshot_{stamp}.snap"));   // archive this version

        CatalogDiff? diff = null; string? reportPath = null;
        Kind kind = File.Exists(LatestSnapPath) ? Kind.Updated : Kind.FirstRun;
        if (kind == Kind.Updated)
        {
            try
            {
                diff = CatalogSnapshot.Compare(CatalogSnapshot.Load(LatestSnapPath), snap);
                if (diff.AnyChange)
                {
                    reportPath = Path.Combine(StoreDir, $"diff_{stamp}.txt");
                    File.WriteAllText(reportPath, diff.ToReport());
                }
                // Preserve the pre-update snapshot as "previous" so the changes stay queryable
                // on every later launch (LastUpdateChanges), not just this one.
                File.Copy(LatestSnapPath, PreviousSnapPath, overwrite: true);
            }
            catch { /* corrupt previous baseline → just roll it forward */ }
        }

        snap.Save(LatestSnapPath);
        File.WriteAllText(FingerprintPath, fp);
        return new Result(kind, diff, snap.TotalAssets, reportPath, StoreDir);
    }
}
