using DedupDesk.Core;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DedupDesk.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ScanRoot> roots = [];
    private ScanOptions settings = new();
    private ScanOptions? runOptions;
    private Catalog? catalog;
    private DiskMap? disks;
    private Scanner? scanner;
    private CancellationTokenSource? cancellation;
    private bool initialized, changing, busy, recycling;
    private long runId, total;
    private int page, queryVersion;
    private int progressGeneration, searchWorkerLimit, hashWorkerLimit;
    private string? folder;
    private IReadOnlyList<ResultRow> rows = [];
    private readonly Dictionary<long, long> checkedFiles = [];
    private readonly string settingsFile;
    private readonly bool smoke;
    private readonly Stopwatch elapsed = new();
    private const int PageSize = 200;
    public MainWindow()
    {
        var args = Environment.GetCommandLineArgs(); smoke = args.Contains("--smoke");
        settingsFile = smoke ? Path.Combine(Path.GetFullPath(args[Array.IndexOf(args, "--smoke") + 1]), "ui-settings.json")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DedupDesk", "settings.json");
        InitializeComponent();
        var cellText = new Style(typeof(TextBlock));
        cellText.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        cellText.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(5, 0, 5, 0)));
        cellText.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        cellText.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(ResultRow.Path))));
        foreach (var column in ResultsGrid.Columns.OfType<DataGridTextColumn>()) column.ElementStyle = cellText;
        RoleColumn.ItemsSource = new[] { "保留目录", "待清理目录" }; RootsGrid.ItemsSource = roots;
        FilterBox.ItemsSource = new[] { "全部文件", "照片", "视频", "照片和视频", "自定义扩展名" };
        SortBox.ItemsSource = new[] { "重复组", "名称", "大小", "路径" }; SortBox.SelectedIndex = 0;
        if (File.Exists(settingsFile))
            try { settings = SettingsStore.Load(settingsFile); } catch (Exception ex) when (ex is IOException or JsonException) { ProgressText.Text = "设置无法读取，已使用默认值：" + ex.Message; }
        ApplyOptions(settings); initialized = true;
        Loaded += async (_, _) =>
        {
            if (smoke) return;
            try
            {
                disks = await Task.Run(DiskMap.Discover); catalog = new(settings.CacheDirectory);
                var last = await Task.Run(catalog.LastRun);
                ResumeButton.IsEnabled = last is not null;
                if (last is { State: "已完成" } && catalog.ConfigurationMatches(last.Value.Id, settings))
                {
                    runId = last.Value.Id; runOptions = last.Value.Options; await LoadPageAsync(); await LoadTreeAsync();
                    ProgressText.Text = "已载入上次结果；回收前仍会重新核验文件。";
                }
            }
            catch (Exception ex) { ShowError(ex); }
        };
    }
    private void ApplyOptions(ScanOptions options)
    {
        changing = true;
        settings = options.Clone(); roots.Clear(); foreach (var root in settings.Roots) roots.Add(root);
        NameMode.IsChecked = settings.Mode == CompareMode.NameSize; Md5Mode.IsChecked = settings.Mode == CompareMode.MD5; ShaMode.IsChecked = settings.Mode == CompareMode.SHA256;
        FilterBox.SelectedItem = settings.Filter; UpdateExtensions(); UpdateHint(); changing = false;
    }
    private ScanOptions CaptureOptions()
    {
        RootsGrid.CommitEdit(DataGridEditingUnit.Cell, true); RootsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var o = settings.Clone(); o.Roots = roots.Select(r => new ScanRoot { Path = r.Path, Role = r.Role }).ToList();
        o.Mode = Md5Mode.IsChecked == true ? CompareMode.MD5 : ShaMode.IsChecked == true ? CompareMode.SHA256 : CompareMode.NameSize;
        o.Filter = FilterBox.SelectedItem as string ?? "全部文件"; return o;
    }
    private void SaveSettings()
    {
        settings = CaptureOptions(); SettingsStore.Save(settingsFile, settings);
    }
    private void InvalidateResults()
    {
        if (!initialized || changing || busy) return;
        runId = 0; runOptions = null; queryVersion++; checkedFiles.Clear(); rows = []; ResultsGrid.ItemsSource = rows; FolderTree.Items.Clear(); total = 0; page = 0;
        EmptyText.Visibility = Visibility.Visible; EmptyText.Text = "配置已更改，请重新扫描。"; PageText.Text = "第 0 / 0 页"; SummaryText.Text = "旧结果已失效";
        ShowWorkerCounts(0, 0, 0, 0);
        try { SaveSettings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ProgressText.Text = "目录设置保存失败：" + ex.Message; }
    }
    private void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "添加扫描目录（默认保留）", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FolderNames) if (!roots.Any(r => string.Equals(r.Path, Paths.Normalize(path), StringComparison.OrdinalIgnoreCase))) roots.Add(new() { Path = Paths.Normalize(path) });
        InvalidateResults();
    }
    private void RemoveRoot_Click(object sender, RoutedEventArgs e) { foreach (var row in RootsGrid.SelectedItems.Cast<ScanRoot>().ToArray()) roots.Remove(row); InvalidateResults(); }
    private void Roots_EditEnding(object sender, DataGridCellEditEndingEventArgs e) => Dispatcher.BeginInvoke(InvalidateResults);
    private void RuleChanged(object sender, RoutedEventArgs e) { if (!initialized) return; UpdateHint(); InvalidateResults(); }
    private void UpdateHint() { if (ModeHint is not null) { bool names = NameMode.IsChecked == true; ModeHint.Text = names ? "未核验内容。速度最快，不读取文件内容。" : "忽略文件名；建立全部匹配类型文件的完整指纹库。"; ModeHint.Foreground = new SolidColorBrush(names ? Color.FromRgb(161,98,7) : Color.FromRgb(37,99,235)); } }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (!initialized || changing) return; settings.Filter = FilterBox.SelectedItem as string ?? "全部文件"; changing = true; UpdateExtensions(); changing = false; InvalidateResults(); }
    private void UpdateExtensions()
    {
        ExtensionsBox.IsEnabled = settings.Filter != "全部文件";
        ExtensionsBox.Text = settings.Filter switch { "照片" => settings.PhotoExtensions, "视频" => settings.VideoExtensions, "照片和视频" => settings.PhotoExtensions + " " + settings.VideoExtensions, "自定义扩展名" => settings.CustomExtensions, _ => "扫描所有扩展名" };
    }
    private void ExtensionsChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized || changing) return;
        switch (settings.Filter)
        {
            case "照片": settings.PhotoExtensions = ExtensionsBox.Text; break;
            case "视频": settings.VideoExtensions = ExtensionsBox.Text; break;
            case "照片和视频": settings.CustomExtensions = ExtensionsBox.Text; settings.Filter = "自定义扩展名"; changing = true; FilterBox.SelectedItem = settings.Filter; changing = false; break;
            case "自定义扩展名": settings.CustomExtensions = ExtensionsBox.Text; break;
        }
        InvalidateResults();
    }
    private async void Scan_Click(object sender, RoutedEventArgs e) { try { await StartScanAsync(); } catch (Exception ex) { ShowError(ex); } }
    private async Task StartScanAsync()
    {
        if (busy) return;
        var options = CaptureOptions(); options.Validate(); settings.ForceHash = false; SaveSettings();
        catalog = new(options.CacheDirectory); disks ??= await Task.Run(DiskMap.Discover);
        foreach (var root in options.Roots) disks.ForPath(root.Path);
        scanner = new(catalog, disks); cancellation = new(); runId = 0; runOptions = null; checkedFiles.Clear(); rows = []; ResultsGrid.ItemsSource = rows; FolderTree.Items.Clear();
        folder = null; page = 0; queryVersion++; SetBusy(true); elapsed.Restart(); EmptyText.Text = "正在扫描。完整指纹计算可能需要较长时间，可暂停或取消后恢复。"; EmptyText.Visibility = Visibility.Visible;
        var generation = ++progressGeneration; ShowWorkerCounts(0, 0, 0, 0);
        var progress = new Progress<ScanProgress>(p =>
        {
            if (!busy || recycling || generation != progressGeneration) return;
            ShowWorkerCounts(p.ActiveSearchWorkers, p.SearchWorkerLimit, p.ActiveHashWorkers, p.HashWorkerLimit);
            ProgressText.Text = $"{p.Stage}  ·  已发现 {p.Files:N0} 个文件  ·  已计算 {p.Hashed:N0}  ·  缓存命中 {p.CacheHits:N0}  ·  错误 {p.Errors:N0}  ·  平均读取 {Formatting.Bytes((long)(p.BytesRead / Math.Max(1, elapsed.Elapsed.TotalSeconds)))}/s";
        });
        try
        {
            var result = await Task.Run(() => scanner.RunAsync(options, progress, cancellation.Token));
            runId = result.RunId; runOptions = options; total = 0;
            ShowWorkerCounts(0, result.SearchWorkerLimit, 0, result.HashWorkerLimit);
            SummaryText.Text = $"可清理 {result.Targets:N0} 个文件 · {Formatting.Bytes(result.Bytes)}";
            ProgressText.Text = $"完成 · {result.Files:N0} 个文件 · 已计算 {result.Hashed:N0} · 缓存命中 {result.CacheHits:N0} · 错误 {result.Errors:N0} · 用时 {elapsed.Elapsed:hh\\:mm\\:ss}";
            await LoadPageAsync(); await LoadTreeAsync();
        }
        catch (OperationCanceledException) { ProgressText.Text = "已取消。已完成的指纹已保存，可恢复任务；未完成文件会从头计算。"; EmptyText.Text = "任务未完成，不能执行回收操作。"; }
        finally { progressGeneration++; SetBusy(false); cancellation.Dispose(); cancellation = null; ResumeButton.IsEnabled = true; }
    }
    private void ShowWorkerCounts(int search, int searchLimit, int hash, int hashLimit)
    {
        searchWorkerLimit = searchLimit; hashWorkerLimit = hashLimit;
        WorkerCountsText.Text = $"搜索线程：{search} / {searchLimit}　　计算线程：{hash} / {hashLimit}　（活动 / 上限）";
    }
    private void SetBusy(bool value)
    {
        busy = value; OptionsPanel.IsEnabled = !value; RootsGrid.IsEnabled = !value; AddButton.IsEnabled = RemoveButton.IsEnabled = SettingsButton.IsEnabled = ScanButton.IsEnabled = ResumeButton.IsEnabled = !value;
        PauseButton.IsEnabled = value && !recycling; CancelButton.IsEnabled = value; RecycleButton.IsEnabled = !value; ActivityBar.IsIndeterminate = value; PauseButton.Content = "暂停";
        if (!value) ShowWorkerCounts(0, searchWorkerLimit, 0, hashWorkerLimit);
    }
    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        try { catalog ??= new(settings.CacheDirectory); var last = await Task.Run(catalog.LastRun); if (last is null) return; ApplyOptions(last.Value.Options); settings.ForceHash = false; await StartScanAsync(); } catch (Exception ex) { ShowError(ex); }
    }
    private void Pause_Click(object sender, RoutedEventArgs e) { if (scanner is null) return; if (scanner.Pause.IsPaused) { scanner.Pause.Resume(); PauseButton.Content = "暂停"; } else { scanner.Pause.Pause(); PauseButton.Content = "继续"; ProgressText.Text = "已请求暂停；正在保存已完成的指纹。"; } }
    private void Cancel_Click(object sender, RoutedEventArgs e) { cancellation?.Cancel(); scanner?.Pause.Resume(); }
    private async Task LoadPageAsync()
    {
        if (catalog is null || runId == 0) return;
        var version = ++queryVersion; var current = runId; var search = SearchBox.Text.Trim(); var sort = SortBox.SelectedItem as string ?? "重复组"; var descending = DescendingBox.IsChecked == true; var parent = TreeMode.IsChecked == true ? folder : null; var pageIndex = page;
        var result = await Task.Run(() => catalog.Query(current, pageIndex, PageSize, search, sort, descending, parent));
        if (version != queryVersion || runId != current) return;
        rows = result.Rows; total = result.Total;
        foreach (var row in rows)
        {
            row.Selected = checkedFiles.ContainsKey(row.Id);
            row.PropertyChanged += (_, args) => { if (args.PropertyName != nameof(ResultRow.Selected)) return; if (row.Selected) checkedFiles[row.Id] = row.Size; else checkedFiles.Remove(row.Id); UpdatePageLabel(); };
        }
        ResultsGrid.ItemsSource = rows; UpdatePageLabel();
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed; EmptyText.Text = parent is null ? "没有符合条件的重复文件。" : "本目录没有直接匹配文件；可展开下级目录。";
    }
    private void UpdatePageLabel() => PageText.Text = $"第 {(total == 0 ? 0 : page + 1)} / {Math.Max(0, (total + PageSize - 1) / PageSize):N0} 页 · {total:N0} 项 · 已勾选 {checkedFiles.Count:N0}";
    private async Task LoadTreeAsync()
    {
        FolderTree.Items.Clear(); if (catalog is null || runId == 0) return;
        var id = runId; var candidates = runOptions!.Roots.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var children = await Task.Run(() => candidates.Where(path => !candidates.Any(other => other != path && Paths.IsWithin(path, other)) && catalog.FolderExists(id, path)).ToArray()); if (runId != id) return;
        foreach (var child in children)
        {
            var node = CreateTreeNode(child); var name = Path.GetFileName(child); node.Header = (name.Length == 0 ? child : name) + (Paths.RoleFor(child, runOptions.Roots) == RootRole.Keep ? " · 保留" : " · 待清理"); FolderTree.Items.Add(node);
        }
    }
    private TreeViewItem CreateTreeNode(string path)
    {
        var node = new TreeViewItem { Header = Path.GetFileName(path) is { Length: > 0 } name ? name : path, Tag = path, ToolTip = path };
        node.Items.Add(new TreeViewItem { Header = "加载中…" });
        RoutedEventHandler? expanded = null;
        expanded = async (_, e) => { if (e.OriginalSource != node) return; node.Expanded -= expanded; node.Items.Clear(); await LoadChildrenAsync(node, path, 0); };
        node.Expanded += expanded; return node;
    }
    private async Task LoadChildrenAsync(TreeViewItem node, string path, int offset)
    {
        if (catalog is null || runId == 0) return; var id = runId;
        var children = await Task.Run(() => catalog.ChildFolders(id, path, offset)); if (runId != id) return;
        foreach (var child in children) node.Items.Add(CreateTreeNode(child));
        if (children.Count == 200)
        {
            var more = new TreeViewItem { Header = "加载更多目录…" };
            more.Selected += async (_, e) => { e.Handled = true; node.Items.Remove(more); await LoadChildrenAsync(node, path, offset + 200); }; node.Items.Add(more);
        }
    }
    private async void Tree_Selected(object sender, RoutedPropertyChangedEventArgs<object> e) { if (e.NewValue is TreeViewItem { Tag: string path }) { folder = path; page = 0; await LoadPageAsync(); } }
    private async void ViewChanged(object sender, RoutedEventArgs e) { if (!initialized) return; TreeColumn.Width = TreeMode.IsChecked == true ? new GridLength(260) : new GridLength(0); page = 0; await LoadPageAsync(); }
    private async void Search_Click(object sender, RoutedEventArgs e) { page = 0; await LoadPageAsync(); }
    private async void Search_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { page = 0; await LoadPageAsync(); } }
    private async void SortChanged(object sender, RoutedEventArgs e) { if (!initialized) return; page = 0; await LoadPageAsync(); }
    private async void Prev_Click(object sender, RoutedEventArgs e) { if (page > 0) { page--; await LoadPageAsync(); } }
    private async void Next_Click(object sender, RoutedEventArgs e) { if ((page + 1L) * PageSize < total) { page++; await LoadPageAsync(); } }
    private void SelectPage_Click(object sender, RoutedEventArgs e) { foreach (var row in rows) row.Selected = true; }
    private void Results_RightClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not DataGridRow) source = VisualTreeHelper.GetParent(source);
        if (source is DataGridRow row && !row.IsSelected) { ResultsGrid.SelectedItems.Clear(); row.IsSelected = true; }
    }
    private void Tree_RightClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not TreeViewItem) source = VisualTreeHelper.GetParent(source);
        if (source is TreeViewItem node) node.IsSelected = true;
    }
    private async void RecycleSelected_Click(object sender, RoutedEventArgs e)
    {
        if (busy || runId == 0 || runOptions is null) return;
        var selection = checkedFiles.Select(p => (Id: p.Key, Size: p.Value)).Concat(ResultsGrid.SelectedItems.Cast<ResultRow>().Where(r => r.CanSelect).Select(r => (r.Id,r.Size))).DistinctBy(r => r.Id).ToArray();
        if (selection.Length == 0) { MessageBox.Show(this, "请先选择待清理文件。保留副本不能回收。", "DedupDesk"); return; }
        await RecycleAsync(selection.Select(r => r.Id).ToArray(), null, selection.LongLength, selection.Sum(r => r.Size));
    }
    private async void RecycleFolder_Click(object sender, RoutedEventArgs e)
    {
        if (busy || catalog is null || runId == 0 || runOptions is null || FolderTree.SelectedItem is not TreeViewItem { Tag: string path }) return;
        var stats = await Task.Run(() => catalog.TargetStats(runId, path));
        if (stats.Count == 0) { MessageBox.Show(this, "此目录下没有可清理的重复文件。", "DedupDesk"); return; }
        await RecycleAsync(null, path, stats.Count, stats.Bytes);
    }
    private async Task RecycleAsync(long[]? ids, string? subtree, long count, long bytes)
    {
        if (catalog is null || runOptions is null) return;
        if (MessageBox.Show(this, $"将 {count:N0} 个文件移入回收站，共 {Formatting.Bytes(bytes)}。\n\n比较方式：{Formatting.Mode(runOptions.Mode)}\n保留目录中的文件保持不变。" + (runOptions.Mode == CompareMode.NameSize ? "\n当前只比较名字和大小，未核验文件内容。" : "\n回收前将重新读取并核验完整内容。") + "\n\n无法进入回收站的文件将跳过。", "移入回收站", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        recycling = true; SetBusy(true); cancellation = new(); long good = 0, bad = 0, processed = 0;
        int activeVerification = 0; int verificationLimit = runOptions.Mode == CompareMode.NameSize ? 0 : 1;
        ShowWorkerCounts(0, 0, 0, verificationLimit);
        var verificationTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        verificationTimer.Tick += (_, _) => ShowWorkerCounts(0, 0, Volatile.Read(ref activeVerification), verificationLimit);
        verificationTimer.Start();
        try
        {
            var service = new RecyclingService(catalog, new WindowsRecycleBin()); long after = 0;
            do
            {
                var batch = ids ?? (await Task.Run(() => catalog.TargetIds(runId, subtree!, after))).ToArray(); if (batch.Length == 0) break;
                foreach (var id in batch)
                {
                    cancellation.Token.ThrowIfCancellationRequested(); after = id;
                    var result = await Task.Run(() => service.RecycleAsync(runId, id, runOptions, cancellation.Token, working => Interlocked.Add(ref activeVerification, working ? 1 : -1))); if (result.Success) good++; else bad++; processed++; checkedFiles.Remove(id);
                    ProgressText.Text = $"回收进度 {processed:N0}/{count:N0} · 已移入回收站 {good:N0} · 跳过 {bad:N0} · {result.Message}";
                }
                if (ids is not null) break;
            } while (true);
            ProgressText.Text = $"回收完成 · 已移入回收站 {good:N0} 个 · 跳过 {bad:N0} 个。文件大小不代表立即释放的磁盘空间。";
        }
        catch (OperationCanceledException) { ProgressText.Text = $"回收已取消 · 已完成 {good:N0} 个 · 跳过 {bad:N0} 个"; }
        catch (Exception ex) { ShowError(ex); }
        finally { verificationTimer.Stop(); cancellation.Dispose(); cancellation = null; recycling = false; SetBusy(false); await LoadPageAsync(); }
    }
    private void Open_Click(object sender, RoutedEventArgs e) { if (ResultsGrid.SelectedItem is ResultRow row) OpenExplorer(row.Path, true); }
    private void Copy_Click(object sender, RoutedEventArgs e) { var paths = ResultsGrid.SelectedItems.Cast<ResultRow>().Select(r => r.Path); Clipboard.SetText(string.Join(Environment.NewLine, paths)); }
    private void OpenTree_Click(object sender, RoutedEventArgs e) { if (FolderTree.SelectedItem is TreeViewItem { Tag: string path }) OpenExplorer(path, false); }
    private void CopyTree_Click(object sender, RoutedEventArgs e) { if (FolderTree.SelectedItem is TreeViewItem { Tag: string path }) Clipboard.SetText(path); }
    private static void OpenExplorer(string path, bool select) => Process.Start(new ProcessStartInfo("explorer.exe", (select ? "/select," : "") + "\"" + path + "\"") { UseShellExecute = true });
    private async void Errors_Click(object sender, RoutedEventArgs e)
    {
        if (catalog is null) return; var id = runId == 0 ? catalog.LastRun()?.Id ?? 0 : runId;
        var text = await Task.Run(() => catalog.ErrorReport(id));
        new Window { Owner = this, Title = "错误记录（最多显示 1000 条）", Width = 850, Height = 550, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } }.ShowDialog();
    }
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        disks ??= await Task.Run(DiskMap.Discover); foreach (var root in roots) disks.ForPath(root.Path);
        var dialog = new SettingsWindow(CaptureOptions(), disks.Disks) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        settings = dialog.Options; catalog = new(settings.CacheDirectory); InvalidateResults(); SaveSettings();
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; MessageBox.Show(this, "请先取消当前任务，等待指纹保存完成后再关闭。", "DedupDesk"); return; }
        try { SaveSettings(); } catch (Exception ex) { MessageBox.Show(this, "设置保存失败：" + ex.Message); }
    }
    private void ShowError(Exception ex) { ProgressText.Text = ex.Message; MessageBox.Show(this, ex.Message, "DedupDesk", MessageBoxButton.OK, MessageBoxImage.Error); }
    public async Task SmokeAsync(string outputDirectory)
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        Directory.CreateDirectory(outputDirectory); string data = Path.Combine(outputDirectory, "isolated-data"); string keep = Path.Combine(data, "保留照片"), target = Path.Combine(data, "待整理备份");
        for (int i = 0; i < 16; i++)
        {
            var sub = i < 8 ? "旅行相册" : "摄影素材"; Directory.CreateDirectory(Path.Combine(keep, sub)); Directory.CreateDirectory(Path.Combine(target, sub));
            var payload = Enumerable.Repeat((byte)i, 4096 + i * 4096).ToArray(); File.WriteAllBytes(Path.Combine(keep, sub, $"IMG_{1000 + i}.jpg"), payload); File.WriteAllBytes(Path.Combine(target, sub, $"IMG_{1000 + i}.jpg"), payload);
        }
        ApplyOptions(new() { Roots = [new() { Path = keep }, new() { Path = target, Role = RootRole.Target }], CacheDirectory = Path.Combine(outputDirectory, "cache") });
        InvalidateResults();
        var reopened = new MainWindow(); var restored = reopened.CaptureOptions(); reopened.Close();
        if (restored.Roots.Count != 2 || restored.Roots[0].Path != keep || restored.Roots[1].Path != target || restored.Roots[1].Role != RootRole.Target)
            throw new InvalidOperationException("Added directories were not saved and restored before scanning");
        disks = new DiskMap(); await StartScanAsync(); if (rows.Count != 32) throw new InvalidOperationException("UI smoke result count mismatch");
        if (!WorkerCountsText.Text.Contains("搜索线程：0 / 2") || !WorkerCountsText.Text.Contains("计算线程：0 / 0"))
            throw new InvalidOperationException("Completed worker counts did not reset correctly");
        var checkedRow = rows.First(r => r.CanSelect); long checkedId = checkedRow.Id; checkedRow.Selected = true;
        SearchBox.Text = "IMG_1000"; await LoadPageAsync(); SearchBox.Text = ""; await LoadPageAsync();
        if (!rows.Single(r => r.Id == checkedId).Selected) throw new InvalidOperationException("Checkbox selection was lost while browsing");
        rows.Single(r => r.Id == checkedId).Selected = false;
        await Capture("list.png"); TreeMode.IsChecked = true;
        if (FolderTree.Items.Count > 1 && FolderTree.Items[1] is TreeViewItem node)
        {
            node.IsExpanded = true; await Task.Delay(200);
            if (node.Items.Count > 0 && node.Items[0] is TreeViewItem leaf) leaf.IsSelected = true;
        }
        await Capture("tree.png");
        if (rows.Count != 8 || rows.Any(r => r.Role != RootRole.Target)) throw new InvalidOperationException("Tree folder filter is incorrect");
        var settingsDialog = new SettingsWindow(CaptureOptions(), disks.Disks) { Owner = this }; settingsDialog.Show();
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); settingsDialog.Close();
        File.WriteAllText(Path.Combine(outputDirectory, "smoke.txt"), "PASS: directories restored before first scan; worker counts reset; 32 list records; 8 target tree records; checkbox selection survives search; settings window loads; no recycling performed.");
        async Task Capture(string name)
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle); await Task.Delay(300); UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(outputDirectory, name)); encoder.Save(file);
        }
    }
}
