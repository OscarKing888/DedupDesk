using System.Threading.Channels;

namespace DedupDesk.Core;

public sealed class Scanner(Catalog catalog, DiskMap disks)
{
    public PauseGate Pause { get; } = new();
    private long files, hashed, hits, errors, bytes;
    private string stage = "枚举与计算";
    private sealed record WriteItem(IndexedFile? File = null, string? Path = null, string? Error = null);
    public async Task<ScanSummary> RunAsync(ScanOptions input, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var options = input.Clone(); options.Validate(); var run = catalog.StartRun(options);
        files = hashed = hits = errors = bytes = 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token); var ct = linked.Token;
        using var cpu = new SemaphoreSlim(options.CpuConcurrency);
        var output = Channel.CreateBounded<WriteItem>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        using var timer = new System.Threading.Timer(_ => progress?.Report(new(Interlocked.Read(ref files), Interlocked.Read(ref hashed), Interlocked.Read(ref hits), Interlocked.Read(ref errors), Interlocked.Read(ref bytes), Pause.IsPaused ? "已暂停" : stage)), null, 0, 250);
        var writer = Task.Run(async () =>
        {
            using var connection = catalog.Open(); var batch = new List<IndexedFile>(256); var failures = new List<(string,string)>();
            try
            {
                while (await output.Reader.WaitToReadAsync())
                {
                    // Short coalescing window also commits small scans and cancellation promptly.
                    await Task.Delay(25);
                    while (batch.Count + failures.Count < 256 && output.Reader.TryRead(out var item))
                        if (item.File is not null) batch.Add(item.File); else failures.Add((item.Path!, item.Error!));
                    catalog.WriteBatch(connection, run, options.Mode, batch, failures); batch.Clear(); failures.Clear();
                }
            }
            catch { await linked.CancelAsync(); throw; }
        });
        var roots = options.Roots.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length).ToList();
        roots = roots.Where(p => !roots.Any(other => other != p && Paths.IsWithin(p, other))).ToList();
        var extensions = options.Extensions();
        var groupedRoots = roots.GroupBy(p => disks.ForPath(p).Key).ToArray();
        var tasks = new List<Task>();
        foreach (var group in groupedRoots)
        {
            var disk = disks.ForPath(group.First());
            int concurrency = Math.Clamp(options.DiskConcurrency.GetValueOrDefault(disk.Key, disk.Concurrency), 1, 64);
            var queue = Channel.CreateBounded<FileStamp>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait });
            var producer = Task.Run(async () =>
            {
                try
                {
                    foreach (var root in group)
                        await Enumerate(root);
                }
                finally { queue.Writer.TryComplete(); }
                async Task Enumerate(string root)
                {
                    // A stack of enumerators bounds memory by directory depth, not file count.
                    var stack = new Stack<IEnumerator<string>>();
                    try
                    {
                        stack.Push(Directory.EnumerateFileSystemEntries(root).GetEnumerator());
                        while (stack.Count > 0)
                        {
                            await Pause.WaitAsync(ct);
                            string path;
                            try { if (!stack.Peek().MoveNext()) { stack.Pop().Dispose(); continue; } path = stack.Peek().Current; }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stack.Pop().Dispose(); await Error(root, ex.Message); continue; }
                            try
                            {
                                var attr = File.GetAttributes(path);
                                if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                                if ((attr & FileAttributes.Directory) != 0)
                                {
                                    var name = Path.GetFileName(path);
                                    if (name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) || Paths.IsWithin(path, options.CacheDirectory)) continue;
                                    stack.Push(Directory.EnumerateFileSystemEntries(path).GetEnumerator()); continue;
                                }
                                if (extensions is not null && !extensions.Contains(Path.GetExtension(path))) continue;
                                var stamp = FileAccess.Stat(path); Interlocked.Increment(ref files);
                                await queue.Writer.WriteAsync(stamp, ct);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Error(path, ex.Message); }
                        }
                    }
                    finally { foreach (var enumerator in stack) enumerator.Dispose(); }
                }
            }, ct);
            tasks.Add(producer);
            for (int i = 0; i < concurrency; i++)
                tasks.Add(Task.Run(async () =>
                {
                    using var connection = catalog.Open();
                    await foreach (var stamp in queue.Reader.ReadAllAsync(ct))
                    {
                        await Pause.WaitAsync(ct);
                        try
                        {
                            string? hash = null;
                            if (options.Mode != CompareMode.NameSize)
                            {
                                if (!options.ForceHash) hash = catalog.CachedHash(connection, stamp, options.Mode);
                                if (hash is not null)
                                {
                                    if (!stamp.SameVersion(FileAccess.Stat(stamp.Path))) throw new IOException("文件属性变化，请重扫");
                                    Interlocked.Increment(ref hits);
                                }
                                else
                                {
                                    await cpu.WaitAsync(ct);
                                    try { hash = await FileAccess.HashAsync(stamp, options.Mode, Pause, n => Interlocked.Add(ref bytes, n), ct); }
                                    finally { cpu.Release(); }
                                    Interlocked.Increment(ref hashed);
                                }
                            }
                            await output.Writer.WriteAsync(new(new(stamp, Paths.RoleFor(stamp.Path, options.Roots), hash)), ct);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { await Error(stamp.Path, ex.Message); }
                    }
                }, ct));
        }
        async Task Error(string path, string message)
        {
            Interlocked.Increment(ref errors); await output.Writer.WriteAsync(new(null, path, message), ct);
        }
        // Cancel the pipeline on an unexpected worker failure to unblock bounded queues.
        foreach (var task in tasks) _ = task.ContinueWith(_ => linked.Cancel(), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        try
        {
            await Task.WhenAll(tasks); output.Writer.TryComplete(); await writer;
            ct.ThrowIfCancellationRequested(); stage = "建立重复组与目录索引";
            await Task.Run(() => catalog.BuildGroups(run, options, ct), ct);
            catalog.SetRunState(run, "已完成"); catalog.PruneOldResults(run); var totals = catalog.TargetStats(run);
            progress?.Report(new(files, hashed, hits, errors, bytes, "已完成"));
            return new(run, files, hashed, hits, errors, totals.Count, totals.Bytes);
        }
        catch
        {
            await linked.CancelAsync(); Pause.Resume();
            try { await Task.WhenAll(tasks); } catch { }
            output.Writer.TryComplete(); try { await writer; } catch { }
            catalog.SetRunState(run, token.IsCancellationRequested ? "已取消，可继续" : "失败，可继续"); throw;
        }
    }
}
