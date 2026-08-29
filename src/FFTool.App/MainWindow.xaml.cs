using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace FFTool.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private ulong? _shotAssetHash;
    private bool _gridWired;   // ensures the grid/table handlers are attached only once

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;

        // Optional CLI: --import <dir> [--select <packageName>] [--screenshot <path>]
        var args = Environment.GetCommandLineArgs();
        string? shotPath = null, importDir = null, selectName = null;
        bool cliImport = false;
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--import") { importDir = args[i + 1]; cliImport = true; }
            else if (args[i] == "--select") selectName = args[i + 1];
            else if (args[i] == "--screenshot") shotPath = args[i + 1];
            else if (args[i] == "--asset") _shotAssetHash = Convert.ToUInt64(args[i + 1], 16);
            else if (args[i] == "--names") vm.LoadNamesFile(args[i + 1]);
        }
        bool gridShot = args.Contains("--grid");

        // CLI import runs async now — wait for packages before selecting, so screenshot/--select work.
        if (importDir is not null)
            _ = Dispatcher.InvokeAsync(async () =>
            {
                await vm.ImportDirectoryAsync(importDir);
                if (selectName is not null) vm.SelectPackageByName(selectName);
            });

        // Right-click actions on the asset table AND the thumbnail grid (copy / export / jump).
        // Each Selector gets its own menu instance (a ContextMenu can't be shared) and a
        // right-button handler so the menu targets the row/tile actually under the cursor.
        Loaded += (_, _) =>
        {
            if (_gridWired) return;   // Loaded can fire again on hide/show — wire handlers once
            _gridWired = true;
            if (MainTable is not null)
            {
                MainTable.ContextMenu = BuildRowContextMenu();
                MainTable.PreviewMouseRightButtonDown += SelectItemUnderMouseForContextMenu;
            }
            if (GridView is not null)
            {
                GridView.ContextMenu = BuildRowContextMenu();
                GridView.PreviewMouseRightButtonDown += SelectItemUnderMouseForContextMenu;
                // Ctrl+scroll zooms the thumbnails instead of scrolling the list.
                GridView.PreviewMouseWheel += (_, me) =>
                {
                    if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;
                    if (DataContext is MainViewModel vm) { vm.StepThumbSize(me.Delta > 0 ? 1 : -1); me.Handled = true; }
                };
            }
        };

        // --grid activates grid mode once assets load (independent of screenshot).
        if (gridShot && shotPath is null)
            Loaded += async (_, _) =>
            {
                for (int i = 0; i < 120 && !vm.AssetsLoaded; i++) await Task.Delay(250);
                vm.GridMode = true;
            };

        // Auto-restore the last game directory + remember window geometry on a normal launch.
        if (!cliImport) { RestoreWindowGeometry(); _ = vm.RestoreLastSession(); }

        if (shotPath is not null)
        {
            // Poll until assets load (or timeout), render the UI to PNG, then exit.
            Loaded += async (_, _) =>
            {
                if (args.Contains("--content"))
                {
                    vm.ShowContent = true;
                    var g = vm.Content.FirstOrDefault(c => c.Content == "Tumen") ?? vm.Content.FirstOrDefault();
                    vm.SelectedContent = g;
                    await Task.Delay(400);
                    vm.SelectedContentFile = vm.ContentFiles.FirstOrDefault();
                    await Task.Delay(800);
                    try { SaveScreenshot(shotPath); } catch { }
                    System.Windows.Application.Current.Shutdown(); return;
                }
                for (int i = 0; i < 60 && !vm.AssetsLoaded; i++)
                    await Task.Delay(250);
                if (gridShot)
                {
                    vm.GridMode = true;
                    // Let the visible tiles decode on demand, then realise the viewport once.
                    await Task.Delay(4000);
                    GridView.UpdateLayout();
                    await Task.Delay(6000);
                    if (args.Contains("--scroll"))
                    {
                        var sv = FindScrollViewer(GridView);
                        for (int s = 0; s < 40; s++) sv?.LineDown();
                        await Task.Delay(3000);
                    }
                }
                else if (_shotAssetHash is { } hash) { vm.SelectAssetByHash(hash); await Task.Delay(1800); }
                else { vm.SelectFirstImageAsset(); await Task.Delay(1200); }
                try { SaveScreenshot(shotPath); }
                catch (Exception ex) { System.IO.File.WriteAllText(shotPath + ".err.txt", ex.ToString()); }
                System.Windows.Application.Current.Shutdown();
            };
        }
    }

    // ── Remember window geometry across launches (normal launch only) ────────────
    // Geometry uses the SAME Settings instance the view-model holds (single source of truth),
    // and is kept continuously current by the move/resize handlers — so any settings save
    // (from either side) persists the real window bounds, even on a crash/kill between changes.
    private Settings? _cfg;

    private void RestoreWindowGeometry()
    {
        _cfg = (DataContext as MainViewModel)?.Settings ?? Settings.Load();
        if (_cfg.WindowWidth is > 400 and < 10000) Width = _cfg.WindowWidth;
        if (_cfg.WindowHeight is > 300 and < 10000) Height = _cfg.WindowHeight;
        // Only restore position if it lands on a visible screen (avoid off-screen windows).
        double vs = System.Windows.SystemParameters.VirtualScreenLeft, vt = System.Windows.SystemParameters.VirtualScreenTop;
        double vw = System.Windows.SystemParameters.VirtualScreenWidth, vh = System.Windows.SystemParameters.VirtualScreenHeight;
        if (_cfg.WindowLeft is { } l && _cfg.WindowTop is { } t
            && l >= vs - 8 && l < vs + vw - 100 && t >= vt - 8 && t < vt + vh - 60)
        {
            WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            Left = l; Top = t;
        }
        if (_cfg.WindowMaximized) WindowState = System.Windows.WindowState.Maximized;

        // Keep the shared settings' geometry live (in memory) as the window moves/resizes.
        LocationChanged += (_, _) => UpdateGeometry();
        SizeChanged += (_, _) => UpdateGeometry();
        StateChanged += (_, _) => UpdateGeometry();
        Closing += (_, _) => { UpdateGeometry(); _cfg!.Save(); };
    }

    private void UpdateGeometry()
    {
        if (_cfg is null) return;
        _cfg.WindowMaximized = WindowState == System.Windows.WindowState.Maximized;
        // RestoreBounds holds the normal (non-maximized) rect even when maximized/minimized.
        var r = WindowState == System.Windows.WindowState.Normal
            ? new System.Windows.Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.Width is > 400 and < 10000) _cfg.WindowWidth = r.Width;
        if (r.Height is > 300 and < 10000) _cfg.WindowHeight = r.Height;
        if (!double.IsNaN(r.Left) && !double.IsNaN(r.Top)) { _cfg.WindowLeft = r.Left; _cfg.WindowTop = r.Top; }
    }

    // ── Drag-and-drop a game folder (or a file within it) to import ──────────────
    private static bool HasFolderDrop(System.Windows.DragEventArgs e) =>
        e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
        && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] { Length: > 0 };

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        bool ok = HasFolderDrop(e);
        e.Effects = ok ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        if (DropOverlay is not null)
            DropOverlay.Visibility = ok ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (DropOverlay is not null) DropOverlay.Visibility = System.Windows.Visibility.Collapsed;
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (DropOverlay is not null) DropOverlay.Visibility = System.Windows.Visibility.Collapsed;
        if (DataContext is not MainViewModel vm || !HasFolderDrop(e)) return;
        e.Handled = true;
        try
        {
            var paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            var p = paths[0];
            string dir = System.IO.Directory.Exists(p) ? p : System.IO.Path.GetDirectoryName(p) ?? p;
            vm.ImportDirectory(dir);   // async — never blocks the UI thread now
        }
        catch (Exception ex) { vm.Status = $"Could not read the dropped item: {ex.Message}"; }
    }

    // ── Jump to address → new tab, sorted by offset, scrolled to target ──────────
    private void GoToAddress_Click(object sender, System.Windows.RoutedEventArgs e) => DoJump();

    private void GoToBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) DoJump();
    }

    private void ToggleUpdatesFilter_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ToggleUpdatesFilter();
    }

    private void ExportMenu_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not System.Windows.Controls.Button btn) return;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = btn };
        void Item(string header, System.Windows.Input.ICommand cmd)
        {
            var mi = new System.Windows.Controls.MenuItem { Header = header, Command = cmd };
            menu.Items.Add(mi);
        }
        Item("Export images — this package…", vm.ExportImagesCommand);
        Item("Export images — all packages…", vm.ExportImagesAllCommand);
        menu.Items.Add(new System.Windows.Controls.Separator());
        Item("Export raw — this package…", vm.ExportPackageCommand);
        Item("Export raw — whole game…", vm.ExportGameCommand);
        menu.Items.Add(new System.Windows.Controls.Separator());
        Item("Export CSV — this package…", vm.ExportCsvCommand);
        Item("Export CSV — all packages…", vm.ExportCsvAllCommand);
        btn.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void RecentFolders_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not System.Windows.Controls.Button btn) return;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = btn };
        foreach (var dir in vm.RecentFolders)
        {
            var mi = new System.Windows.Controls.MenuItem { Header = dir };
            var d = dir;   // capture
            mi.Click += (_, _) => vm.OpenRecent(d);
            menu.Items.Add(mi);
        }
        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new System.Windows.Controls.Separator());
            var clear = new System.Windows.Controls.MenuItem { Header = "Clear recent folders" };
            clear.Click += (_, _) => vm.ClearRecentFolders();
            menu.Items.Add(clear);
        }
        btn.ContextMenu = menu;   // root it to the button so it inherits DataContext/resources
        menu.IsOpen = menu.Items.Count > 0;
    }

    private void DoJump()
    {
        if (DataContext is not MainViewModel vm) return;
        if (!MainViewModel.TryParseAddress(GoToBox.Text, out var addr))
        { vm.Status = $"'{GoToBox.Text}' is not a valid address."; return; }
        JumpToAddress(vm, addr);
    }

    // Navigate the MAIN view to the asset nearest a global offset — select it and scroll it into
    // view in the table (offset order, all assets), instead of opening a separate tab.
    private void JumpToAddress(MainViewModel vm, ulong addr)
    {
        var target = vm.NearestByAddress(addr);
        if (target is null) { vm.Status = vm.Packages.Count == 0 ? "No package loaded." : $"No asset near 0x{addr:x}."; return; }
        vm.PrepareForJump();                 // table view, all assets, offset order → target is visible
        vm.SelectedAsset = target;
        MainTable.Dispatcher.BeginInvoke(new Action(() =>
        {
            MainTable.UpdateLayout();
            MainTable.ScrollIntoView(target);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
        vm.Status = $"Jumped to {target.Offset} (nearest to 0x{addr:x}).";
    }

    private System.Windows.Controls.ContextMenu BuildRowContextMenu()
    {
        var m = new System.Windows.Controls.ContextMenu();
        // Single-target actions operate on the row the menu was opened on (captured by the
        // right-button handler), falling back to the Selector's primary SelectedItem.
        AssetRow? Target(System.Windows.Controls.MenuItem mi) =>
            _contextRow
            ?? ((mi.Parent as System.Windows.Controls.ContextMenu)?.PlacementTarget
                    is System.Windows.Controls.Primitives.Selector sel ? sel.SelectedItem as AssetRow : null);
        void Item(string header, Action<AssetRow> act)
        {
            var mi = new System.Windows.Controls.MenuItem { Header = header };
            mi.Click += (_, _) => { if (Target(mi) is { } r) act(r); };
            m.Items.Add(mi);
        }
        void Sep() => m.Items.Add(new System.Windows.Controls.Separator());

        Item("Copy name / hash", r => CopyText(r.DisplayName));
        Item("Copy hash", r => CopyText(r.Hash));
        Item("Copy offset", r => CopyText(r.Offset));
        Sep();
        Item("Export raw asset…", r =>
        {
            if (DataContext is MainViewModel vm) { vm.SelectedAsset = r; vm.ExportAssetCommand.Execute(null); }
        });
        // Export ALL selected rows (enabled only when 2+ are selected).
        var exportSel = new System.Windows.Controls.MenuItem { Header = "Export selected…" };
        exportSel.Click += (_, _) =>
        {
            if ((exportSel.Parent as System.Windows.Controls.ContextMenu)?.PlacementTarget
                    is System.Windows.Controls.Primitives.Selector sel
                && DataContext is MainViewModel vm)
            {
                var rows = SelectedItemsOf(sel)?.OfType<AssetRow>().ToList() ?? [];
                if (rows.Count > 0) _ = vm.ExportSelectedAssetsAsync(rows);
            }
        };
        m.Items.Add(exportSel);
        // Reflect the selection count in the header when the menu opens.
        m.Opened += (_, _) =>
        {
            int n = SelectedItemsOf(m.PlacementTarget as System.Windows.Controls.Primitives.Selector)?.Count ?? 0;
            exportSel.Header = n > 1 ? $"Export {n} selected…" : "Export selected…";
            exportSel.IsEnabled = n > 1;
        };
        Sep();
        Item("Jump to this address", r =>
        {
            if (DataContext is MainViewModel vm) JumpToAddress(vm, r.Entry.Offset);
        });
        Item("Go to ~1MB before this address", r =>
        {
            if (DataContext is MainViewModel vm)
                JumpToAddress(vm, r.Entry.Offset > 0x100000 ? r.Entry.Offset - 0x100000 : 0);
        });
        return m;
    }

    private static void CopyText(string text)
    {
        try { System.Windows.Clipboard.SetDataObject(text, copy: true); } catch { /* clipboard busy */ }
    }

    // Right-click should act on the row/tile under the cursor: select it before the menu opens —
    // but preserve an existing multi-selection when right-clicking one of the selected items.
    private void SelectItemUnderMouseForContextMenu(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.Selector selector) return;
        var dep = e.OriginalSource as System.Windows.DependencyObject;
        while (dep is not null and not System.Windows.Controls.ListBoxItem and not System.Windows.Controls.DataGridRow)
            dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
        if (dep is not System.Windows.FrameworkElement { DataContext: AssetRow row }) return;
        _contextRow = row;   // single-target menu actions operate on the row actually under the cursor
        var selected = SelectedItemsOf(selector);
        if (selected is not null && selected.Contains(row)) return; // keep the multi-selection intact
        selector.SelectedItem = row;
    }

    private AssetRow? _contextRow;   // the asset row the context menu was opened on

    /// <summary>The SelectedItems list of a ListBox or DataGrid (Selector base lacks it).</summary>
    private static System.Collections.IList? SelectedItemsOf(System.Windows.Controls.Primitives.Selector? s) => s switch
    {
        System.Windows.Controls.ListBox lb => lb.SelectedItems,
        System.Windows.Controls.DataGrid dg => dg.SelectedItems,
        _ => null,
    };

    // Double-click a candidate render → user says "this is the correct format" → log + apply.
    private void Candidate_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is System.Windows.FrameworkElement { DataContext: CandidateThumb c }
            && DataContext is MainViewModel vm)
            vm.ChooseCandidate(c);
    }

    private void ShowPackages_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ShowContent = false;
    }

    private void ShowContent_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.ShowContent = true;
    }

    private void TableMode_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.GridMode = false;
    }

    private void GridMode_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.GridMode = true;
    }

    // A cell was realized (first time) → load its thumbnail.
    private void Thumb_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { DataContext: AssetRow row })
            row.EnsureThumbnail();
    }

    // A recycled cell was reassigned to a different row → load THAT row's thumbnail.
    // (With virtualization recycling, Loaded fires once per container, not per item.)
    private void Thumb_DataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is AssetRow row) row.EnsureThumbnail();
    }

    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(System.Windows.DependencyObject? o)
    {
        if (o is null) return null;
        if (o is System.Windows.Controls.ScrollViewer sv) return sv;
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(o);
        for (int i = 0; i < n; i++)
        {
            var r = FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(o, i));
            if (r is not null) return r;
        }
        return null;
    }

    private void SaveScreenshot(string path)
    {
        UpdateLayout();
        int w = (int)ActualWidth, h = (int)ActualHeight;
        if (w <= 0 || h <= 0) { w = (int)Width; h = (int)Height; }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(this);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }
}