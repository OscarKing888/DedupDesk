using DedupDesk.Core;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FileAccess = DedupDesk.Core.FileAccess;

var testRoot = Path.GetFullPath(Path.Combine(args.FirstOrDefault(a => !a.StartsWith("--")) ?? Path.Combine(Path.GetTempPath(), "DedupDeskTests"), Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(testRoot);
int passed = 0; var lines = new List<string>();
void Log(string text) { Console.WriteLine(text); lines.Add(text); }
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); }
ScanOptions Setup(string name, CompareMode mode = CompareMode.NameSize)
{
    var dir = Path.Combine(testRoot, name); var keep = Path.Combine(dir, "keep"); var target = Path.Combine(dir, "target"); Directory.CreateDirectory(keep); Directory.CreateDirectory(target);
    return new() { Mode = mode, CacheDirectory = Path.Combine(dir, "cache"), Roots = [new() { Path = keep }, new() { Path = target, Role = RootRole.Target }], CpuConcurrency = 4 };
}
async Task<(Catalog Catalog, ScanSummary Summary)> Scan(ScanOptions o)
{
    var catalog = new Catalog(o.CacheDirectory); var map = new DiskMap(); foreach (var root in o.Roots) { var disk = map.ForPath(root.Path); o.DiskConcurrency[disk.Key] = 4; }
    var summary = await new Scanner(catalog, map).RunAsync(o, null, CancellationToken.None); return (catalog, summary);
}
try
{
    var o = Setup("matching"); string k = o.Roots[0].Path, t = o.Roots[1].Path;
    Write(Path.Combine(k,"same.txt"),"AAAA"); Write(Path.Combine(t,"SAME.txt"),"BBBB");
    Write(Path.Combine(k,"original.jpg"),"image-data"); Write(Path.Combine(t,"renamed.png"),"image-data");
    Write(Path.Combine(k,"empty.bin"),""); Write(Path.Combine(t,"empty.bin"),"");
    Write(Path.Combine(t,"only-target.dat"),"alone"); Write(Path.Combine(t,"sub","only-target.dat"),"alone");
    var result = await Scan(o); Assert(result.Summary.Targets == 2,"name+size matching"); Assert(result.Summary.Hashed == 0,"default must not hash"); Assert(result.Catalog.Query(result.Summary.RunId,0,200).Total == 4,"keepers visible alongside targets");
    o.Mode = CompareMode.MD5; result = await Scan(o); Assert(result.Summary.Targets == 2,"MD5 ignores names, rejects same-size different content"); Assert(result.Summary.Hashed == 8,"full fingerprint catalog includes unique files");
    var again = await Scan(o); Assert(again.Summary.CacheHits == 8 && again.Summary.Hashed == 0,"warm cache");
    File.Move(Path.Combine(t,"renamed.png"),Path.Combine(t,"renamed-again.png")); again = await Scan(o); Assert(again.Summary.CacheHits == 8,"file identity cache survives rename");
    o.Mode = CompareMode.SHA256; result = await Scan(o); Assert(result.Summary.Hashed == 8,"algorithms use separate cache keys");
    o.ForceHash = true; again = await Scan(o); Assert(again.Summary.Hashed == 8 && again.Summary.CacheHits == 0,"force recompute"); o.ForceHash = false;
    o.Filter = "照片"; result = await Scan(o); Assert(result.Summary.Files == 2 && result.Summary.Targets == 1,"photo preset includes png and jpg");
    o.Filter = "自定义扩展名"; o.CustomExtensions = "*.TXT; .BIN"; result = await Scan(o); Assert(result.Summary.Files == 4,"custom case-insensitive extensions");
    result.Catalog.ClearHashes(); again = await Scan(o); Assert(again.Summary.Hashed == 4,"clear fingerprint cache"); Log("PASS matching, complete fingerprints, cache, algorithms, filters");

    o = Setup("overlap"); k=o.Roots[0].Path; t=o.Roots[1].Path;
    Write(Path.Combine(k,"nested","same.bin"),"123"); Write(Path.Combine(t,"same.bin"),"123");
    o.Roots.Add(new() { Path = Path.Combine(k,"nested"), Role = RootRole.Target }); o.Roots.Add(new() { Path=k });
    result = await Scan(o); Assert(result.Summary.Files==2 && result.Summary.Targets==1,"overlap deduplicated, keep wins");
    string link = Path.Combine(t,"hard.bin"); if (!Native.CreateHardLinkW(link,Path.Combine(k,"nested","same.bin"),IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
    o.Mode=CompareMode.SHA256; result = await Scan(o); Assert(result.Summary.Targets==1,"hard link not counted as reclaimable copy");
    string symbolic = Path.Combine(t,"symbolic");
    var junction = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
    junction.ArgumentList.Add("-NoProfile"); junction.ArgumentList.Add("-Command"); junction.ArgumentList.Add("New-Item -ItemType Junction -Path '" + symbolic.Replace("'", "''") + "' -Target '" + k.Replace("'", "''") + "' | Out-Null");
    using (var create = Process.Start(junction)!) { await create.WaitForExitAsync(); Assert(create.ExitCode == 0,"junction fixture creation"); }
    result = await Scan(o); Assert(result.Summary.Files==3,"directory junction skipped");
    string longDir = Path.Combine(t,string.Join(Path.DirectorySeparatorChar,Enumerable.Repeat("中文_"+new string('x',35),7))); Write(Path.Combine(longDir,"same.bin"),"123");
    result = await Scan(o); Assert(result.Summary.Targets==2,"Unicode long paths");
    Log("PASS overlapping roots, keep precedence, hard links, links, long paths");

    o=Setup("recycle-guards",CompareMode.SHA256); k=o.Roots[0].Path; t=o.Roots[1].Path;
    Write(Path.Combine(k,"a.bin"),"AAAA"); Write(Path.Combine(t,"b.bin"),"AAAA"); result=await Scan(o);
    var target = result.Catalog.Query(result.Summary.RunId,0,200).Rows.Single(r=>r.Role==RootRole.Target);
    var recorder=new RecordingBin(); var recycler=new RecyclingService(result.Catalog,recorder);
    var changedOptions=o.Clone(); changedOptions.Mode=CompareMode.NameSize;
    var refused=await recycler.RecycleAsync(result.Summary.RunId,target.Id,changedOptions,CancellationToken.None); Assert(!refused.Success && recorder.Calls==0,"changing comparison invalidates recycle authorization");
    changedOptions=o.Clone(); changedOptions.Roots[1].Role=RootRole.Keep;
    refused=await recycler.RecycleAsync(result.Summary.RunId,target.Id,changedOptions,CancellationToken.None); Assert(!refused.Success && recorder.Calls==0,"changing directory roles invalidates old results");
    var outcome=await recycler.RecycleAsync(result.Summary.RunId,target.Id,o,CancellationToken.None); Assert(outcome.Success && recorder.Calls==1,"verified target reaches recycle-only adapter");
    result=await Scan(o); target=result.Catalog.Query(result.Summary.RunId,0,200).Rows.Single(r=>r.Role==RootRole.Target);
    var modified=File.GetLastWriteTimeUtc(Path.Combine(k,"a.bin")); Write(Path.Combine(k,"a.bin"),"BBBB"); File.SetLastWriteTimeUtc(Path.Combine(k,"a.bin"),modified);
    // Cached metadata remains identical, but recycling must rehash the retained copy.
    recycler=new(result.Catalog,recorder); outcome=await recycler.RecycleAsync(result.Summary.RunId,target.Id,o,CancellationToken.None); Assert(!outcome.Success && recorder.Calls==1 && File.Exists(target.Path),"fresh keeper hash catches preserved-timestamp changes");
    Write(Path.Combine(k,"a.bin"),"AAAA"); o.ForceHash=true; result=await Scan(o); target=result.Catalog.Query(result.Summary.RunId,0,200).Rows.Single(r=>r.Role==RootRole.Target);
    File.Delete(Path.Combine(k,"a.bin")); recycler=new(result.Catalog,recorder); outcome=await recycler.RecycleAsync(result.Summary.RunId,target.Id,o,CancellationToken.None); Assert(!outcome.Success && recorder.Calls==1,"missing keeper blocks recycling");
    var keeper=result.Catalog.Query(result.Summary.RunId,0,200).Rows.Single(r=>r.Role==RootRole.Keep); outcome=await recycler.RecycleAsync(result.Summary.RunId,keeper.Id,o,CancellationToken.None); Assert(!outcome.Success && File.Exists(target.Path),"keeper cannot be selected");
    Assert(!WindowsRecycleBin.IsRecycleFlag(0) && WindowsRecycleBin.IsRecycleFlag(0x80),"permanent-delete callback veto");
    var nativeBin=new WindowsRecycleBin(); outcome=await nativeBin.RecycleAsync(@"\\invalid-server\share\file.bin",()=>true,CancellationToken.None); Assert(!outcome.Success,"network location denied");
    outcome=await nativeBin.RecycleAsync(target.Path,()=>false,CancellationToken.None); Assert(!outcome.Success && File.Exists(target.Path),"validation failure never deletes");
    Log("PASS recycle role restrictions, current-content validation, changed/missing keeper, no permanent-delete path");

    o=Setup("changed-target",CompareMode.SHA256); k=o.Roots[0].Path; t=o.Roots[1].Path;
    Write(Path.Combine(k,"keep.bin"),"1234"); Write(Path.Combine(t,"target.bin"),"1234"); result=await Scan(o);
    target=result.Catalog.Query(result.Summary.RunId,0,200).Rows.Single(r=>r.Role==RootRole.Target); var oldTime=File.GetLastWriteTimeUtc(target.Path);
    Write(target.Path,"4321"); File.SetLastWriteTimeUtc(target.Path,oldTime); recorder=new(); recycler=new(result.Catalog,recorder);
    outcome=await recycler.RecycleAsync(result.Summary.RunId,target.Id,o,CancellationToken.None); Assert(!outcome.Success && recorder.Calls==0,"fresh target hash catches same-timestamp changes");
    o.ForceHash=true; result=await Scan(o); Assert(result.Summary.Targets==0,"changed target removed from results");
    Write(target.Path,"1234"); result=await Scan(o); target=result.Catalog.Query(result.Summary.RunId,0,200).Rows.Single(r=>r.Role==RootRole.Target);
    foreach(var failure in new[]{"回收站已禁用","容量不足","不支持回收","权限不足"})
    {
        result=await Scan(o); target=result.Catalog.Query(result.Summary.RunId,0,200).Rows.Single(r=>r.Role==RootRole.Target);
        outcome=await new RecyclingService(result.Catalog,new FailingBin(failure)).RecycleAsync(result.Summary.RunId,target.Id,o,CancellationToken.None);
        Assert(!outcome.Success && File.Exists(target.Path),"adapter failure preserves original: "+failure);
    }
    using(var locked=new FileStream(Path.Combine(k,"keep.bin"),FileMode.Open,System.IO.FileAccess.ReadWrite,FileShare.None))
    { result=await Scan(o); Assert(result.Summary.Errors==1 && result.Summary.Targets==0,"locked keeper logged and never matched"); }
    o.ForceHash=false; result=await Scan(o); Assert(result.Summary.Targets==1,"scan recovers after read errors");
    var oldRun=result.Summary.RunId; result=await Scan(o); Assert(result.Catalog.GetRow(oldRun,target.Id)==null,"old result snapshots pruned, fingerprints retained");
    Log("PASS target mutation, configuration invalidation, simulated recycle failures, read errors, old-snapshot cleanup");

    o=Setup("pause-cancel",CompareMode.SHA256); for(int i=0;i<80;i++){Write(Path.Combine(o.Roots[0].Path,$"{i}.bin"),new string('a',65536));Write(Path.Combine(o.Roots[1].Path,$"{i}.bin"),new string('a',65536));}
    var cat=new Catalog(o.CacheDirectory); var scanner=new Scanner(cat,new DiskMap()); scanner.Pause.Pause(); using var cts=new CancellationTokenSource(); var task=scanner.RunAsync(o,null,cts.Token); await Task.Delay(100); Assert(!task.IsCompleted,"pause blocks scanning"); cts.Cancel();
    try { await task; throw new Exception("Expected cancellation"); } catch(OperationCanceledException) { passed++; }
    result=await Scan(o); Assert(result.Summary.Targets==80,"cancelled run resumes by rediscovery"); Assert(cat.LastRun()?.State=="已完成","persistent task state");
    Log("PASS pause, cancel, resume, task persistence");

    o=Setup("parallel-directory-search"); k=o.Roots[0].Path; t=o.Roots[1].Path;
    Write(Path.Combine(k,"item.dat"),"x");
    for(int i=0;i<620;i++) Write(Path.Combine(t,$"folder-{i:D4}","item.dat"),"x");
    Directory.CreateDirectory(Path.Combine(t,"empty"));
    var searchMap=new DiskMap(); var searchDisk=searchMap.ForPath(t);
    foreach(int workers in new[]{1,4})
    {
        o.DiskSearchConcurrency[searchDisk.Key]=workers;
        var observed=new System.Collections.Concurrent.ConcurrentQueue<ScanProgress>();
        var observer=new InlineProgress<ScanProgress>(p=>observed.Enqueue(p));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var summary=await new Scanner(new Catalog(o.CacheDirectory),searchMap).RunAsync(o,observer,timeout.Token);
        Assert(summary.Files==621 && summary.Targets==620,"bounded directory queue drains without missing/duplicate files: "+workers);
        Assert(observed.All(p=>p.SearchWorkerLimit==workers && p.ActiveSearchWorkers>=0 && p.ActiveSearchWorkers<=workers),"search activity stays within per-disk limit");
        Assert(observed.All(p=>p.ActiveHashWorkers==0 && p.HashWorkerLimit==0),"name/size mode has no hash workers");
        Assert(observed.Last().ActiveSearchWorkers==0 && observed.Last().ActiveHashWorkers==0,"completed workers return to zero");
        Log($"SEARCH workers={workers}, peak sampled active={observed.Max(p=>p.ActiveSearchWorkers)}, files={summary.Files}");
    }
    o.Mode=CompareMode.SHA256; o.CpuConcurrency=2; o.DiskConcurrency[searchDisk.Key]=4;
    var pausedObserver=new System.Collections.Concurrent.ConcurrentQueue<ScanProgress>();
    scanner=new(new Catalog(o.CacheDirectory),searchMap); scanner.Pause.Pause();
    using(var pausedCancellation=new CancellationTokenSource())
    {
        var pausedTask=scanner.RunAsync(o,new InlineProgress<ScanProgress>(p=>pausedObserver.Enqueue(p)),pausedCancellation.Token);
        await Task.Delay(350);
        Assert(pausedObserver.Any() && pausedObserver.All(p=>p.ActiveSearchWorkers==0 && p.ActiveHashWorkers==0 && p.HashWorkerLimit==2),"paused work is not counted, CPU limit is respected");
        pausedCancellation.Cancel(); try{await pausedTask;}catch(OperationCanceledException){}
        Assert(pausedObserver.Last().ActiveSearchWorkers==0 && pausedObserver.Last().ActiveHashWorkers==0,"cancelled workers return to zero");
    }
    var fingerprintPath=Path.Combine(testRoot,"activity-hash.bin"); File.WriteAllBytes(fingerprintPath,new byte[3*1024*1024]);
    var hashGate=new PauseGate(); int hashActivity=0, blocks=0; var firstBlock=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var hashing=FileAccess.HashAsync(FileAccess.Stat(fingerprintPath),CompareMode.SHA256,hashGate,_=>{if(Interlocked.Increment(ref blocks)==1){hashGate.Pause();firstBlock.TrySetResult();}},CancellationToken.None,active=>Interlocked.Add(ref hashActivity,active?1:-1));
    await firstBlock.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(50);
    Assert(!hashing.IsCompleted && Volatile.Read(ref hashActivity)==0,"hash activity releases its count while paused between blocks");
    hashGate.Resume(); await hashing; Assert(hashActivity==0 && blocks==3,"hash activity is balanced after resume and completion");
    using(var hashCancellation=new CancellationTokenSource())
    {
        try { await FileAccess.HashAsync(FileAccess.Stat(fingerprintPath),CompareMode.SHA256,new PauseGate(),null,hashCancellation.Token,active=>{Interlocked.Add(ref hashActivity,active?1:-1);if(active)hashCancellation.Cancel();}); }
        catch(OperationCanceledException){}
        Assert(hashActivity==0,"hash activity is balanced on read cancellation");
    }
    var savedPath=Path.Combine(testRoot,"automatic-settings","settings.json");
    SettingsStore.Save(savedPath,o); var reopened=SettingsStore.Load(savedPath);
    Assert(reopened.Roots.Count==2 && reopened.Roots[0].Path==k && reopened.Roots[1].Role==RootRole.Target && reopened.DiskSearchConcurrency[searchDisk.Key]==4,"directory roles and worker settings survive restart before scanning");
    reopened.Roots.RemoveAt(1); reopened.Roots[0].Role=RootRole.Target; SettingsStore.Save(savedPath,reopened); reopened=SettingsStore.Load(savedPath);
    Assert(reopened.Roots.Count==1 && reopened.Roots[0].Role==RootRole.Target,"removed directories and role edits persist");
    Assert(!File.Exists(savedPath+".tmp"),"atomic settings save replaces temporary file");
    Log("PASS per-disk parallel search, bounded queue, live worker counts, pause/cancel, automatic directory persistence");

    if(args.Contains("--recycle-test"))
    {
        string path=Path.Combine(testRoot,"recycle-"+Guid.NewGuid().ToString("N")+".txt"); Write(path,"isolated recycle test"); var stamp=FileAccess.Stat(path);
        outcome=await nativeBin.RecycleAsync(path,()=>stamp.SameVersion(FileAccess.Stat(path)),CancellationToken.None);
        Assert(outcome.Success && !File.Exists(path),"native recycle operation: "+outcome.Message);
        await Native.RestoreFromRecycleAsync(path); Assert(File.ReadAllText(path)=="isolated recycle test","restore native recycled file"); Log("PASS native Windows recycle and restore (isolated file only)");
    }
    if(args.Contains("--benchmark"))
    {
        var b=Setup("benchmark"); var db=new Catalog(b.CacheDirectory); long run=db.StartRun(b); var watch=Stopwatch.StartNew();
        using(var conn=db.Open())
        {
            for(int batch=0;batch<1000;batch++)
            {
                var list=new List<IndexedFile>(1000);
                for(int i=0;i<1000;i++)
                {
                    long n=batch*1000L+i; string parent=b.Roots[n%2==0?0:1].Path; string name=$"{n/2:D9}.jpg"; string path=Path.Combine(parent,name);
                    list.Add(new(new(path,name,parent,4096,1,1,"synthetic:"+n),n%2==0?RootRole.Keep:RootRole.Target,null));
                }
                db.WriteBatch(conn,run,CompareMode.NameSize,list,[]);
            }
        }
        db.BuildGroups(run,b,CancellationToken.None); Log($"BENCH million-record index/group build: {watch.Elapsed.TotalSeconds:0.00}s"); watch.Restart();
        var p=db.Query(run,0,200); var q=db.Query(run,4999,200); var stats=db.TargetStats(run);
        Assert(p.Total==1_000_000 && q.Rows.Count==200 && stats.Count==500_000,"million-record database pagination");
        Log($"BENCH first/last page + totals: {watch.Elapsed.TotalMilliseconds:0}ms; working set {Formatting.Bytes(Process.GetCurrentProcess().WorkingSet64)}");
        Assert(Process.GetCurrentProcess().WorkingSet64<1024L*1024*1024,"working set below 1GiB for million rows");
        var perf=Setup("throughput",CompareMode.SHA256); var data=new byte[8*1024*1024]; new Random(42).NextBytes(data);
        for(int i=0;i<16;i++){File.WriteAllBytes(Path.Combine(perf.Roots[0].Path,$"{i}.bin"),data);File.WriteAllBytes(Path.Combine(perf.Roots[1].Path,$"{i}.bin"),data);}
        foreach(var concurrency in new[]{1,4})
        {
            var map=new DiskMap(); var disk=map.ForPath(perf.Roots[0].Path); perf.DiskConcurrency[disk.Key]=concurrency; perf.CpuConcurrency=concurrency; perf.ForceHash=true;
            watch.Restart(); var s=await new Scanner(new(perf.CacheDirectory),map).RunAsync(perf,null,CancellationToken.None); Log($"BENCH {concurrency} workers, full hashing {Formatting.Bytes(s.Bytes)} duplicates, 256MiB read: {watch.Elapsed.TotalSeconds:0.000}s");
        }
        perf.ForceHash=false; watch.Restart(); var warm=await Scan(perf); Log($"BENCH warm cache: {watch.Elapsed.TotalSeconds:0.000}s, hits {warm.Summary.CacheHits}");
    }
    Log($"PASS {passed} assertions. Fixtures and report: {testRoot}");
    File.WriteAllLines(Path.Combine(testRoot,"report.txt"),lines); return 0;
}
catch(Exception ex) { Log("FAIL "+ex); File.WriteAllLines(Path.Combine(testRoot,"report.txt"),lines); return 1; }

sealed class RecordingBin : IRecycleBin
{
    public int Calls;
    public Task<RecycleOutcome> RecycleAsync(string path,Func<bool> validate,CancellationToken token){if(!validate())return Task.FromResult(new RecycleOutcome(false,"validation"));Calls++;return Task.FromResult(new RecycleOutcome(true,"test adapter (source preserved)"));}
}
sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value)=>report(value);
}
sealed class FailingBin(string message) : IRecycleBin
{
    public Task<RecycleOutcome> RecycleAsync(string path,Func<bool> validate,CancellationToken token)=>Task.FromResult(new RecycleOutcome(false,message));
}
static class Native
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool CreateHardLinkW(string name,string existing,IntPtr security);
    public static Task RestoreFromRecycleAsync(string original)
    {
        var tcs=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var thread=new Thread(()=>
        {
            try
            {
                dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!; dynamic recycle=shell.NameSpace(10); dynamic items=recycle.Items();
                bool found=false;
                foreach(dynamic item in items)
                {
                    string name=Convert.ToString(item.ExtendedProperty("System.ItemNameDisplay")); string parent=Convert.ToString(item.ExtendedProperty("System.Recycle.DeletedFrom"));
                    if(name==Path.GetFileName(original) && string.Equals(parent,Path.GetDirectoryName(original),StringComparison.OrdinalIgnoreCase))
                    { dynamic destination=shell.NameSpace(Path.GetDirectoryName(original)); destination.MoveHere(item,4|16|1024); found=true; break; }
                }
                if(!found)throw new Exception("Isolated file not found in system recycle bin");
                for(int i=0;i<50 && !File.Exists(original);i++)Thread.Sleep(100);
                if(!File.Exists(original))throw new Exception("Restore did not complete"); tcs.SetResult();
            }
            catch(Exception ex){tcs.SetException(ex);}
        }); thread.SetApartmentState(ApartmentState.STA); thread.Start(); return tcs.Task;
    }
}
