using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DedupDesk.Core;

public enum CompareMode { NameSize, MD5, SHA256 }
public enum RootRole { Keep, Target }
public sealed class ScanRoot
{
    public string Path { get; set; } = "";
    public RootRole Role { get; set; }
    public string RoleLabel { get => Role == RootRole.Keep ? "保留目录" : "待清理目录"; set => Role = value == "待清理目录" ? RootRole.Target : RootRole.Keep; }
}
public sealed class ScanOptions
{
    public List<ScanRoot> Roots { get; set; } = [];
    public CompareMode Mode { get; set; }
    public string Filter { get; set; } = "全部文件";
    public string PhotoExtensions { get; set; } = "jpg jpeg png gif bmp tif tiff webp heic heif avif dng cr2 cr3 nef nrw arw rw2 orf raf pef srw";
    public string VideoExtensions { get; set; } = "mp4 m4v mov mkv avi wmv flv webm mpg mpeg ts mts m2ts 3gp vob";
    public string CustomExtensions { get; set; } = "";
    public string CacheDirectory { get; set; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DedupDesk");
    public int CpuConcurrency { get; set; } = Environment.ProcessorCount;
    public Dictionary<string, int> DiskConcurrency { get; set; } = [];
    public bool ForceHash { get; set; }
    public HashSet<string>? Extensions() => Filter switch
    {
        "照片" => ParseExtensions(PhotoExtensions), "视频" => ParseExtensions(VideoExtensions),
        "照片和视频" => ParseExtensions(PhotoExtensions + " " + VideoExtensions),
        "自定义扩展名" => ParseExtensions(CustomExtensions), _ => null
    };
    public static HashSet<string> ParseExtensions(string input) => input.Split([';', ',', ' ', '\t', '\r', '\n', '；', '，'], StringSplitOptions.RemoveEmptyEntries)
        .Select(x => "." + x.Trim().TrimStart('*', '.')).Where(x => x.Length > 1).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public ScanOptions Clone() => JsonSerializer.Deserialize<ScanOptions>(JsonSerializer.Serialize(this))!;
    public void Validate()
    {
        if (!Roots.Any(r => r.Role == RootRole.Keep) || !Roots.Any(r => r.Role == RootRole.Target)) throw new InvalidOperationException("请至少添加一个保留目录和一个待清理目录。");
        foreach (var root in Roots)
        {
            root.Path = Paths.Normalize(root.Path);
            if (!Directory.Exists(root.Path)) throw new DirectoryNotFoundException("目录不可用：" + root.Path);
            if (Paths.HasReparseAncestor(root.Path)) throw new IOException("扫描根目录不能是链接或位于链接内：" + root.Path);
        }
        CacheDirectory = Paths.Normalize(CacheDirectory);
        if (Roots.Any(r => Paths.IsWithin(r.Path, CacheDirectory))) throw new InvalidOperationException("扫描目录不能位于应用缓存内。");
        if (Extensions() is { Count: 0 }) throw new InvalidOperationException("请输入至少一个有效扩展名。");
        if (CpuConcurrency < 1 || CpuConcurrency > 1024) throw new InvalidOperationException("计算并发必须为 1–1024。");
    }
}
public static class Paths
{
    public static string Normalize(string path) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    public static bool IsWithin(string path, string parent) => string.Equals(path, parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    public static bool HasReparseAncestor(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = System.IO.Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
    public static RootRole RoleFor(string path, IEnumerable<ScanRoot> roots) => roots.Any(r => r.Role == RootRole.Keep && IsWithin(path, r.Path)) ? RootRole.Keep : RootRole.Target;
}
public sealed record FileStamp(string Path, string Name, string Parent, long Size, long Modified, long Created, string Identity, int Links = 1)
{
    public bool SameVersion(FileStamp other) => Identity == other.Identity && Size == other.Size && Modified == other.Modified && Created == other.Created;
}
public sealed record IndexedFile(FileStamp Stamp, RootRole Role, string? Hash);
public sealed record ScanSummary(long RunId, long Files, long Hashed, long CacheHits, long Errors, long Targets, long Bytes);
public sealed record ScanProgress(long Files, long Hashed, long CacheHits, long Errors, long BytesRead, string Stage);
public sealed record ResultPage(IReadOnlyList<ResultRow> Rows, long Total);
public sealed class ResultRow : INotifyPropertyChanged
{
    public long Id { get; init; }
    public long RunId { get; init; }
    public FileStamp Stamp { get; init; } = null!;
    public RootRole Role { get; init; }
    public string? Hash { get; init; }
    public string GroupKey { get; init; } = "";
    public string Status { get; set; } = "";
    public string Name => Stamp.Name;
    public string Path => Stamp.Path;
    public long Size => Stamp.Size;
    public string SizeLabel => Formatting.Bytes(Size);
    public string ComparisonLabel => Hash is null ? "名字＋大小" : Hash.Length == 32 ? "MD5" : "SHA-256";
    public string GroupLabel => Hash is null ? GroupKey : Hash[..12];
    public string RoleLabel => Role == RootRole.Keep ? "保留" : "待清理";
    public bool CanSelect => Role == RootRole.Target && Status == "待处理";
    private bool selected;
    public bool Selected { get => selected; set { selected = CanSelect && value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public static class Formatting
{
    public static string Bytes(long bytes)
    {
        double n = bytes; string[] units = ["B", "KB", "MB", "GB", "TB", "PB"]; int i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return $"{n:0.##} {units[i]}";
    }
    public static string Mode(CompareMode mode) => mode == CompareMode.NameSize ? "名字＋大小（未核验内容）" : mode.ToString();
}
public sealed class PauseGate
{
    private TaskCompletionSource source = Completed();
    private static TaskCompletionSource Completed() { var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); t.SetResult(); return t; }
    public bool IsPaused => !source.Task.IsCompleted;
    public void Pause() { if (!IsPaused) source = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void Resume() => source.TrySetResult();
    public Task WaitAsync(CancellationToken token) => source.Task.WaitAsync(token);
}
