using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FFTool.Formats;
using FFTool.Formats.Kapi;
using Microsoft.Win32;

namespace FFTool.App;

public sealed class PackageRow : ViewModelBase
{
    private readonly PackageSet _set;

    public PackageRow(PackageSet set, long totalBytes)
    {
        _set = set;
        Name = set.XpakPath is { } p ? KapiReader.StemOf(p)
               : (set.XsubPaths.Count > 0 ? KapiReader.StemOf(set.XsubPaths[0]) : "?");
        HasIndex = set.XpakPath is not null;
        XsubCount = set.XsubPaths.Count;
        Size = Format.Bytes(totalBytes);
        Guid = $"0x{set.Guid:x16}";
    }

    public string Name { get; }
    public bool HasIndex { get; }
    public int XsubCount { get; }
    public string Size { get; }
    public string Guid { get; }
    public PackageSet Set => _set;

    // ── Asset counts (filled in by a background scan after import) ───────────────
    private int _assetCount = -1, _offDiskCount = -1, _imageCount = -1;
    public bool CountsKnown => _assetCount >= 0;

    public void SetCounts(int total, int offDisk, int images)
    {
        _assetCount = total; _offDiskCount = offDisk; _imageCount = images;
        Raise(nameof(CountsKnown)); Raise(nameof(AssetsText)); Raise(nameof(CdnText));
        Raise(nameof(HasCdn)); Raise(nameof(CountsTip));
        Raise(nameof(AssetCountValue)); Raise(nameof(OffDiskValue));
    }

    // Numeric values so the sidebar columns sort by magnitude, not lexically. Still-unknown
    // counts are -1, which sorts them to the top on an ascending sort (they fill in within a
    // second or two as the background scan completes).
    public int AssetCountValue => _assetCount;
    public int OffDiskValue => _offDiskCount;

    /// <summary>Total on-disk-installable + CDN asset count, e.g. "45,016".</summary>
    public string AssetsText => _assetCount < 0 ? "…" : _assetCount.ToString("N0");

    /// <summary>CDN-stub (off-disk) count for the sidebar column; blank when zero/unknown.</summary>
    public string CdnText => _offDiskCount <= 0 ? "" : _offDiskCount.ToString("N0");
    public bool HasCdn => _offDiskCount > 0;

    /// <summary>Rich hover summary: counts + disk size + content category.</summary>
    public string CountsTip => _assetCount < 0
        ? $"{Content}\n{Size} on disk · counting assets…"
        : $"{Content}\n{_assetCount:N0} assets · {_imageCount:N0} images · " +
          $"{_assetCount - _offDiskCount:N0} installed · {_offDiskCount:N0} CDN-only\n{Size} on disk";

    /// <summary>Human content category derived from the package name (the game groups content this way).</summary>
    public string Content => Categorize(Stem);

    private string Stem => _set.XpakPath is { } p ? KapiReader.StemOf(p)
                           : (_set.XsubPaths.Count > 0 ? KapiReader.StemOf(_set.XsubPaths[0]) : "?");

    /// <summary>A muted accent brush per category, for the sidebar's category dot.</summary>
    public Brush CategoryBrush => CategoryBrushOf(Stem);

    private static string Categorize(string stem)
    {
        string s = stem.ToLowerInvariant();
        if (s.StartsWith("eng_") || s.StartsWith("ens_") || s.StartsWith("ww_")) return "Localization";
        if (s.Contains("mtx")) return "Store / cosmetics (camo, operator, cards, emblems)";
        if (s.Contains("wz"))  return "Warzone (maps)";
        if (s.Contains("mp"))  return "Multiplayer (maps, weapons)";
        if (s.Contains("sp") || s.Contains("rex")) return "Campaign";
        if (s.Contains("codhq")) return "Core / frontend UI";
        if (s.Contains("boot")) return "Boot";
        return "Other";
    }

    private static readonly Dictionary<string, Brush> _categoryBrushes = new();
    private static readonly object _brushGate = new();
    private static Brush CategoryBrushOf(string stem)
    {
        string s = stem.ToLowerInvariant();
        string hex =
              s.StartsWith("eng_") || s.StartsWith("ens_") || s.StartsWith("ww_") ? "#FF6B7280" // Localization — grey
            : s.Contains("mtx") ? "#FFC084FC"   // Store — violet
            : s.Contains("wz")  ? "#FF34D399"   // Warzone — green
            : s.Contains("mp")  ? "#FF5B9DFF"   // Multiplayer — blue
            : s.Contains("sp") || s.Contains("rex") ? "#FFFB923C" // Campaign — orange
            : s.Contains("codhq") ? "#FF22D3EE" // Core — cyan
            : s.Contains("boot") ? "#FFFBBF24"  // Boot — amber
            : "#FF9AA7B4";                       // Other — muted
        lock (_brushGate)
        {
            if (!_categoryBrushes.TryGetValue(hex, out var brush))
            {
                brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
                brush.Freeze();
                _categoryBrushes[hex] = brush;
            }
            return brush;
        }
    }
}

public sealed class AssetRow : ViewModelBase
{
    public AssetRow(KapiAssetEntry entry)
    {
        Entry = entry;
        Hash = $"0x{entry.Key:x16}";
        ShortHash = $"{entry.Key:x16}"[..8];
        Offset = $"0x{entry.Offset:x}";
        Decompressed = entry.DecompressedSize;
        Compressed = entry.CompressedSize;
        Size = Format.Bytes(entry.DecompressedSize);
        Packed = Format.Bytes(entry.CompressedSize);
        IsImageShaped = ThumbnailProvider.IsImageShaped(entry.DecompressedSize);
        if (IsImageShaped && DimensionGuesser.Best((long)entry.DecompressedSize) is { } g)
        {
            GuessW = g.Width; GuessH = g.Height;
            double ar = (double)g.Width / g.Height;      // simple shape bucket, no role guessing
            Shape = ar > 1.25 ? "Wide" : ar < 0.8 ? "Tall" : "Square";
        }
    }

    public int GuessW { get; }
    public int GuessH { get; }
    /// <summary>Simple shape bucket for filtering: Square / Wide / Tall (empty for non-images).</summary>
    public string Shape { get; } = "";
    public string Dims => GuessW > 0 ? $"{GuessW}×{GuessH}" : "";

    public KapiAssetEntry Entry { get; }
    public string Hash { get; }
    public string ShortHash { get; }
    public string Offset { get; }
    public long Decompressed { get; }
    public long Compressed { get; }
    public string Size { get; }
    public string Packed { get; }
    public bool IsImageShaped { get; }

    /// <summary>Category for the Type column / filter: Image | Data | Off-disk. Set at load.</summary>
    public string Category { get; set; } = "Data";

    /// <summary>Set at load from the last-update diff: this asset is new / changed since the last game patch.</summary>
    public bool IsNew { get; set; }
    public bool IsChanged { get; set; }
    /// <summary>Update badge for the Type column ("NEW" / "CHG"), else empty.</summary>
    public string ChangeTag => IsNew ? "NEW" : IsChanged ? "CHG" : "";
    public bool HasChangeTag => IsNew || IsChanged;
    /// <summary>Re-raise change-badge bindings after IsNew/IsChanged are updated in place.</summary>
    public void NotifyChangeTag() { Raise(nameof(ChangeTag)); Raise(nameof(HasChangeTag)); }

    private string? _refinedKind;
    /// <summary>Refine the Type once the asset has been inspected/decoded (e.g. "Sound (likely)").</summary>
    public void RefineKind(string kind) { if (_refinedKind != kind) { _refinedKind = kind; Raise(nameof(Kind)); } }
    public string Kind => _refinedKind ?? Category;
    public bool IsOffDisk => Category == "Off-disk";

    /// <summary>Real name from a loaded name database, else null.</summary>
    private string? _resolvedName;
    public string? ResolvedName { get => _resolvedName; set { if (Set(ref _resolvedName, value)) Raise(nameof(DisplayName)); } }
    /// <summary>Name if resolved, else the hash. This is what the browser's Name column shows.</summary>
    public string DisplayName => _resolvedName ?? Hash;
    public bool HasName => _resolvedName is not null;

    // Lazy thumbnail — the grid triggers EnsureThumbnail when a cell is realized.
    private static ThumbnailProvider? _provider;
    private static Action? _onScored;
    public static void SetProvider(ThumbnailProvider? p, Action? onScored = null)
    {
        _provider = p; _onScored = onScored;
    }

    private bool _requested;
    private System.Windows.Media.Imaging.BitmapSource? _thumbnail;
    public System.Windows.Media.Imaging.BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set => Set(ref _thumbnail, value);
    }

    /// <summary>null = not yet decoded; true/false = confirmed image vs non-image (by decode score).</summary>
    public bool? IsImage { get; private set; }

    /// <summary>Decode confidence [0,1] once a thumbnail has been evaluated (for sort/filter).</summary>
    public double Confidence { get; private set; }

    public async void EnsureThumbnail()
    {
        if (_requested || !IsImageShaped || _provider is null) return;
        _requested = true;
        var t = await _provider.GetAsync(Entry);
        Thumbnail = t.Image;
        // Smooth-fraction score: real textures ~0.5+, noise/wrong-decode ~0.2-0.4.
        IsImage = t.Image is not null && t.Score >= 0.45;  // keep confident images, hide noise
        Confidence = t.Score;
        _onScored?.Invoke();
    }
}

public sealed class MainViewModel : ViewModelBase
{
    private KapiPackage? _openPackage;

    public ObservableCollection<PackageRow> Packages { get; } = [];
    public ObservableCollection<ContentGroupRow> Content { get; } = [];
    public ObservableCollection<ContentEntry> ContentFiles { get; } = [];

    /// <summary>Recently-imported game folders (most recent first) for the "Recent" menu.</summary>
    public ObservableCollection<string> RecentFolders { get; } = [];
    public bool HasRecentFolders => RecentFolders.Count > 0;

    /// <summary>Open a folder from the recents list.</summary>
    public void OpenRecent(string dir) => ImportDirectory(dir);

    /// <summary>Empty the recent-folders list.</summary>
    public void ClearRecentFolders()
    {
        RecentFolders.Clear();
        Raise(nameof(HasRecentFolders));
        _settings.RecentFolders = [];
        _settings.Save();
    }

    private void AddRecentFolder(string dir)
    {
        RecentFolders.Remove(dir);
        RecentFolders.Insert(0, dir);
        while (RecentFolders.Count > 8) RecentFolders.RemoveAt(RecentFolders.Count - 1);
        Raise(nameof(HasRecentFolders));
        _settings.RecentFolders = [.. RecentFolders];
        _settings.Save();
    }

    private List<ContentEntry> _allContent = [];

    private bool _showContent;
    /// <summary>Left panel toggle: false = Packages (xpak), true = Content (maps/fastfiles).</summary>
    public bool ShowContent { get => _showContent; set { if (Set(ref _showContent, value)) { Raise(nameof(ShowPackages)); Raise(nameof(NoAssetsLoaded)); } } }
    public bool ShowPackages => !_showContent;

    private ContentGroupRow? _selectedContent;
    public ContentGroupRow? SelectedContent
    {
        get => _selectedContent;
        set { if (Set(ref _selectedContent, value)) ShowContentFiles(value); }
    }

    private void ShowContentFiles(ContentGroupRow? group)
    {
        ContentFiles.Clear();
        if (group is null) return;
        foreach (var e in _allContent
                     .Where(e => e.Content == group.Content && e.Category == group.Category)
                     .OrderByDescending(e => e.Size))
            ContentFiles.Add(e);
        Status = $"{group.Content}: {ContentFiles.Count} fastfiles ({group.Category}). " +
                 "Note: fastfiles are Oodle-compressed; asset names inside are hash-referenced " +
                 "(resolved by the game at runtime), so this lists content, not individual named assets.";
    }

    private ContentEntry? _selectedContentFile;
    public ContentEntry? SelectedContentFile
    {
        get => _selectedContentFile;
        set { if (Set(ref _selectedContentFile, value)) ShowFastfileHeader(value); }
    }

    private string _fastfileInfo = "";
    public string FastfileInfo { get => _fastfileInfo; set => Set(ref _fastfileInfo, value); }
    private bool _hasFastfileInfo;
    public bool HasFastfileInfo { get => _hasFastfileInfo; set { if (Set(ref _hasFastfileInfo, value)) Raise(nameof(NothingSelected)); } }

    private void ShowFastfileHeader(ContentEntry? e)
    {
        FastfileInfo = ""; HasFastfileInfo = false; ZonePreview = ""; HasZone = false;
        if (e is null || _gameDir is null) return;
        SelectedAsset = null;   // the inspector shows one thing at a time (asset OR fastfile)
        var path = Path.Combine(_gameDir, e.FileName);
        var info = FastfileHeader.TryRead(path);
        if (info is null) return;
        FastfileInfo = info.Describe();
        HasFastfileInfo = true;
    }

    private string _zonePreview = "";
    public string ZonePreview { get => _zonePreview; set => Set(ref _zonePreview, value); }
    private bool _hasZone;
    public bool HasZone { get => _hasZone; set => Set(ref _hasZone, value); }

    public ICommand DecompressZoneCommand { get; private set; } = null!;
    public ICommand ExportZoneCommand { get; private set; } = null!;

    private async Task<DecompressedZone?> DecompressCurrentAsync()
    {
        if (SelectedContentFile is null || _gameDir is null) return null;
        if (!_oodleReady) { Status = "oo2core not loaded."; return null; }
        var path = Path.Combine(_gameDir, SelectedContentFile.FileName);
        IsBusy = true; Progress = 0;
        try
        {
            var prog = new Progress<double>(p => Progress = p * 100);
            return await Task.Run(() => FastfileDecompressor.Decompress(path, prog));
        }
        catch (Exception ex) { Status = $"Decompress failed: {ex.Message}"; return null; }
        finally { IsBusy = false; }
    }

    private async Task DecompressZoneAsync()
    {
        var name = SelectedContentFile?.FileName;
        Status = $"Decompressing {name}…";
        var zone = await DecompressCurrentAsync();
        if (zone is null) return;
        ZonePreview = AssetSniffer.HexPreview(zone.Data, 512);
        HasZone = true;
        var completeness = zone.Complete ? "" : " (incomplete — some blocks unresolved)";
        Status = $"{name}: decompressed {zone.Data.Length:N0} bytes in {zone.BlockCount} blocks " +
                 $"(declared {zone.DeclaredSize:N0}){completeness}. Oodle-compressed — NOT encrypted.";
    }

    private async Task ExportZoneAsync()
    {
        if (SelectedContentFile is null) return;
        var dlg = new SaveFileDialog
        {
            FileName = Path.ChangeExtension(SelectedContentFile.FileName, ".zone.bin"),
            Filter = "Decompressed zone (*.bin)|*.bin",
        };
        if (dlg.ShowDialog() != true) return;
        var zone = await DecompressCurrentAsync();
        if (zone is null) return;
        try
        {
            await File.WriteAllBytesAsync(dlg.FileName, zone.Data);
            Status = $"Exported decompressed zone ({zone.Data.Length:N0} bytes) → {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { Status = $"Zone export failed: {ex.Message}"; }
    }

    private List<AssetRow> _allAssets = [];
    private readonly CollectionViewSource _assetsSource = new();
    public ICollectionView AssetsView => _assetsSource.View;

    private readonly CollectionViewSource _packagesSource = new();
    public ICollectionView PackagesView => _packagesSource.View;

    private string _packageFilter = "";
    /// <summary>Live filter over the sidebar package list (matches name or content category).</summary>
    public string PackageFilter { get => _packageFilter; set { if (Set(ref _packageFilter, value)) PackagesView?.Refresh(); } }

    private readonly Settings _settings = Settings.Load();
    /// <summary>The single settings instance — the window shares it for geometry so there is one source of truth.</summary>
    public Settings Settings => _settings;

    public MainViewModel()
    {
        // Use the CollectionViewSource.Filter EVENT (persists across Source changes),
        // not View.Filter (which is lost when Source is reassigned).
        _assetsSource.Filter += (_, e) => e.Accepted = Match((AssetRow)e.Item);
        _assetsSource.Source = _allAssets;

        foreach (var f in _settings.RecentFolders.Where(Directory.Exists)) RecentFolders.Add(f);
        _settings.RecentFolders = [.. RecentFolders];   // prune dead paths from the persisted list
        Raise(nameof(HasRecentFolders));

        _packagesSource.Source = Packages;
        _packagesSource.Filter += (_, e) =>
        {
            if (_packageFilter.Length == 0) { e.Accepted = true; return; }
            var p = (PackageRow)e.Item;
            e.Accepted = p.Name.Contains(_packageFilter, StringComparison.OrdinalIgnoreCase)
                      || p.Content.Contains(_packageFilter, StringComparison.OrdinalIgnoreCase);
        };
        ImportCommand = new RelayCommand(Import);
        ExportGameCommand = new RelayCommand(() => _ = ExportWholeGameAsync(), () => Packages.Count > 0 && !IsBusy);
        ExportAssetCommand = new RelayCommand(() => _ = ExportSelectedAsync(), () => SelectedAsset is not null);
        ExportPackageCommand = new RelayCommand(() => _ = ExportPackageAsync(), () => _openPackage is not null && !IsBusy);
        ExportPngCommand = new RelayCommand(ExportPng, () => Preview is not null);
        ExportDdsCommand = new RelayCommand(ExportDds, () => SelectedCandidate is not null && _selectedBytes is not null);
        FindTextCommand = new RelayCommand(() => _ = FindTextAsync(), () => _openPackage is not null && !IsBusy);
        ClearTextCommand = new RelayCommand(ClearTextSearch, () => _textMatches is not null);
        LoadNamesCommand = new RelayCommand(LoadNames);
        DecompressZoneCommand = new RelayCommand(() => _ = DecompressZoneAsync(),
            () => SelectedContentFile is not null && _oodleReady && !IsBusy);
        ExportZoneCommand = new RelayCommand(() => _ = ExportZoneAsync(),
            () => SelectedContentFile is not null && _oodleReady && !IsBusy);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => _allAssets.Count > 0);
        ExportCsvAllCommand = new RelayCommand(() => _ = ExportCsvAllAsync(), () => Packages.Count > 0 && !IsBusy);
        ExportImagesCommand = new RelayCommand(() => _ = ExportImagesAsync(false), () => _openPackage is not null && _oodleReady && !IsBusy);
        ExportImagesAllCommand = new RelayCommand(() => _ = ExportImagesAsync(true), () => Packages.Count > 0 && _oodleReady && !IsBusy);
        CopyHashCommand = new RelayCommand(() => CopyToClipboard(SelectedAsset?.Hash, "hash"), () => SelectedAsset is not null);
        CopyOffsetCommand = new RelayCommand(() => CopyToClipboard(SelectedAsset?.Offset, "offset"), () => SelectedAsset is not null);
        CopyImageCommand = new RelayCommand(CopyImage, () => Preview is not null);
    }

    public ICommand CopyHashCommand { get; }
    public ICommand CopyOffsetCommand { get; }
    public ICommand CopyImageCommand { get; }

    /// <summary>Copy the decoded preview image to the clipboard.</summary>
    private void CopyImage()
    {
        if (Preview is null) return;
        try { System.Windows.Clipboard.SetImage(Preview); Status = "Preview image copied to clipboard."; }
        catch (Exception ex) { Status = $"Copy image failed: {ex.Message}"; }
    }

    // ── "Reveal in Explorer" for the last export ────────────────────────────────
    private string? _lastExportPath;
    public bool HasLastExport => _lastExportPath is not null;
    private void SetLastExport(string path) { _lastExportPath = path; Raise(nameof(HasLastExport)); }

    public ICommand RevealExportCommand => _revealExportCommand ??= new RelayCommand(() =>
    {
        if (_lastExportPath is null) return;
        try
        {
            // Full path to explorer.exe (avoids PATH resolution). Quote the path so spaces work.
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var psi = new System.Diagnostics.ProcessStartInfo(explorer) { UseShellExecute = true };
            // explorer's /select mishandles commas in the path, so fall back to opening the folder there.
            if (File.Exists(_lastExportPath) && !_lastExportPath.Contains(','))
                psi.Arguments = $"/select,\"{_lastExportPath}\"";
            else
            {
                var folder = File.Exists(_lastExportPath) ? Path.GetDirectoryName(_lastExportPath) : _lastExportPath;
                if (string.IsNullOrEmpty(folder)) return;
                psi.Arguments = $"\"{folder}\"";
            }
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { Status = $"Reveal failed: {ex.Message}"; }
    });
    private ICommand? _revealExportCommand;

    /// <summary>
    /// Copy a value to the clipboard and confirm in the status bar. The Windows clipboard is
    /// frequently held for a few milliseconds by Explorer / RDP / clipboard managers, so retry
    /// a couple of times before reporting failure to avoid spurious "Copy failed" messages.
    /// </summary>
    private void CopyToClipboard(string? value, string label)
    {
        if (string.IsNullOrEmpty(value)) return;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(value, copy: true);   // OLE-backed, retries internally
                Status = $"Copied {label}: {value}";
                return;
            }
            catch (Exception ex)
            {
                if (attempt == 2) { Status = $"Copy failed: {ex.Message}"; return; }
                System.Threading.Thread.Sleep(30);
            }
        }
    }

    public ICommand LoadNamesCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand ExportCsvAllCommand { get; }
    public ICommand ExportDdsCommand { get; }
    public ICommand ExportImagesCommand { get; }
    public ICommand ExportImagesAllCommand { get; }

    /// <summary>
    /// Decode and export every detected image (PNG + DDS) from the selected package,
    /// or from every package. Files land in per-package folders named by asset hash.
    /// </summary>
    private async Task ExportImagesAsync(bool wholeGame)
    {
        if (!_oodleReady) { Status = "oo2core not loaded — cannot decode images."; return; }
        var dlg = new OpenFolderDialog { Title = wholeGame ? "Output folder for ALL game images (PNG+DDS)" : "Output folder for this package's images (PNG+DDS)" };
        if (dlg.ShowDialog() != true || dlg.FolderName is not { Length: > 0 } outRoot) return;

        var targets = wholeGame
            ? Packages.Where(p => p.HasIndex).ToArray()
            : SelectedPackage is { HasIndex: true } sp ? [sp] : [];
        if (targets.Length == 0) { Status = "No package to export."; return; }

        IsBusy = true; Progress = 0;
        long okPng = 0, okDds = 0, skipped = 0;
        try
        {
            await Task.Run(() =>
            {
                for (int ti = 0; ti < targets.Length; ti++)
                {
                    var row = targets[ti];
                    var dir = Path.Combine(outRoot, row.Name);
                    Directory.CreateDirectory(dir);
                    KapiPackage pkg;
                    try { pkg = KapiPackage.Open(row.Set); } catch { continue; }
                    using (pkg)
                    {
                        int idx = 0, count = pkg.Entries.Count;
                        foreach (var e in pkg.Entries)
                        {
                            idx++;
                            if (!DimensionGuesser.CouldBeImage((long)e.DecompressedSize)) continue;
                            try
                            {
                                var blob = pkg.Extract(e);
                                var best = TextureDecoder.BestGuess(blob, minScore: 0.4);   // only confident images
                                if (best is not { } b) { skipped++; continue; }
                                var stem = Path.Combine(dir, $"{e.Key:x16}_{b.Guess.Width}x{b.Guess.Height}");
                                // DDS (lossless, original blocks)
                                try { File.WriteAllBytes(stem + ".dds", DdsWriter.FromBlob(blob, b.Guess)); okDds++; } catch { }
                                // PNG (decoded RGBA)
                                try
                                {
                                    var img = b.Image;
                                    for (int k = 3; k < img.Bgra.Length; k += 4) img.Bgra[k] = 255;
                                    var src = System.Windows.Media.Imaging.BitmapSource.Create(img.Width, img.Height, 96, 96,
                                        PixelFormats.Bgra32, null, img.Bgra, img.Width * 4);
                                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
                                    using var fs = File.Create(stem + ".png");
                                    enc.Save(fs);
                                    okPng++;
                                }
                                catch { }
                            }
                            catch { skipped++; }

                            if ((idx & 0x3F) == 0)
                            {
                                double p = 100.0 * (ti + (double)idx / count) / targets.Length;
                                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                                {
                                    Progress = p;
                                    Status = $"Exporting images… {row.Name} {idx:N0}/{count:N0}  [{okPng:N0} PNG, {okDds:N0} DDS]";
                                });
                            }
                        }
                    }
                }
            });
            Progress = 100;
            Status = $"Image export complete: {okPng:N0} PNG + {okDds:N0} DDS → {outRoot}  ({skipped:N0} skipped)";
        }
        catch (Exception ex) { Status = $"Image export failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private void ExportDds()
    {
        if (_selectedBytes is null || SelectedCandidate is not { } g || SelectedAsset is null) return;
        var dlg = new SaveFileDialog
        {
            FileName = $"{SelectedAsset.Entry.Key:x16}_{g.Width}x{g.Height}.dds",
            Filter = "DDS image (*.dds)|*.dds",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var dds = DdsWriter.FromBlob(_selectedBytes, g);
            File.WriteAllBytes(dlg.FileName, dds);
            SetLastExport(dlg.FileName);
            Status = $"Saved DDS ({g.Format} {g.Width}×{g.Height}) → {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { Status = $"DDS export failed: {ex.Message}"; }
    }

    /// <summary>Export a single CSV covering EVERY package — a full game streamables manifest.</summary>
    private async Task ExportCsvAllAsync()
    {
        if (Packages.Count == 0) return;
        var dlg = new SaveFileDialog
        {
            FileName = "cod26_full_catalog.csv",
            Filter = "CSV (*.csv)|*.csv",
        };
        if (dlg.ShowDialog() != true) return;

        IsBusy = true; Progress = 0;
        var indexed = Packages.Where(p => p.HasIndex).ToArray();
        long rows = 0;
        try
        {
            await Task.Run(() =>
            {
                using var w = new StreamWriter(dlg.FileName);
                w.WriteLine("name_or_hash,hash,type,ui_role,width,height,decompressed_bytes,compressed_bytes,offset,package");
                for (int pi = 0; pi < indexed.Length; pi++)
                {
                    var row = indexed[pi];
                    try
                    {
                        using var pkg = KapiPackage.Open(row.Set);
                        foreach (var e in pkg.Entries)
                        {
                            bool img = ThumbnailProvider.IsImageShaped(e.DecompressedSize);
                            var gg = img ? DimensionGuesser.Best((long)e.DecompressedSize) : null;
                            int gw = gg?.Width ?? 0, gh = gg?.Height ?? 0;
                            string name = _namesLoaded > 0 && _names.TryGet(e.Key, out var an) ? an.Name : $"0x{e.Key:x16}";
                            string kind = img ? "Image" : "Data";
                            w.WriteLine($"{Csv(name)},0x{e.Key:x16},{kind},,{gw},{gh}," +
                                        $"{e.DecompressedSize},{e.CompressedSize},0x{e.Offset:x},{Csv(row.Name)}");
                            rows++;
                        }
                    }
                    catch { /* skip unreadable package */ }

                    double p = 100.0 * (pi + 1) / indexed.Length;
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        Progress = p;
                        Status = $"Writing full catalog… package {pi + 1}/{indexed.Length} ({row.Name}) — {rows:N0} rows";
                    });
                }
            });
            Progress = 100;
            Status = $"Full catalog exported: {rows:N0} rows across {indexed.Length} packages → {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { Status = $"Full CSV export failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>Export the current package's asset catalog (streamables manifest) to CSV.</summary>
    private void ExportCsv()
    {
        if (_allAssets.Count == 0) { Status = "Load a package first."; return; }
        var pkgName = SelectedPackage?.Name ?? "package";
        var dlg = new SaveFileDialog
        {
            FileName = $"{pkgName}_catalog.csv",
            Filter = "CSV (*.csv)|*.csv",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var w = new StreamWriter(dlg.FileName);
            w.WriteLine("name_or_hash,hash,type,shape,width,height,decompressed_bytes,compressed_bytes,offset,package");
            foreach (var r in _allAssets)
                w.WriteLine($"{Csv(r.DisplayName)},{r.Hash},{r.Kind},{Csv(r.Shape)}," +
                            $"{r.GuessW},{r.GuessH},{r.Decompressed},{r.Compressed},{r.Offset},{Csv(pkgName)}");
            Status = $"Exported {_allAssets.Count:N0}-row catalog → {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { Status = $"CSV export failed: {ex.Message}"; }
    }

    private static string Csv(string s)
    {
        // Neutralize spreadsheet formula injection from imported names.
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@') s = "'" + s;
        bool needQuote = s.IndexOfAny([',', '"', '\r', '\n']) >= 0;
        return needQuote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private readonly NameDatabase _names = new();
    private int _namesLoaded;

    private void LoadNames()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Load asset names (Cordycep/Saluki/community list: CSV, TXT, or JSON — hash,name)",
            Filter = "Name lists (*.csv;*.txt;*.json;*.wni)|*.csv;*.txt;*.json;*.wni|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            int added = _names.LoadFile(dlg.FileName);
            _namesLoaded = _names.Count;
            ApplyNames();
            int matched = _allAssets.Count(r => r.HasName);
            Status = $"Loaded {added:N0} names ({_namesLoaded:N0} total) — matched {matched:N0}/{_allAssets.Count:N0} assets in this package.";
        }
        catch (Exception ex) { Status = $"Name load failed: {ex.Message}"; }
    }

    private void ApplyNames()
    {
        if (_namesLoaded == 0) return;
        foreach (var r in _allAssets)
            if (_names.TryGet(r.Entry.Key, out var an)) r.ResolvedName = an.Name;
        AssetsView?.Refresh();
    }

    /// <summary>Load a names file by path (CLI/testing).</summary>
    public void LoadNamesFile(string path)
    {
        try { _names.LoadFile(path); _namesLoaded = _names.Count; ApplyNames(); } catch { }
    }

    public ICommand FindTextCommand { get; }
    public ICommand ClearTextCommand { get; }

    private string _findText = "";
    public string FindText { get => _findText; set => Set(ref _findText, value); }

    private HashSet<ulong>? _textMatches;   // when set, table shows only assets containing the text

    private async Task FindTextAsync()
    {
        if (_openPackage is null || FindText.Trim().Length == 0) return;
        if (!_oodleReady) { Status = "oo2core not loaded — cannot scan."; return; }

        var query = FindText.Trim();
        var pkg = _openPackage;
        var entries = _allAssets.Where(r => r.Category != "Off-disk").Select(r => r.Entry).ToArray();
        IsBusy = true; Progress = 0;
        var matches = new HashSet<ulong>();
        try
        {
            await Task.Run(() =>
            {
                int done = 0;
                Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    e =>
                    {
                        try { if (StringExtractor.Contains(pkg.Extract(e), query)) lock (matches) matches.Add(e.Key); }
                        catch { }
                        int d = Interlocked.Increment(ref done);
                        if ((d & 0x3FF) == 0)
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                Progress = 100.0 * d / entries.Length;
                                Status = $"Scanning for \"{query}\"… {d:N0}/{entries.Length:N0} ({matches.Count} hits)";
                            });
                    });
            });
            _textMatches = matches;
            AssetsView?.Refresh();
            Status = $"Found \"{query}\" in {matches.Count:N0} assets (filter active — Clear to reset).";
        }
        catch (Exception ex) { Status = $"Text scan failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private void ClearTextSearch()
    {
        _textMatches = null;
        AssetsView?.Refresh();
        Status = "Text filter cleared.";
    }

    /// <summary>Esc: clear search + filters back to the default view (all assets).</summary>
    public ICommand ResetViewCommand => _resetViewCommand ??= new RelayCommand(() =>
    {
        bool changed = false;
        if (_assetSearch.Length > 0) { _assetSearch = ""; Raise(nameof(AssetSearch)); changed = true; }
        if (_textMatches is not null) { _textMatches = null; changed = true; }
        if (_categoryFilter != "All assets") { _categoryFilter = "All assets"; Raise(nameof(CategoryFilter)); Raise(nameof(ShowingUpdatesOnly)); changed = true; }
        if (changed) { AssetsView?.Refresh(); Status = "View reset."; }
    });
    private ICommand? _resetViewCommand;

    public ICommand ImportCommand { get; }
    public ICommand ExportAssetCommand { get; }
    public ICommand ExportPackageCommand { get; }
    public ICommand ExportPngCommand { get; }
    public ICommand ExportGameCommand { get; }

    /// <summary>Auto-load the last used game directory + view state on startup, if still present.</summary>
    public async Task RestoreLastSession()
    {
        if (_settings.LastGameDir is not { Length: > 0 } d || !Directory.Exists(d)) return;
        await ImportDirectoryAsync(d);   // wait for packages before selecting one below
        if (_settings.ThumbSizeName is { Length: > 0 } ts && ThumbSizes.Contains(ts)) ThumbSizeName = ts;
        // Only re-open the last package if it still EXISTS — otherwise SelectPackageByName's
        // fallback to the first package would overwrite the remembered choice via the setter.
        if (_settings.LastPackage is { Length: > 0 } lp
            && Packages.Any(p => string.Equals(p.Name, lp, StringComparison.OrdinalIgnoreCase)))
            SelectPackageByName(lp);
        // GridMode's setter runs StartPrefetch immediately; assets aren't loaded yet, so it's a
        // no-op here — LoadPackageAsync starts the prefetch once its rows exist.
        if (_settings.GridMode) GridMode = true;
    }

    private async Task ExportWholeGameAsync()
    {
        if (Packages.Count == 0) return;
        if (!_oodleReady) { Status = "oo2core not loaded — cannot extract."; return; }

        var dlg = new OpenFolderDialog { Title = "Choose an output folder for the FULL game raw export" };
        if (dlg.ShowDialog() != true || dlg.FolderName is not { Length: > 0 } outRoot) return;

        IsBusy = true; Progress = 0;
        var indexed = Packages.Where(p => p.HasIndex).ToArray();
        long grandOk = 0, grandFail = 0;
        try
        {
            await Task.Run(() =>
            {
                for (int pi = 0; pi < indexed.Length; pi++)
                {
                    var row = indexed[pi];
                    var dir = Path.Combine(outRoot, row.Name);
                    Directory.CreateDirectory(dir);
                    try
                    {
                        using var pkg = KapiPackage.Open(row.Set);
                        foreach (var e in pkg.Entries)
                        {
                            try
                            {
                                var bytes = pkg.Extract(e);
                                var ext = AssetSniffer.Detect(bytes).Extension;
                                File.WriteAllBytes(Path.Combine(dir, $"{e.Key:x16}.{ext}"), bytes);
                                grandOk++;
                            }
                            catch { grandFail++; }
                        }
                    }
                    catch { /* skip unreadable package */ }

                    double p = 100.0 * (pi + 1) / indexed.Length;
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        Progress = p;
                        Status = $"Exporting game… package {pi + 1}/{indexed.Length} ({row.Name})  " +
                                 $"[{grandOk:N0} ok, {grandFail:N0} skipped]";
                    });
                }
            });
            Progress = 100;
            Status = $"Full export complete: {grandOk:N0} assets across {indexed.Length} packages → {outRoot}";
        }
        catch (Exception ex) { Status = $"Full export failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private void ExportPng()
    {
        if (Preview is null || SelectedAsset is null) return;
        var g = _selectedCandidate;
        var dlg = new SaveFileDialog
        {
            FileName = $"{SelectedAsset.Entry.Key:x16}_{g?.Width}x{g?.Height}.png",
            Filter = "PNG image (*.png)|*.png",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Preview));
            using (var fs = File.Create(dlg.FileName)) enc.Save(fs);
            SetLastExport(dlg.FileName);
            Status = $"Saved PNG → {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { Status = $"PNG export failed: {ex.Message}"; }
    }

    private string _status = "Ready. Import a game folder to begin.";
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _summary = "";
    public string Summary { get => _summary; set => Set(ref _summary, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) Raise(nameof(NotBusy)); } }
    public bool NotBusy => !_isBusy;

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    private string? _gameDir;
    private bool _oodleReady;

    private string _assetSearch = "";
    public string AssetSearch { get => _assetSearch; set { if (Set(ref _assetSearch, value)) AssetsView?.Refresh(); } }

    private ThumbnailProvider? _thumbs;

    private bool _gridMode;
    public bool GridMode
    {
        get => _gridMode;
        set
        {
            if (!Set(ref _gridMode, value)) return;
            Raise(nameof(TableMode));
            AssetsView?.Refresh();
            if (value) StartPrefetch(); else _thumbs?.StopPrefetch();
            _settings.GridMode = value; _settings.Save();
        }
    }
    public bool TableMode => !_gridMode;

    /// <summary>Background-decode all image-shaped thumbnails in the current view so scrolling is smooth.</summary>
    private string _preloadStatus = "";
    public string PreloadStatus { get => _preloadStatus; set { if (Set(ref _preloadStatus, value)) Raise(nameof(IsPreloading)); } }
    public bool IsPreloading => _preloadStatus.Length > 0;

    private void StartPrefetch()
    {
        if (_thumbs is null || _allAssets.Count == 0) return;
        var entries = _allAssets.Where(r => r.IsImageShaped).Select(r => r.Entry).ToList();
        var ui = System.Windows.Application.Current.Dispatcher;
        _thumbs.Prefetch(entries, (d, t) => ui.BeginInvoke(() =>
            PreloadStatus = d >= t ? "" : $"Preloading thumbnails… {d:N0} / {t:N0}"));
        PreloadStatus = $"Preloading thumbnails… 0 / {entries.Count:N0}";
    }

    // Thumbnail size for the grid view.
    public IReadOnlyList<string> ThumbSizes { get; } = ["Small", "Normal", "Large", "X-Large", "Huge"];

    /// <summary>Step the thumbnail size up (+1) or down (−1) — used by Ctrl+scroll in the grid.</summary>
    public void StepThumbSize(int delta)
    {
        int i = 0;
        for (int k = 0; k < ThumbSizes.Count; k++) if (ThumbSizes[k] == _thumbSizeName) { i = k; break; }
        int ni = Math.Clamp(i + delta, 0, ThumbSizes.Count - 1);
        if (ni != i) ThumbSizeName = ThumbSizes[ni];
    }

    private string _thumbSizeName = "Normal";
    public string ThumbSizeName
    {
        get => _thumbSizeName;
        set { if (Set(ref _thumbSizeName, value)) { Raise(nameof(ThumbSize)); Raise(nameof(ThumbCellSize)); _settings.ThumbSizeName = value; _settings.Save(); } }
    }
    public double ThumbSize => _thumbSizeName switch
    {
        "Small" => 80, "Large" => 176, "X-Large" => 240, "Huge" => 320, _ => 128,
    };
    public double ThumbCellSize => ThumbSize + 18;   // image + label

    public IReadOnlyList<string> CategoryFilters { get; } =
    [
        "All assets", "Images", "Square", "Wide", "Tall", "Data",
        "New (since update)", "Changed (since update)",
    ];

    // Per-package change sets from the last game update (package name → added/changed asset keys),
    // used to mark rows as NEW/CHG and to power the "New/Changed (since update)" filters.
    private Dictionary<string, (HashSet<ulong> added, HashSet<ulong> changed)> _changesByPackage = new();
    private CatalogDiff? _updateChanges;

    /// <summary>True when there are tracked changes from the last update (drives the chip/filters).</summary>
    public bool HasUpdateChanges => _updateChanges is { AnyChange: true };

    /// <summary>Compact chip label, e.g. "✦ 8 updated". Full detail is in the tooltip (UpdateSummary).</summary>
    public string UpdateChipText { get; private set; } = "";
    private string _updateSummary = "";
    public string UpdateSummary { get => _updateSummary; private set { if (Set(ref _updateSummary, value)) { Raise(nameof(HasUpdateChanges)); Raise(nameof(UpdateChipText)); } } }

    /// <summary>Whether the list is currently filtered to update changes (drives the chip's toggled state).</summary>
    public bool ShowingUpdatesOnly => _categoryFilter is "New (since update)" or "Changed (since update)";

    /// <summary>Load the last-update diff into the per-package change lookup (called on import).</summary>
    private void LoadUpdateChanges()
    {
        _updateChanges = UpdateTracker.LastUpdateChanges();
        var map = new Dictionary<string, (HashSet<ulong>, HashSet<ulong>)>(StringComparer.OrdinalIgnoreCase);
        if (_updateChanges is { } d)
            foreach (var p in d.Packages)
                map[p.Name] = ([.. p.AddedKeys], [.. p.ChangedKeys]);
        _changesByPackage = map;
        if (_updateChanges is { AnyChange: true } dd)
        {
            UpdateChipText = $"✦ {dd.TotalAdded + dd.TotalChanged:N0} updated";
            UpdateSummary = $"Last game update: {dd.TotalAdded:N0} new · {dd.TotalChanged:N0} changed · {dd.TotalRemoved:N0} removed.\nClick to show only these; click again to clear.";
        }
        else { UpdateChipText = ""; UpdateSummary = ""; }
    }

    /// <summary>Toggle the list between "new since update" and "all assets" (the chip's on/off click).</summary>
    public void ToggleUpdatesFilter() => CategoryFilter = ShowingUpdatesOnly ? "All assets" : "New (since update)";

    private string _categoryFilter = "All assets";
    public string CategoryFilter
    {
        get => _categoryFilter;
        set { if (Set(ref _categoryFilter, value)) { AssetsView?.Refresh(); Raise(nameof(ShowingUpdatesOnly)); } }
    }

    public IReadOnlyList<string> SortOptions { get; } =
        ["Offset (file order)", "Size ↓", "Size ↑", "Type", "Hash", "Images first", "CDN stubs first"];

    private string _sortBy = "Offset (file order)";
    public string SortBy { get => _sortBy; set { if (Set(ref _sortBy, value)) ApplySort(); } }

    private void ApplySort()
    {
        if (AssetsView is not ListCollectionView v) { AssetsView?.Refresh(); return; }
        using (v.DeferRefresh())
        {
            v.CustomSort = _sortBy switch
            {
                "Size ↓" => Comparer<object>.Create((a, b) => ((AssetRow)b).Decompressed.CompareTo(((AssetRow)a).Decompressed)),
                "Size ↑" => Comparer<object>.Create((a, b) => ((AssetRow)a).Decompressed.CompareTo(((AssetRow)b).Decompressed)),
                "Type"   => Comparer<object>.Create((a, b) => string.CompareOrdinal(((AssetRow)a).Kind, ((AssetRow)b).Kind)),
                "Hash"   => Comparer<object>.Create((a, b) => ((AssetRow)a).Entry.Key.CompareTo(((AssetRow)b).Entry.Key)),
                // Image-shaped assets to the top, largest first within that group.
                "Images first" => Comparer<object>.Create((a, b) =>
                {
                    var (x, y) = ((AssetRow)a, (AssetRow)b);
                    int c = y.IsImageShaped.CompareTo(x.IsImageShaped);
                    return c != 0 ? c : y.Decompressed.CompareTo(x.Decompressed);
                }),
                // CDN-only stubs to the top (surface what a package streams vs installs).
                "CDN stubs first" => Comparer<object>.Create((a, b) =>
                {
                    var (x, y) = ((AssetRow)a, (AssetRow)b);
                    int c = y.IsOffDisk.CompareTo(x.IsOffDisk);
                    return c != 0 ? c : x.Entry.Offset.CompareTo(y.Entry.Offset);
                }),
                _ => null, // offset / natural file order
            };
        }
    }

    /// <summary>True once a package's assets have finished loading (for screenshot/testing sync).</summary>
    public bool AssetsLoaded => _allAssets.Count > 0;

    /// <summary>All loaded assets sorted by ascending global offset (for address/jump tabs).</summary>
    /// <summary>
    /// Ready the main view for an address jump: clear search/filters so every asset is visible,
    /// order by offset, and switch to the table (the natural home for offset navigation) so the
    /// target row can be selected and scrolled to. (Replaces the old AssetsByOffset tab approach.)
    /// </summary>
    public void PrepareForJump()
    {
        if (_assetSearch.Length > 0) { _assetSearch = ""; Raise(nameof(AssetSearch)); }
        _textMatches = null;
        if (_categoryFilter != "All assets")
        { _categoryFilter = "All assets"; Raise(nameof(CategoryFilter)); Raise(nameof(ShowingUpdatesOnly)); }
        AssetsView?.Refresh();
        SortBy = "Offset (file order)";
        GridMode = false;   // table view
    }

    /// <summary>The asset at/after the given global offset (nearest by address).</summary>
    public AssetRow? NearestByAddress(ulong addr)
    {
        AssetRow? best = null; ulong bestOff = ulong.MaxValue;
        AssetRow? last = null; ulong lastOff = 0;
        foreach (var r in _allAssets)
        {
            var o = r.Entry.Offset;
            if (o >= addr && o < bestOff) { bestOff = o; best = r; }
            if (o >= lastOff) { lastOff = o; last = r; }
        }
        return best ?? last;
    }

    /// <summary>Assets whose offset falls within [lo, hi], sorted by offset.</summary>
    public IReadOnlyList<AssetRow> AssetsInRange(ulong lo, ulong hi) =>
        _allAssets.Where(r => r.Entry.Offset >= lo && r.Entry.Offset <= hi)
                  .OrderBy(r => r.Entry.Offset).ToList();

    /// <summary>Parse an address as hex (0x… or bare hex) or decimal.</summary>
    public static bool TryParseAddress(string s, out ulong addr)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out addr);
        if (ulong.TryParse(s, out addr)) return true;
        return ulong.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out addr);
    }

    /// <summary>Select a specific asset by hash key (for demo/screenshot/testing).</summary>
    public void SelectAssetByHash(ulong key)
    {
        var row = _allAssets.FirstOrDefault(r => r.Entry.Key == key);
        if (row is not null) SelectedAsset = row;
    }

    /// <summary>Select the first asset whose size best fits a square texture (for demo/screenshot).</summary>
    public void SelectFirstImageAsset()
    {
        var row = _allAssets.FirstOrDefault(r =>
            DimensionGuesser.Best(r.Decompressed) is { } g && g.Width == g.Height && r.Decompressed >= 65536);
        if (row is not null) SelectedAsset = row;
    }

    private PackageRow? _selectedPackage;
    public PackageRow? SelectedPackage
    {
        get => _selectedPackage;
        set
        {
            if (!Set(ref _selectedPackage, value)) return;
            if (value is not null) { _settings.LastPackage = value.Name; _settings.Save(); }
            _ = LoadPackageAsync(value);
        }
    }

    private AssetRow? _selectedAsset;
    public AssetRow? SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            if (!Set(ref _selectedAsset, value)) return;
            // Selecting an asset supersedes any fastfile shown in the inspector (one at a time).
            if (value is not null && _hasFastfileInfo)
            { _selectedContentFile = null; Raise(nameof(SelectedContentFile)); FastfileInfo = ""; HasFastfileInfo = false; }
            Raise(nameof(HasAsset)); Raise(nameof(NothingSelected)); _ = PreviewAsync(value);
        }
    }
    public bool HasAsset => _selectedAsset is not null;

    /// <summary>Nothing (asset or fastfile) is selected → show inspector guidance.</summary>
    public bool NothingSelected => _selectedAsset is null && !_hasFastfileInfo;

    /// <summary>No package's assets are loaded → show center-panel empty-state guidance.</summary>
    public bool NoAssetsLoaded => _allAssets.Count == 0 && !_showContent;

    private string _detectedType = "";
    public string DetectedType { get => _detectedType; set => Set(ref _detectedType, value); }

    private string _hexPreview = "";
    public string HexPreview { get => _hexPreview; set => Set(ref _hexPreview, value); }

    private string _stringsPreview = "";
    public string StringsPreview { get => _stringsPreview; set => Set(ref _stringsPreview, value); }

    private bool _hasStrings;
    public bool HasStrings { get => _hasStrings; set => Set(ref _hasStrings, value); }

    /// <summary>True only if the blob is mostly printable ASCII (a genuine text asset).</summary>
    private static bool LooksTextual(byte[] b)
    {
        int n = Math.Min(b.Length, 4096); if (n < 8) return false;
        int printable = 0;
        for (int i = 0; i < n; i++)
        {
            byte c = b[i];
            if (c is 9 or 10 or 13 || (c >= 32 && c < 127)) printable++;
        }
        return (double)printable / n > 0.90;
    }

    private SniffResult _lastSniff = SniffResult.Unknown;
    private byte[]? _selectedBytes;

    // Texture preview
    public ObservableCollection<TextureGuess> Candidates { get; } = [];
    public bool HasCandidates => Candidates.Count > 0;

    // Format-detection tool: every candidate interpretation rendered as a thumbnail,
    // so the user can double-click the correct one (which we log to find patterns).
    public ObservableCollection<CandidateThumb> CandidateThumbs { get; } = [];
    private bool _showAllFormats;
    public bool ShowAllFormats { get => _showAllFormats; set { if (Set(ref _showAllFormats, value)) _ = RebuildCandidateThumbsAsync(); } }

    private async Task RebuildCandidateThumbsAsync()
    {
        CandidateThumbs.Clear();
        if (!_showAllFormats || _selectedBytes is null) return;
        var blob = _selectedBytes;
        var list = await Task.Run(() =>
        {
            var outp = new List<CandidateThumb>();
            foreach (var g in DimensionGuesser.Guess(blob.Length, 40))
            {
                DecodedImage img;
                try { img = TextureDecoder.Decode(blob, g); } catch { continue; }
                double score = ImageLikelihood.Score(img);
                for (int i = 3; i < img.Bgra.Length; i += 4) img.Bgra[i] = 255; // opaque preview
                var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Bgra, img.Width * 4);
                if (bmp.CanFreeze) bmp.Freeze();
                outp.Add(new CandidateThumb(g, bmp, $"{g.Format} {g.Width}×{g.Height}", score));
            }
            return outp.OrderByDescending(c => c.Score).ToList();
        });
        if (!ReferenceEquals(blob, _selectedBytes)) return; // selection changed
        foreach (var c in list) CandidateThumbs.Add(c);
    }

    /// <summary>User double-clicked the interpretation they judge correct: log it + apply it.</summary>
    public void ChooseCandidate(CandidateThumb c)
    {
        if (SelectedAsset is null) return;
        // Apply as the preview.
        _selectedCandidate = c.Guess; Raise(nameof(SelectedCandidate)); RenderPreview();
        // Log for pattern analysis.
        var auto = TextureDecoder.BestGuess(_selectedBytes ?? [], 0.0)?.Guess;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MW4FFTool");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "format_choices.csv");
            bool newFile = !File.Exists(path);
            using var w = new StreamWriter(path, append: true);
            if (newFile) w.WriteLine("hash,blob_bytes,chosen_format,chosen_w,chosen_h,auto_format,auto_w,auto_h,agree");
            bool agree = auto is { } a && a.Format == c.Guess.Format && a.Width == c.Guess.Width && a.Height == c.Guess.Height;
            w.WriteLine($"{SelectedAsset.Hash},{_selectedBytes?.Length ?? 0}," +
                        $"{c.Guess.Format},{c.Guess.Width},{c.Guess.Height}," +
                        $"{auto?.Format},{auto?.Width},{auto?.Height},{agree}");
            Status = $"Logged: {c.Guess.Format} {c.Guess.Width}×{c.Guess.Height} for {SelectedAsset.Hash} " +
                     (agree ? "(matches auto)" : "(auto was WRONG — logged for pattern analysis)");
        }
        catch (Exception ex) { Status = $"Log failed: {ex.Message}"; }
    }

    private BitmapSource? _preview;
    public BitmapSource? Preview { get => _preview; set { if (Set(ref _preview, value)) Raise(nameof(HasPreview)); } }
    public bool HasPreview => _preview is not null;

    private string _previewError = "";
    public string PreviewError { get => _previewError; set => Set(ref _previewError, value); }

    private TextureGuess? _selectedCandidate;
    public TextureGuess? SelectedCandidate
    {
        get => _selectedCandidate;
        set { if (Set(ref _selectedCandidate, value)) RenderPreview(); }
    }

    private bool _isSelectedOffDisk;
    /// <summary>The selected asset is a CDN stub (offset beyond installed data) — not extractable.</summary>
    public bool IsSelectedOffDisk { get => _isSelectedOffDisk; set => Set(ref _isSelectedOffDisk, value); }

    private async Task PreviewAsync(AssetRow? row)
    {
        DetectedType = ""; HexPreview = ""; Preview = null; PreviewError = "";
        Candidates.Clear(); Raise(nameof(HasCandidates));
        _selectedCandidate = null; _selectedBytes = null;
        IsSelectedOffDisk = false;
        if (row is null || _openPackage is null) return;
        if (!_oodleReady) { DetectedType = "oo2core not loaded"; return; }

        var pkg = _openPackage; var entry = row.Entry;

        // CDN stub: the offset points past the installed .xsub data — the asset is streamed
        // on demand and is not on disk, so there is nothing to extract. Say so up front.
        if (!pkg.IsOnDisk(entry))
        {
            IsSelectedOffDisk = true;
            DetectedType = "CDN stub — streamed, not installed locally";
            return;
        }

        try
        {
            var bytes = await Task.Run(() => pkg.Extract(entry));
            if (!ReferenceEquals(row, _selectedAsset)) return; // selection moved on
            _selectedBytes = bytes;
            _lastSniff = AssetSniffer.Detect(bytes);
            var kind = AssetSniffer.ClassifyKind(bytes, bytes.Length);
            DetectedType = _lastSniff.Type != "binary"
                ? $"{_lastSniff.Type}  ·  .{_lastSniff.Extension}"
                : kind;   // e.g. "Image", "Sound (likely)", "Binary data"
            // Also refine the selected row's Type column now that we've inspected it.
            if (row.Category != "Off-disk") row.RefineKind(kind);
            HexPreview = AssetSniffer.HexPreview(bytes);

            // Only show STRINGS when the asset is genuinely textual. On this game the
            // readable names live in the encrypted .ff, so xsub blobs are binary — showing
            // "extracted" ASCII noise is misleading, so we suppress it.
            if (LooksTextual(bytes))
            {
                var strs = StringExtractor.Extract(bytes, minLen: 4, max: 400);
                StringsPreview = string.Join("\n", strs);
                HasStrings = strs.Count > 0;
            }
            else { StringsPreview = ""; HasStrings = false; }
            Raise(nameof(HasStrings));

            // Offer texture interpretations for image-shaped blobs.
            foreach (var g in DimensionGuesser.Guess(bytes.Length, 24)) Candidates.Add(g);
            Raise(nameof(HasCandidates));
            if (Candidates.Count > 0)
            {
                // Prefer the SAME interpretation the grid thumbnail chose (so tile and preview
                // match); fall back to full scoring if the thumbnail hasn't decoded this asset.
                TextureGuess pick;
                if (_thumbs is not null && _thumbs.TryGetChosenGuess(row.Entry.Key, out var chosen))
                    pick = chosen;
                else
                {
                    var best = await Task.Run(() => TextureDecoder.BestGuess(bytes));
                    if (!ReferenceEquals(row, _selectedAsset)) return;
                    pick = best?.Guess ?? Candidates[0];
                }
                _selectedCandidate = Candidates.FirstOrDefault(
                    c => c.Format == pick.Format && c.Width == pick.Width && c.Height == pick.Height, Candidates[0]);
                Raise(nameof(SelectedCandidate));
                RenderPreview();
                if (_showAllFormats) _ = RebuildCandidateThumbsAsync();
            }
        }
        catch (KapiExtractException kex)
        {
            DetectedType = kex.Message.Contains("outside data")
                ? "off-disk asset (CDN-streamed — not installed locally)"
                : "unresolved layout";
        }
        catch (Exception ex) { DetectedType = $"error: {ex.Message}"; }
    }

    private void RenderPreview()
    {
        Preview = null; PreviewError = "";
        if (_selectedBytes is null || _selectedCandidate is not { } g) return;
        try
        {
            var img = TextureDecoder.Decode(_selectedBytes, g);
            for (int i = 3; i < img.Bgra.Length; i += 4) img.Bgra[i] = 255; // opaque preview
            var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96,
                PixelFormats.Bgra32, null, img.Bgra, img.Width * 4);
            bmp.Freeze();
            Preview = bmp;
        }
        catch (Exception ex)
        {
            // Keep the dropdown visible; just report that this interpretation didn't decode.
            Preview = null;
            PreviewError = $"Can't decode as {g.Format} {g.Width}×{g.Height}: {ex.GetType().Name}";
        }
    }

    private bool Match(AssetRow r)
    {
        // Update filters: only assets new / changed since the last game patch.
        if (_categoryFilter == "New (since update)" && !r.IsNew) return false;
        if (_categoryFilter == "Changed (since update)" && !r.IsChanged) return false;

        // Category filter. Shape filters (Square/Wide/Tall) imply images. Grid always implies images.
        bool shapeFilter = _categoryFilter is "Square" or "Wide" or "Tall";
        bool wantImages = _gridMode || _categoryFilter == "Images" || shapeFilter;
        if (wantImages && r.Category != "Image") return false;
        if (shapeFilter && r.Shape != _categoryFilter) return false;
        if (!_gridMode && _categoryFilter == "Data" && r.Category != "Data") return false;
        // In the grid, drop tiles that decoded and scored as non-images.
        if (_gridMode && r.IsImage == false) return false;
        // Global text-search filter (assets whose content contains the query).
        if (_textMatches is not null && !_textMatches.Contains(r.Entry.Key)) return false;
        if (_assetSearch.Length == 0) return true;
        return r.Hash.Contains(_assetSearch, StringComparison.OrdinalIgnoreCase)
            || r.DisplayName.Contains(_assetSearch, StringComparison.OrdinalIgnoreCase);
    }

    // Throttled re-filter: as thumbnails score, drop confirmed non-images from the grid
    // without refreshing on every single decode.
    private System.Windows.Threading.DispatcherTimer? _refreshTimer;
    private bool _refreshDirty;
    private void ScheduleGridRefresh()
    {
        _refreshDirty = true;
        if (_refreshTimer is null)
        {
            _refreshTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(700) };
            _refreshTimer.Tick += (_, _) =>
            {
                if (_refreshDirty && _gridMode) { _refreshDirty = false; AssetsView?.Refresh(); }
                else _refreshTimer!.Stop();
            };
        }
        if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
    }

    private void Import()
    {
        var dlg = new OpenFolderDialog { Title = "Select the game directory (contains .xpak / .xsub)" };
        if (dlg.ShowDialog() != true) return;
        ImportDirectory(dlg.FolderName);
    }

    /// <summary>Index a directory (fire-and-forget wrapper for the Import button, drag-drop, CLI).</summary>
    public void ImportDirectory(string dir) => _ = ImportDirectoryAsync(dir);

    /// <summary>
    /// Index a directory. The heavy discovery (opening ~40 package headers, sizing files, scanning
    /// fastfiles) runs OFF the UI thread so dropping/opening a folder never freezes the window; the
    /// collection mutations are marshalled back. Guarded against re-entrancy via <see cref="IsBusy"/>.
    /// </summary>
    public async Task ImportDirectoryAsync(string dir)
    {
        if (!Directory.Exists(dir)) { Status = $"Folder not found: {dir}"; return; }
        if (IsBusy) { Status = "Busy — finish the current operation first."; return; }
        Status = "Importing…"; IsBusy = true;
        try
        {
            _gameDir = dir;
            _settings.LastGameDir = dir; _settings.Save();
            _oodleReady = TryLoadOodle(dir);

            var built = await Task.Run(() =>
            {
                var sets = KapiReader.DiscoverPackages(dir);
                long grand = 0; int xsubTotal = 0;
                var rows = new List<PackageRow>(sets.Count);
                foreach (var s in sets.OrderByDescending(TotalBytes))
                {
                    long bytes = TotalBytes(s);
                    grand += bytes; xsubTotal += s.XsubPaths.Count;
                    rows.Add(new PackageRow(s, bytes));
                }
                List<ContentEntry> content;
                try { content = FastfileCatalog.Scan(dir).ToList(); }
                catch { content = []; }   // catalog is best-effort
                var groups = content
                    .GroupBy(e => (e.Content, e.Category))
                    .Select(g => new ContentGroupRow(g.Key.Content, g.Key.Category, g.Count(),
                        g.Sum(x => x.Size), g.Select(x => x.Detail).FirstOrDefault(d => d.Length > 0) ?? ""))
                    .OrderByDescending(g => g.Files).ToList();
                return (rows, content, groups, sets.Count, grand, xsubTotal);
            });

            Packages.Clear();
            _allAssets = []; _assetsSource.Source = _allAssets; Raise(nameof(NoAssetsLoaded)); // reset center panel
            foreach (var r in built.rows) Packages.Add(r);

            Content.Clear(); ContentFiles.Clear();
            _allContent = built.content;
            foreach (var g in built.groups) Content.Add(g);

            Summary = $"{built.Item4} package sets · {built.xsubTotal} xsub · {Format.Bytes(built.grand)} · " +
                      (_oodleReady ? "oo2core ✓" : "oo2core ✗ (extraction disabled)");
            Status = $"Indexed {built.Item4} packages · {Content.Count} content groups. Select a package to list assets.";
            AddRecentFolder(dir);

            // Tracked changes from the last update (filters/badges), then the silent update check.
            LoadUpdateChanges();
            CheckForGameUpdate(dir);
            // Background-count each package's assets + CDN stubs for the sidebar.
            ScanPackageCounts([.. Packages]);
        }
        catch (Exception ex) { Status = $"Import failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Silently detect a game update (cheap install fingerprint) and, when detected, capture +
    /// diff + archive a catalog snapshot on a background thread, surfacing a one-line summary.
    /// Runs on every import; does nothing visible when the install is unchanged.
    /// </summary>
    private void CheckForGameUpdate(string dir)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var res = UpdateTracker.CheckAndRecord(dir);
                string? msg = res.Kind switch
                {
                    UpdateTracker.Kind.FirstRun =>
                        $"Baseline recorded ({res.AssetCount:N0} assets). Future game updates are now tracked automatically.",
                    UpdateTracker.Kind.Updated when res.Diff is { AnyChange: true } d =>
                        $"Game update detected: +{d.TotalAdded:N0} added · ~{d.TotalChanged:N0} changed · −{d.TotalRemoved:N0} removed. Report: {res.DiffReportPath}",
                    UpdateTracker.Kind.Updated =>
                        "Game files changed (no asset-level differences). Snapshot updated.",
                    _ => null,   // Unchanged → stay silent
                };
                // A fresh update was just detected → reload change sets and re-mark the open
                // package so NEW/CHG badges and the New/Changed filters reflect it immediately.
                if (res.Kind == UpdateTracker.Kind.Updated && res.Diff is { AnyChange: true })
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        LoadUpdateChanges();
                        RemarkOpenPackage();
                        AssetsView?.Refresh();
                    });

                if (msg is not null)
                    System.Windows.Application.Current.Dispatcher.Invoke(() => Status = msg);
            }
            catch { /* update tracking is best-effort, never blocks import */ }
        });
    }

    /// <summary>Re-apply NEW/CHG flags to the currently loaded rows from the change lookup.</summary>
    private void RemarkOpenPackage()
    {
        if (SelectedPackage is not { } row || _allAssets.Count == 0) return;
        _changesByPackage.TryGetValue(row.Name, out var changes);
        foreach (var r in _allAssets)
        {
            r.IsNew = changes.added?.Contains(r.Entry.Key) ?? false;
            r.IsChanged = changes.changed?.Contains(r.Entry.Key) ?? false;
            r.NotifyChangeTag();
        }
    }

    private volatile int _countScanGeneration;

    /// <summary>
    /// Background-scan each package's .xpak index to count total assets and CDN-only stubs
    /// (offset beyond the installed data), updating each PackageRow on the UI thread as it
    /// completes. Cheap: reads only the small index files, no decompression.
    /// </summary>
    private void ScanPackageCounts(IReadOnlyList<PackageRow> rows)
    {
        int gen = ++_countScanGeneration;
        var ui = System.Windows.Application.Current.Dispatcher;
        _ = Task.Run(() =>
        {
            foreach (var row in rows)
            {
                if (gen != _countScanGeneration) return;   // superseded by a new import
                if (!row.HasIndex) continue;
                int total = 0, offDisk = 0, images = 0;
                try
                {
                    using var pkg = KapiPackage.Open(row.Set);
                    total = pkg.Entries.Count;
                    foreach (var e in pkg.Entries)
                    {
                        if (!pkg.IsOnDisk(e)) offDisk++;
                        else if (ThumbnailProvider.IsImageShaped(e.DecompressedSize)) images++;
                    }
                }
                catch { continue; }   // locked/new-format package → leave counts unknown
                if (gen != _countScanGeneration) return;
                ui.BeginInvoke(() => row.SetCounts(total, offDisk, images));
            }
        });
    }

    /// <summary>Select a package by name stem (used by the --select CLI arg / auto-open).</summary>
    public void SelectPackageByName(string name)
    {
        var row = Packages.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                  ?? Packages.FirstOrDefault(p => p.HasIndex);
        if (row is not null) SelectedPackage = row;
    }

    private volatile int _loadGeneration;

    private async Task LoadPackageAsync(PackageRow? row)
    {
        int gen = ++_loadGeneration;   // invalidate any in-flight older load
        _allAssets = [];
        _assetsSource.Source = _allAssets;
        SelectedAsset = null;
        SelectedContentFile = null;   // clear any fastfile shown in the inspector on package switch
        _openPackage?.Dispose(); _openPackage = null;
        AssetRow.SetProvider(null);
        if (row is null || !row.HasIndex) { Status = "Package has no index."; Raise(nameof(NoAssetsLoaded)); return; }

        try
        {
            Status = $"Loading {row.Name}…";
            var pkg = await Task.Run(() => KapiPackage.Open(row.Set));
            if (gen != _loadGeneration) { pkg.Dispose(); return; }  // superseded — discard
            _openPackage = pkg;
            _thumbs = null;
            if (_oodleReady)
            {
                _thumbs = new ThumbnailProvider(pkg, System.Windows.Application.Current.Dispatcher);
                AssetRow.SetProvider(_thumbs, ScheduleGridRefresh);
            }

            // Build the whole list off-thread (classify each: Image / Data / Off-disk), bind once.
            _changesByPackage.TryGetValue(row.Name, out var changes);
            var rows = await Task.Run(() => pkg.Entries.Select(e =>
            {
                var r = new AssetRow(e);
                r.Category = !pkg.IsOnDisk(e) ? "Off-disk" : r.IsImageShaped ? "Image" : "Data";
                r.IsNew = changes.added?.Contains(e.Key) ?? false;
                r.IsChanged = changes.changed?.Contains(e.Key) ?? false;
                return r;
            }).ToList());
            if (gen != _loadGeneration) return;   // superseded during row build
            _allAssets = rows;
            _assetsSource.Source = _allAssets;   // single reset → fast even at 45k rows
            ApplyNames();
            ApplySort();
            Raise(nameof(AssetsView));
            Raise(nameof(NoAssetsLoaded));

            // Fill this package's sidebar counts instantly from the classification we just did,
            // instead of waiting for the background scan to reach it.
            int offDisk = rows.Count(r => r.IsOffDisk);
            int images = rows.Count(r => r.Category == "Image");
            row.SetCounts(rows.Count, offDisk, images);

            Summary = $"{row.Name}: {pkg.Entries.Count:N0} assets · {pkg.Entries.Count - offDisk:N0} extractable";
            Status = $"Loaded {pkg.Entries.Count:N0} assets from {row.Name}.";

            // If the grid is already active, begin preloading now that assets exist.
            if (_gridMode) StartPrefetch();
        }
        catch (Exception ex) { Status = $"Load failed: {ex.Message}"; Raise(nameof(NoAssetsLoaded)); }
    }

    private async Task ExportSelectedAsync()
    {
        if (_openPackage is null || SelectedAsset is null) return;
        if (!_oodleReady) { Status = "oo2core not loaded — cannot extract."; return; }

        var entry = SelectedAsset.Entry;
        var pkg = _openPackage;                       // capture — don't race a package switch
        // CDN stubs aren't installed locally — extracting would just throw. Say so plainly.
        if (!pkg.IsOnDisk(entry)) { Status = "CDN stub — not installed locally, nothing to extract."; return; }

        try
        {
            var bytes = await Task.Run(() => pkg.Extract(entry));
            // Sniff the ACTUAL extracted bytes for the suggested extension, rather than relying
            // on the preview pipeline having run (right-click export can fire before it does).
            var ext = AssetSniffer.Detect(bytes).Extension is { Length: > 0 } e ? e : "bin";
            var dlg = new SaveFileDialog
            {
                FileName = $"{entry.Key:x16}.{ext}",
                Filter = $"Detected ({ext})|*.{ext}|Raw asset (*.bin)|*.bin|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            await File.WriteAllBytesAsync(dlg.FileName, bytes);
            SetLastExport(dlg.FileName);
            Status = $"Exported {Format.Bytes(bytes.Length)} → {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex) { Status = $"Export failed: {ex.Message}"; }
    }

    /// <summary>Export several selected assets (raw) to a chosen folder, skipping CDN stubs.</summary>
    public async Task ExportSelectedAssetsAsync(IReadOnlyList<AssetRow> rows)
    {
        if (_openPackage is null || rows.Count == 0) return;
        if (!_oodleReady) { Status = "oo2core not loaded — cannot extract."; return; }

        var dlg = new OpenFolderDialog { Title = $"Export {rows.Count:N0} selected asset(s) to…" };
        if (dlg.ShowDialog() != true || dlg.FolderName is not { Length: > 0 } outDir) return;

        var pkg = _openPackage;
        var entries = rows.Select(r => r.Entry).ToArray();
        IsBusy = true; Progress = 0;
        int ok = 0, skip = 0;
        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    var e = entries[i];
                    if (!pkg.IsOnDisk(e)) { skip++; }           // CDN stub — not installed
                    else
                        try
                        {
                            var bytes = pkg.Extract(e);
                            var ext = AssetSniffer.Detect(bytes).Extension is { Length: > 0 } x ? x : "bin";
                            File.WriteAllBytes(Path.Combine(outDir, $"{e.Key:x16}.{ext}"), bytes);
                            ok++;
                        }
                        catch { skip++; }
                    if ((i & 0x1F) == 0)
                    {
                        double p = 100.0 * (i + 1) / entries.Length;
                        System.Windows.Application.Current.Dispatcher.Invoke(() => Progress = p);
                    }
                }
            });
            SetLastExport(outDir);
            Status = $"Exported {ok:N0} of {rows.Count:N0} selected → {outDir}" + (skip > 0 ? $"  ({skip:N0} CDN/failed skipped)" : "");
        }
        catch (Exception ex) { Status = $"Export failed: {ex.Message}"; }
        finally { IsBusy = false; Progress = 0; }
    }

    private async Task ExportPackageAsync()
    {
        if (_openPackage is null) return;
        if (!_oodleReady) { Status = "oo2core not loaded — cannot extract."; return; }

        var dlg = new OpenFolderDialog { Title = "Choose an output folder for raw asset export" };
        if (dlg.ShowDialog() != true || dlg.FolderName is not { Length: > 0 } outDir) return;

        IsBusy = true; Progress = 0;
        var pkg = _openPackage;
        var entries = pkg.Entries.ToArray();
        int ok = 0, fail = 0;

        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < entries.Length; i++)
                {
                    try
                    {
                        var bytes = pkg.Extract(entries[i]);
                        var ext = AssetSniffer.Detect(bytes).Extension;
                        File.WriteAllBytes(Path.Combine(outDir, $"{entries[i].Key:x16}.{ext}"), bytes);
                        ok++;
                    }
                    catch (KapiExtractException) { fail++; }
                    catch (Exception) { fail++; }

                    if ((i & 0x3F) == 0)
                    {
                        double p = 100.0 * (i + 1) / entries.Length;
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            Progress = p;
                            Status = $"Exporting… {i + 1:N0}/{entries.Length:N0}  ({ok:N0} ok, {fail:N0} skipped)";
                        });
                    }
                }
            });
            Progress = 100;
            Status = $"Export complete: {ok:N0} assets → {outDir}  ({fail:N0} skipped)";
        }
        catch (Exception ex) { Status = $"Export failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private bool TryLoadOodle(string dir)
    {
        foreach (var cand in new[] { dir, Directory.GetParent(dir)?.FullName })
        {
            if (cand is null) continue;
            var p = Path.Combine(cand, "oo2core_8_win64.dll");
            if (File.Exists(p)) { try { Native.Oodle.UseLibrary(p); return true; } catch { } }
        }
        return false;
    }

    private static long TotalBytes(PackageSet s)
    {
        long t = 0;
        if (s.XpakPath is { } p && File.Exists(p)) t += new FileInfo(p).Length;
        foreach (var x in s.XsubPaths) if (File.Exists(x)) t += new FileInfo(x).Length;
        return t;
    }
}

/// <summary>One rendered candidate interpretation for the format-detection tool.</summary>
public sealed record CandidateThumb(TextureGuess Guess, System.Windows.Media.Imaging.BitmapSource Image, string Label, double Score)
{
    public string ScoreText => $"score {Score:0.00}";
}

/// <summary>A grouped content entry (a map/reward/mode) for the Content view.</summary>
public sealed class ContentGroupRow(string content, string category, int files, long bytes, string detail)
{
    public string Content { get; } = content;
    public string Category { get; } = category;
    public int Files { get; } = files;
    public string Size { get; } = Format.Bytes(bytes);
    public string Detail { get; } = detail;
}

internal static class Format
{
    public static string Bytes(long n)
    {
        double d = n; string[] u = ["B", "KB", "MB", "GB", "TB"]; int i = 0;
        while (d >= 1024 && i < u.Length - 1) { d /= 1024; i++; }
        return $"{d:0.#} {u[i]}";
    }
}
