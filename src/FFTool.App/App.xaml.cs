using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using FFTool.Formats;

namespace FFTool.App;

/// <summary>
/// Interaction logic for App.xaml. Also handles headless catalog commands that run without a
/// window and exit — used to track what a game update changed:
///   --snapshot &lt;out.snap&gt; [gameDir]                 capture the current catalog
///   --diff &lt;old.snap&gt; &lt;new.snap|gameDir&gt; [report.txt] compare two catalogs
/// </summary>
public partial class App : Application
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && e.Args[0] is "--snapshot" or "--diff")
        {
            AttachConsole(-1);                       // write to the launching terminal if any
            int code;
            try { code = RunCatalogCli(e.Args); }
            catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); code = 1; }
            Shutdown(code);                          // no window
            return;
        }
        base.OnStartup(e);
    }

    private static int RunCatalogCli(string[] args)
    {
        string? DefaultGameDir() => Settings.Load().LastGameDir;

        if (args[0] == "--snapshot")
        {
            if (args.Length < 2) { Console.Error.WriteLine("usage: --snapshot <out.snap> [gameDir]"); return 2; }
            string outPath = args[1];
            string? dir = args.Length >= 3 ? args[2] : DefaultGameDir();
            if (dir is null || !Directory.Exists(dir)) { Console.Error.WriteLine($"game dir not found: {dir}"); return 2; }
            var snap = CatalogSnapshot.Capture(dir);
            snap.Save(outPath);
            Console.WriteLine($"snapshot: {snap.Packages.Count} packages, {snap.TotalAssets} assets -> {outPath}");
            return 0;
        }

        // --diff <old.snap> <new.snap|gameDir> [report.txt]
        if (args.Length < 3) { Console.Error.WriteLine("usage: --diff <old.snap> <new.snap|gameDir> [report.txt]"); return 2; }
        var old = CatalogSnapshot.Load(args[1]);
        CatalogSnapshot @new = Directory.Exists(args[2]) ? CatalogSnapshot.Capture(args[2]) : CatalogSnapshot.Load(args[2]);
        var diff = CatalogSnapshot.Compare(old, @new);
        string report = diff.ToReport();
        string reportPath = args.Length >= 4 ? args[3] : Path.ChangeExtension(args[1], ".diff.txt");
        File.WriteAllText(reportPath, report);
        // console summary
        Console.WriteLine($"diff: +{diff.TotalAdded} added  -{diff.TotalRemoved} removed  ~{diff.TotalChanged} changed");
        if (diff.AddedPackages.Count > 0) Console.WriteLine($"  new packages: {string.Join(", ", diff.AddedPackages)}");
        if (diff.RemovedPackages.Count > 0) Console.WriteLine($"  removed packages: {string.Join(", ", diff.RemovedPackages)}");
        Console.WriteLine($"full report -> {reportPath}");
        return 0;
    }
}
