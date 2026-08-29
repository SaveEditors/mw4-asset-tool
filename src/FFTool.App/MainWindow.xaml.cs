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

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;

        // Optional CLI: --import <dir> [--select <packageName>] [--screenshot <path>]
        var args = Environment.GetCommandLineArgs();
        string? shotPath = null;
        bool cliImport = false;
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--import") { vm.ImportDirectory(args[i + 1]); cliImport = true; }
            else if (args[i] == "--select") vm.SelectPackageByName(args[i + 1]);
            else if (args[i] == "--screenshot") shotPath = args[i + 1];
            else if (args[i] == "--asset") _shotAssetHash = Convert.ToUInt64(args[i + 1], 16);
            else if (args[i] == "--names") vm.LoadNamesFile(args[i + 1]);
        }
        bool gridShot = args.Contains("--grid");

        // Right-click actions on the main asset table (copy / jump).
        Loaded += (_, _) => { if (MainTable is not null) MainTable.ContextMenu = BuildRowContextMenu(); };

        // --grid activates grid mode once assets load (independent of screenshot).
        if (gridShot && shotPath is null)
            Loaded += async (_, _) =>
            {
                for (int i = 0; i < 120 && !vm.AssetsLoaded; i++) await Task.Delay(250);
                vm.GridMode = true;
            };

        // Auto-restore the last game directory on a normal launch.
        if (!cliImport) vm.RestoreLastSession();

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

    private void DoJump()
    {
        if (DataContext is not MainViewModel vm) return;
        if (!MainViewModel.TryParseAddress(GoToBox.Text, out var addr))
        { vm.Status = $"'{GoToBox.Text}' is not a valid address."; return; }
        OpenAddressTab(vm, addr);
    }

    private void OpenAddressTab(MainViewModel vm, ulong addr)
    {
        var rows = vm.AssetsByOffset();
        if (rows.Count == 0) { vm.Status = "No package loaded."; return; }
        var target = vm.NearestByAddress(addr);

        var grid = BuildAssetDataGrid(rows, target);
        var tab = new System.Windows.Controls.TabItem
        {
            Header = $"@0x{addr:x}  ✕",
            Content = grid,
            Style = (System.Windows.Style)FindResource("DarkTabItem"),
        };
        // Close on clicking the ✕ in the header (simple: middle/right or double-click header).
        tab.MouseRightButtonUp += (_, _) => { AssetTabs.Items.Remove(tab); };
        AssetTabs.Items.Add(tab);
        AssetTabs.SelectedItem = tab;

        // Scroll to the target row after layout.
        if (target is not null)
            grid.Dispatcher.BeginInvoke(new Action(() =>
            {
                grid.SelectedItem = target;
                grid.ScrollIntoView(target);
                grid.UpdateLayout();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        vm.Status = target is not null
            ? $"Jumped to {target.Offset} (nearest to 0x{addr:x}). Right-click a tab to close it."
            : $"No asset near 0x{addr:x}.";
    }

    private System.Windows.Controls.DataGrid BuildAssetDataGrid(
        System.Collections.Generic.IReadOnlyList<AssetRow> rows, AssetRow? _)
    {
        var g = new System.Windows.Controls.DataGrid
        {
            Style = (System.Windows.Style)FindResource("DarkDataGrid"),
            ItemsSource = rows,
        };
        void Col(string header, string path, bool mono, string? width = null)
        {
            var c = new System.Windows.Controls.DataGridTextColumn
            {
                Header = header,
                Binding = new System.Windows.Data.Binding(path),
            };
            if (mono)
            {
                var st = new System.Windows.Style(typeof(System.Windows.Controls.TextBlock));
                st.Setters.Add(new System.Windows.Setter(System.Windows.Controls.TextBlock.FontFamilyProperty,
                    (System.Windows.Media.FontFamily)FindResource("MonoFont")));
                c.ElementStyle = st;
            }
            c.Width = width == "*" ? new System.Windows.Controls.DataGridLength(1, System.Windows.Controls.DataGridLengthUnitType.Star)
                                   : System.Windows.Controls.DataGridLength.Auto;
            g.Columns.Add(c);
        }
        Col("Name / hash", nameof(AssetRow.DisplayName), true, "*");
        Col("Type", nameof(AssetRow.Kind), false);
        Col("Dims", nameof(AssetRow.Dims), false);
        Col("Size", nameof(AssetRow.Size), false);
        Col("Offset", nameof(AssetRow.Offset), true);
        g.ContextMenu = BuildRowContextMenu();
        g.SelectionChanged += (s, _) =>
        {
            if (DataContext is MainViewModel vm && g.SelectedItem is AssetRow r) vm.SelectedAsset = r;
        };
        return g;
    }

    private System.Windows.Controls.ContextMenu BuildRowContextMenu()
    {
        var m = new System.Windows.Controls.ContextMenu();
        void Item(string header, Action<AssetRow> act)
        {
            var mi = new System.Windows.Controls.MenuItem { Header = header };
            mi.Click += (s, _) =>
            {
                if (((System.Windows.Controls.ContextMenu)mi.Parent).PlacementTarget is System.Windows.Controls.DataGrid dg
                    && dg.SelectedItem is AssetRow r) act(r);
            };
            m.Items.Add(mi);
        }
        Item("Copy name / hash", r => System.Windows.Clipboard.SetText(r.DisplayName));
        Item("Copy hash", r => System.Windows.Clipboard.SetText(r.Hash));
        Item("Copy offset", r => System.Windows.Clipboard.SetText(r.Offset));
        Item("Jump to this address (new tab)", r =>
        {
            if (DataContext is MainViewModel vm) OpenAddressTab(vm, r.Entry.Offset);
        });
        Item("Find images near this address (±1MB)", r =>
        {
            if (DataContext is MainViewModel vm)
            {
                ulong lo = r.Entry.Offset > 0x100000 ? r.Entry.Offset - 0x100000 : 0;
                OpenAddressTab(vm, lo);
            }
        });
        return m;
    }

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