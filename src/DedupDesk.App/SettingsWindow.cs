using DedupDesk.Core;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace DedupDesk.App;

public sealed class SettingsWindow : Window
{
    public ScanOptions Options { get; private set; }
    public SettingsWindow(ScanOptions input, IReadOnlyList<DiskInfo> source)
    {
        Options = input.Clone(); Title = "性能与指纹缓存"; Width = 820; Height = 650; MinHeight = 580; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new Grid { Margin = new Thickness(24) }; Content = panel;
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) panel.RowDefinitions.Add(new() { Height = h });
        void Put(UIElement child, int row) { Grid.SetRow(child, row); panel.Children.Add(child); }
        Put(new TextBlock { Text = "并发与本地缓存", FontSize = 22, FontWeight = FontWeights.Bold, Margin = new(0,0,0,20) }, 0);
        var cacheRow = new DockPanel { Margin = new(0,0,0,12) }; var choose = new Button { Content = "选择缓存目录", Margin = new(8,0,0,0) }; DockPanel.SetDock(choose, Dock.Right); cacheRow.Children.Add(choose);
        var cache = new TextBox { Text = Options.CacheDirectory }; cacheRow.Children.Add(cache); Put(cacheRow, 1);
        choose.Click += (_, _) => { var dialog = new OpenFolderDialog { Title = "选择本地缓存目录" }; if (dialog.ShowDialog(this) == true) cache.Text = dialog.FolderName; };
        var cpuRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0,0,0,14) };
        cpuRow.Children.Add(new TextBlock { Text = $"计算并发（本机 {Environment.ProcessorCount} 个逻辑处理器）", VerticalAlignment = VerticalAlignment.Center, Margin = new(0,0,16,0) });
        var cpu = new TextBox { Text = Options.CpuConcurrency.ToString(), Width = 80 }; cpuRow.Children.Add(cpu); Put(cpuRow, 2);
        Put(new TextBlock { Text = "每块物理磁盘的读取并发 · 机械盘建议 1，SSD 4，NVMe 8，网络路径 2", TextWrapping = TextWrapping.Wrap, Margin = new(0,0,0,10) }, 3);
        var diskRows = source.Select(d => new DiskInfo { Key = d.Key, Name = d.Name, Volumes = d.Volumes, Concurrency = Options.DiskConcurrency.GetValueOrDefault(d.Key, d.Concurrency) }).ToList();
        var grid = new DataGrid { ItemsSource = diskRows, Margin = new(0,0,0,16) };
        grid.Columns.Add(new DataGridTextColumn { Header = "设备", Binding = new Binding(nameof(DiskInfo.Name)), Width = new(1,DataGridLengthUnitType.Star), IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "卷", Binding = new Binding(nameof(DiskInfo.Volumes)), Width = 140, IsReadOnly = true });
        grid.Columns.Add(new DataGridTextColumn { Header = "并发 1–64", Binding = new Binding(nameof(DiskInfo.Concurrency)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 100 }); Put(grid,4);
        var force = new CheckBox { Content = "下次扫描忽略已有指纹，重新计算完整哈希", IsChecked = Options.ForceHash, Margin = new(0,0,0,16) }; Put(force,5);
        var buttons = new DockPanel(); var clear = new Button { Content = "清理指纹缓存" }; buttons.Children.Add(clear);
        clear.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, "仅清理指纹数据库中的哈希记录，源文件保持不变。继续？", "清理缓存", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            clear.IsEnabled = false;
            var cachePath = cache.Text;
            try { await Task.Run(() => new Catalog(cachePath).ClearHashes()); MessageBox.Show(this, "指纹缓存已清理。"); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message); } finally { clear.IsEnabled = true; }
        };
        var save = new Button { Content = "保存设置", HorizontalAlignment = HorizontalAlignment.Right, Background = System.Windows.Media.Brushes.RoyalBlue, Foreground = System.Windows.Media.Brushes.White }; buttons.Children.Add(save); Put(buttons,6);
        save.Click += (_, _) =>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (!int.TryParse(cpu.Text, out int value) || value < 1 || value > 1024 || diskRows.Any(d => d.Concurrency < 1 || d.Concurrency > 64)) { MessageBox.Show(this, "计算并发需为 1–1024；磁盘并发需为 1–64。"); return; }
            try { Options.CacheDirectory = Paths.Normalize(cache.Text); } catch (Exception ex) { MessageBox.Show(this, ex.Message); return; }
            Options.CpuConcurrency = value; Options.ForceHash = force.IsChecked == true; Options.DiskConcurrency = diskRows.ToDictionary(d => d.Key, d => d.Concurrency); DialogResult = true;
        };
    }
}
