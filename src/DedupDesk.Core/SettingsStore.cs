using System.Text.Json;

namespace DedupDesk.Core;

public static class SettingsStore
{
    public static ScanOptions Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<ScanOptions>(File.ReadAllText(path)) ?? new()
        : new();

    public static void Save(string path, ScanOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
