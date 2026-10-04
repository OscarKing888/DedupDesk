using System.Runtime.InteropServices;

namespace DedupDesk.Core;

public sealed record RecycleOutcome(bool Success, string Message);
public interface IRecycleBin
{
    Task<RecycleOutcome> RecycleAsync(string path, Func<bool> validate, CancellationToken token);
}
public sealed class RecyclingService(Catalog catalog, IRecycleBin recycleBin)
{
    public async Task<RecycleOutcome> RecycleAsync(long run, long id, ScanOptions options, CancellationToken token)
    {
        var row = catalog.GetRow(run, id);
        if (row is null) return new(false, "结果已不存在");
        if (!catalog.ConfigurationMatches(run, options)) return new(false, "扫描未完成或配置已变化，请重新扫描");
        RecycleOutcome result;
        try
        {
            if (!row.CanSelect || Paths.RoleFor(row.Path, options.Roots) != RootRole.Target || !options.Roots.Any(r => r.Role == RootRole.Target && Paths.IsWithin(row.Path, r.Path))) throw new IOException("该文件不在允许清理的目录中");
            if (Paths.HasReparseAncestor(row.Path)) throw new IOException("路径包含链接，已拒绝回收");
            var current = FileAccess.Stat(row.Path);
            if (!current.SameVersion(row.Stamp) || current.Links != 1) throw new IOException("目标文件已变化或属于硬链接，请重扫");
            // Hold target against writes, but allow the Shell to move it into the Recycle Bin.
            using var targetLock = new FileStream(row.Path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read | FileShare.Delete);
            var pause = new PauseGate();
            var targetHash = options.Mode == CompareMode.NameSize ? null : await FileAccess.HashAsync(current, options.Mode, pause, null, token);
            if (options.Mode != CompareMode.NameSize && targetHash != row.Hash) throw new IOException("目标内容已变化，请重扫");
            result = new(false, "没有仍然有效的保留副本");
            foreach (var keeper in catalog.Keepers(row))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (Paths.RoleFor(keeper.Path, options.Roots) != RootRole.Keep || Paths.HasReparseAncestor(keeper.Path)) continue;
                    // Keep this handle open until the Shell operation finishes: no writes or deletion of the retained copy.
                    using var keepLock = new FileStream(keeper.Path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
                    var kept = FileAccess.Stat(keeper.Path);
                    if (!kept.SameVersion(keeper.Stamp) || kept.Identity == current.Identity || kept.Size != current.Size) continue;
                    if (options.Mode == CompareMode.NameSize)
                    {
                        if (!string.Equals(kept.Name, current.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    else if (await FileAccess.HashAsync(kept, options.Mode, pause, null, token) != targetHash) continue;
                    result = await recycleBin.RecycleAsync(row.Path, () => current.SameVersion(FileAccess.Stat(row.Path)) && kept.SameVersion(FileAccess.Stat(keeper.Path)) && !Paths.HasReparseAncestor(row.Path), token);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result = new(false, ex.Message); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { result = new(false, ex.Message); }
        catalog.RecordOperation(row, result.Success, result.Message); return result;
    }
}

public sealed class WindowsRecycleBin : IRecycleBin
{
    public Task<RecycleOutcome> RecycleAsync(string path, Func<bool> validate, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<RecycleOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            IFileOperation? operation = null; IShellItem? item = null;
            try
            {
                token.ThrowIfCancellationRequested();
                if (path.StartsWith(@"\\") || new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
                    throw new IOException("仅允许支持系统回收站的本地固定磁盘；此位置不执行回收");
                if (!validate()) throw new IOException("文件状态变化，已取消回收");
                operation = (IFileOperation)new FileOperationCom();
                // RECYCLEONDELETE + no error UI + early failure: never offer a permanent-delete fallback.
                Check(operation.SetOperationFlags(0x00080000 | 0x00100000 | 0x0400 | 0x2000 | 0x1000 | 0x20000000 | 0x0004));
                var iid = typeof(IShellItem).GUID; Check(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
                var sink = new RecycleSink(validate, token);
                Check(operation.DeleteItem(item, sink)); Check(operation.PerformOperations()); Check(operation.GetAnyOperationsAborted(out bool aborted));
                tcs.TrySetResult(!aborted && sink.Recycled ? new(true, "已移入回收站") : new(false, sink.Message));
            }
            catch (OperationCanceledException) { tcs.TrySetCanceled(token); }
            catch (Exception ex) { tcs.TrySetResult(new(false, ex.Message)); }
            finally { if (item is not null) Marshal.FinalReleaseComObject(item); if (operation is not null) Marshal.FinalReleaseComObject(operation); }
        }) { IsBackground = true, Name = "DedupDesk 回收站 STA" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return tcs.Task;
    }
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    public static bool IsRecycleFlag(uint flags) => (flags & 0x80) != 0;
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class RecycleSink(Func<bool> validate, CancellationToken token) : IFileOperationProgressSink
    {
        public bool Recycled { get; private set; }
        public string Message { get; private set; } = "系统未确认文件已进入回收站，操作已停止";
        public int PreDeleteItem(uint flags, IShellItem item)
        {
            try
            {
                if (token.IsCancellationRequested || !IsRecycleFlag(flags) || !validate()) { Message = "系统未提供回收操作或文件已变化，已阻止删除"; return unchecked((int)0x80004004); }
                return 0;
            }
            catch { Message = "回收前核验失败，已阻止删除"; return unchecked((int)0x80004004); }
        }
        public int PostDeleteItem(uint flags, IShellItem item, int hr, IShellItem? created)
        {
            Recycled = hr >= 0 && IsRecycleFlag(flags) && created is not null;
            if (hr < 0) Message = Marshal.GetExceptionForHR(hr)?.Message ?? $"回收失败：{hr:X8}";
            return 0;
        }
        public int StartOperations() => 0;
        public int FinishOperations(int hr) => 0;
        public int PreRenameItem(uint f, IShellItem i, string n) => 0;
        public int PostRenameItem(uint f, IShellItem i, string n, int hr, IShellItem? c) => 0;
        public int PreMoveItem(uint f, IShellItem i, IShellItem d, string n) => 0;
        public int PostMoveItem(uint f, IShellItem i, IShellItem d, string n, int hr, IShellItem? c) => 0;
        public int PreCopyItem(uint f, IShellItem i, IShellItem d, string n) => 0;
        public int PostCopyItem(uint f, IShellItem i, IShellItem d, string n, int hr, IShellItem? c) => 0;
        public int PreNewItem(uint f, IShellItem d, string n) => 0;
        public int PostNewItem(uint f, IShellItem d, string n, string t, uint a, int hr, IShellItem? c) => 0;
        public int UpdateProgress(uint total, uint complete) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHCreateItemFromParsingName(string path, IntPtr bind, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [ComImport, Guid("3AD05575-8857-4850-9277-11B85BDB8E09")] private class FileOperationCom { }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid iid, out IntPtr ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdn, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr owner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
        [PreserveSig] int MoveItems(IntPtr items, IShellItem dest);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
        [PreserveSig] int CopyItems(IntPtr items, IShellItem dest);
        [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IShellItem dest, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, IFileOperationProgressSink? sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int hr);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem? created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem? created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem? created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int hr, IShellItem? created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem dest, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int hr, IShellItem? created);
        [PreserveSig] int UpdateProgress(uint total, uint complete);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
}
