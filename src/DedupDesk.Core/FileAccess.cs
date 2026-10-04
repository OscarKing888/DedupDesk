using Microsoft.Win32.SafeHandles;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DedupDesk.Core;

public static class FileAccess
{
    [StructLayout(LayoutKind.Sequential)] private struct HandleInfo
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out HandleInfo info);
    public static FileStamp Stat(string path)
    {
        var f = new FileInfo(path); f.Refresh();
        if (!f.Exists) throw new FileNotFoundException("文件已不存在", path);
        if ((f.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new IOException("跳过链接或目录");
        var identity = "PATH:" + Paths.Normalize(path).ToUpperInvariant(); int links = 1;
        using var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (!handle.IsInvalid && GetFileInformationByHandle(handle, out var info))
        {
            if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0) throw new IOException("跳过链接");
            if ((info.IndexHigh | info.IndexLow) != 0 && info.Volume != 0)
                identity = $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
            links = checked((int)info.Links);
        }
        return new(Paths.Normalize(path), f.Name, Paths.Normalize(f.DirectoryName!), f.Length, f.LastWriteTimeUtc.Ticks, f.CreationTimeUtc.Ticks, identity, links);
    }
    public static async Task<string> HashAsync(FileStamp stamp, CompareMode mode, PauseGate pause, Action<int>? progress, CancellationToken token, Action<bool>? working = null)
    {
        using var stream = new FileStream(stamp.Path, FileMode.Open, FileAccessMode, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(mode == CompareMode.MD5 ? HashAlgorithmName.MD5 : HashAlgorithmName.SHA256);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            while (true)
            {
                await pause.WaitAsync(token);
                working?.Invoke(true);
                try
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(), token); if (n == 0) break;
                    hash.AppendData(buffer, 0, n); progress?.Invoke(n);
                }
                finally { working?.Invoke(false); }
            }
            if (!stamp.SameVersion(Stat(stamp.Path))) throw new IOException("文件在读取过程中发生变化，请重扫");
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }
    private const System.IO.FileAccess FileAccessMode = System.IO.FileAccess.Read;
}

public sealed class DiskInfo
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Volumes { get; set; } = "";
    public int Concurrency { get; set; } = 1;
    public int SearchConcurrency { get; set; } = 2;
}
public sealed class DiskMap
{
    private readonly Dictionary<string, DiskInfo> volumes = new(StringComparer.OrdinalIgnoreCase);
    public List<DiskInfo> Disks { get; } = [];
    public static DiskMap Discover()
    {
        var map = new DiskMap(); var media = new Dictionary<int, (int Media, int Bus)>();
        try
        {
            using var q = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DeviceId,MediaType,BusType FROM MSFT_PhysicalDisk");
            foreach (ManagementObject item in q.Get()) { using (item) { if (int.TryParse(item["DeviceId"]?.ToString(), out var id)) media[id] = (Convert.ToInt32(item["MediaType"]), Convert.ToInt32(item["BusType"])); } }
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or COMException) { }
        try
        {
            using var q = new ManagementObjectSearcher("SELECT DeviceID,Index,Model FROM Win32_DiskDrive");
            foreach (ManagementObject disk in q.Get())
            {
                using (disk)
                {
                    var index = Convert.ToInt32(disk["Index"]); var kind = media.GetValueOrDefault(index);
                    var info = new DiskInfo { Key = "disk:" + index, Name = $"磁盘 {index} · {disk["Model"]}", Concurrency = kind.Bus == 17 ? 8 : kind.Media == 4 ? 4 : 1, SearchConcurrency = kind.Bus == 17 ? 8 : kind.Media == 4 ? 4 : 2 };
                    var names = new List<string>();
                    foreach (ManagementObject partition in disk.GetRelated("Win32_DiskPartition"))
                        using (partition) foreach (ManagementObject logical in partition.GetRelated("Win32_LogicalDisk"))
                            using (logical) { string name = logical["DeviceID"] + "\\"; map.volumes[name] = info; names.Add(name); }
                    info.Volumes = string.Join("  ", names); map.Disks.Add(info);
                }
            }
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or COMException) { }
        return map;
    }
    public DiskInfo ForPath(string path)
    {
        var volume = Path.GetPathRoot(path) ?? path;
        if (volumes.TryGetValue(volume, out var info)) return info;
        var network = volume.StartsWith(@"\\");
        info = new() { Key = volume, Name = network ? "网络目录" : "未知设备", Volumes = volume, Concurrency = network ? 2 : 1 };
        volumes[volume] = info; Disks.Add(info); return info;
    }
}
