using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace DedupDesk.Core;

public sealed class Catalog
{
    public string DirectoryPath { get; }
    private readonly string connectionString;
    public Catalog(string directory)
    {
        DirectoryPath = Paths.Normalize(directory); Directory.CreateDirectory(DirectoryPath);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(DirectoryPath, "catalog.db"), Pooling = true, DefaultTimeout = 60 }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS Runs(Id INTEGER PRIMARY KEY, Started TEXT NOT NULL, State TEXT NOT NULL, Options TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Fingerprints(Identity TEXT NOT NULL, Size INTEGER NOT NULL, Modified INTEGER NOT NULL, Created INTEGER NOT NULL, Algorithm INTEGER NOT NULL, Hash TEXT NOT NULL, PRIMARY KEY(Identity,Size,Modified,Created,Algorithm));
            CREATE TABLE IF NOT EXISTS Files(Id INTEGER PRIMARY KEY, RunId INTEGER NOT NULL, Path TEXT NOT NULL COLLATE NOCASE, Name TEXT NOT NULL, Parent TEXT NOT NULL COLLATE NOCASE, Size INTEGER NOT NULL, Modified INTEGER NOT NULL, Created INTEGER NOT NULL, Identity TEXT NOT NULL, Role INTEGER NOT NULL, Hash TEXT, MatchKey TEXT NOT NULL, Status TEXT NOT NULL DEFAULT '待处理', Links INTEGER NOT NULL DEFAULT 1, UNIQUE(RunId,Path));
            CREATE INDEX IF NOT EXISTS IX_Files_Match ON Files(RunId,MatchKey,Role,Identity);
            CREATE INDEX IF NOT EXISTS IX_Files_Parent ON Files(RunId,Parent);
            CREATE TABLE IF NOT EXISTS Groups(RunId INTEGER NOT NULL, MatchKey TEXT NOT NULL, PRIMARY KEY(RunId,MatchKey));
            CREATE TABLE IF NOT EXISTS Errors(Id INTEGER PRIMARY KEY, RunId INTEGER NOT NULL, Path TEXT NOT NULL, Message TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Operations(Id INTEGER PRIMARY KEY, RunId INTEGER NOT NULL, Path TEXT NOT NULL, Time TEXT NOT NULL, Success INTEGER NOT NULL, Message TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Folders(RunId INTEGER NOT NULL, Path TEXT NOT NULL COLLATE NOCASE, Parent TEXT NOT NULL COLLATE NOCASE, PRIMARY KEY(RunId,Path));
            CREATE INDEX IF NOT EXISTS IX_Folders_Parent ON Folders(RunId,Parent);
            """;
        cmd.ExecuteNonQuery();
        using var schema = c.CreateCommand(); schema.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Files') WHERE name='IsResult'";
        if ((long)schema.ExecuteScalar()! == 0)
        {
            schema.CommandText = "ALTER TABLE Files ADD COLUMN IsResult INTEGER NOT NULL DEFAULT 0;"; schema.ExecuteNonQuery();
            schema.CommandText = "UPDATE Files SET IsResult=1 WHERE EXISTS(SELECT 1 FROM Groups g WHERE g.RunId=Files.RunId AND g.MatchKey=Files.MatchKey) AND (Role=0 OR (Links=1 AND EXISTS(SELECT 1 FROM Files k WHERE k.RunId=Files.RunId AND k.MatchKey=Files.MatchKey AND k.Role=0 AND k.Identity<>Files.Identity)));"; schema.ExecuteNonQuery();
        }
        schema.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_Result_Group ON Files(RunId,IsResult,MatchKey,Role,Id);
            CREATE INDEX IF NOT EXISTS IX_Result_Name ON Files(RunId,IsResult,Name COLLATE NOCASE,Role,Id);
            CREATE INDEX IF NOT EXISTS IX_Result_Size ON Files(RunId,IsResult,Size,Role,Id);
            CREATE INDEX IF NOT EXISTS IX_Result_Path ON Files(RunId,IsResult,Path,Role,Id);
            CREATE INDEX IF NOT EXISTS IX_Result_Parent ON Files(RunId,IsResult,Parent,MatchKey,Role,Id);
            """;
        schema.ExecuteNonQuery();
    }
    public SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA cache_size=-16384; PRAGMA temp_store=FILE;"; cmd.ExecuteNonQuery(); return c; }
    private static SqliteCommand Command(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    public long StartRun(ScanOptions options)
    {
        using var c = Open(); using var cmd = Command(c, "INSERT INTO Runs(Started,State,Options) VALUES($time,'扫描中',$options); SELECT last_insert_rowid();", ("$time", DateTime.UtcNow.ToString("O")), ("$options", JsonSerializer.Serialize(options)));
        return (long)cmd.ExecuteScalar()!;
    }
    public void SetRunState(long run, string state) { using var c = Open(); using var cmd = Command(c, "UPDATE Runs SET State=$state WHERE Id=$run", ("$state", state), ("$run", run)); cmd.ExecuteNonQuery(); }
    public bool ConfigurationMatches(long run, ScanOptions options)
    {
        using var c = Open(); using var cmd = Command(c, "SELECT Options FROM Runs WHERE Id=$run AND State='已完成'", ("$run", run));
        if (cmd.ExecuteScalar() is not string json) return false;
        var saved = JsonSerializer.Deserialize<ScanOptions>(json)!;
        static string Key(ScanOptions o) => o.Mode + "|" + string.Join(";", o.Roots.Select(r => ((int)r.Role) + ":" + Paths.Normalize(r.Path).ToUpperInvariant()).Distinct().Order()) + "|" + (o.Extensions() is { } ext ? string.Join(";", ext.Select(e => e.ToUpperInvariant()).Order()) : "*");
        return Key(saved) == Key(options);
    }
    public void PruneOldResults(long completedRun)
    {
        // Fingerprints survive. Keep only the newest completed result set, not N full copies of a 20TB catalog.
        using var c = Open(); using var tx = c.BeginTransaction();
        using var cmd = Command(c, "DELETE FROM Files WHERE RunId<>$run; DELETE FROM Groups WHERE RunId<>$run; DELETE FROM Folders WHERE RunId<>$run; DELETE FROM Errors WHERE RunId<>$run; UPDATE Runs SET State='历史结果已清理' WHERE Id<>$run;", ("$run", completedRun)); cmd.Transaction = tx; cmd.ExecuteNonQuery(); tx.Commit();
    }
    public (long Id, string State, ScanOptions Options)? LastRun()
    {
        using var c = Open(); using var cmd = Command(c, "SELECT Id,State,Options FROM Runs ORDER BY Id DESC LIMIT 1"); using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetString(1), JsonSerializer.Deserialize<ScanOptions>(r.GetString(2))!) : null;
    }
    public string? CachedHash(SqliteConnection c, FileStamp s, CompareMode mode)
    {
        using var cmd = Command(c, "SELECT Hash FROM Fingerprints WHERE Identity=$id AND Size=$size AND Modified=$modified AND Created=$created AND Algorithm=$algo", ("$id", s.Identity), ("$size", s.Size), ("$modified", s.Modified), ("$created", s.Created), ("$algo", (int)mode));
        return cmd.ExecuteScalar() as string;
    }
    public void WriteBatch(SqliteConnection c, long run, CompareMode mode, IReadOnlyList<IndexedFile> files, IReadOnlyList<(string Path, string Message)> errors)
    {
        using var tx = c.BeginTransaction();
        using var insert = Command(c, "INSERT OR IGNORE INTO Files(RunId,Path,Name,Parent,Size,Modified,Created,Identity,Role,Hash,MatchKey,Links) VALUES($run,$path,$name,$parent,$size,$modified,$created,$identity,$role,$hash,$key,$links)");
        insert.Transaction = tx;
        foreach (var key in new[] { "$run", "$path", "$name", "$parent", "$size", "$modified", "$created", "$identity", "$role", "$hash", "$key", "$links" }) insert.Parameters.AddWithValue(key, "");
        using var hash = Command(c, "INSERT OR REPLACE INTO Fingerprints(Identity,Size,Modified,Created,Algorithm,Hash) VALUES($id,$size,$modified,$created,$algo,$hash)"); hash.Transaction = tx;
        foreach (var key in new[] { "$id", "$size", "$modified", "$created", "$algo", "$hash" }) hash.Parameters.AddWithValue(key, "");
        foreach (var f in files)
        {
            var s = f.Stamp;
            object?[] values = [run,s.Path,s.Name,s.Parent,s.Size,s.Modified,s.Created,s.Identity,(int)f.Role,f.Hash,MatchKey(s,mode,f.Hash),s.Links];
            for (int i = 0; i < values.Length; i++) insert.Parameters[i].Value = values[i] ?? DBNull.Value;
            insert.ExecuteNonQuery();
            if (f.Hash is null) continue;
            object[] hv = [s.Identity,s.Size,s.Modified,s.Created,(int)mode,f.Hash];
            for (int i = 0; i < hv.Length; i++) hash.Parameters[i].Value = hv[i];
            hash.ExecuteNonQuery();
        }
        foreach (var e in errors)
        {
            using var cmd = Command(c, "INSERT INTO Errors(RunId,Path,Message) VALUES($run,$path,$message)", ("$run", run), ("$path", e.Path), ("$message", e.Message)); cmd.Transaction = tx; cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public static string MatchKey(FileStamp s, CompareMode mode, string? hash) => mode == CompareMode.NameSize ? s.Size + ":" + s.Name.ToUpperInvariant() : s.Size + ":" + hash;
    public void BuildGroups(long run, ScanOptions options, CancellationToken token)
    {
        using var c = Open(); using var cmd = Command(c, """
            INSERT OR IGNORE INTO Groups(RunId,MatchKey)
            SELECT DISTINCT t.RunId,t.MatchKey FROM Files t
            WHERE t.RunId=$run AND t.Role=1 AND t.Links=1 AND t.Status='待处理'
            AND EXISTS(SELECT 1 FROM Files k WHERE k.RunId=t.RunId AND k.MatchKey=t.MatchKey AND k.Role=0 AND k.Identity<>t.Identity);
            """, ("$run", run));
        using var reg = token.Register(cmd.Cancel); cmd.ExecuteNonQuery(); token.ThrowIfCancellationRequested();
        cmd.CommandText = "UPDATE Files SET IsResult=1 WHERE RunId=$run AND MatchKey IN(SELECT MatchKey FROM Groups WHERE RunId=$run) AND (Role=0 OR (Links=1 AND EXISTS(SELECT 1 FROM Files k WHERE k.RunId=Files.RunId AND k.MatchKey=Files.MatchKey AND k.Role=0 AND k.Identity<>Files.Identity)));";
        cmd.ExecuteNonQuery(); token.ThrowIfCancellationRequested();
        // Store folder topology on disk; never build a full in-memory tree.
        using var read = Command(c, "SELECT DISTINCT Parent FROM Files WHERE RunId=$run AND IsResult=1", ("$run", run));
        using var reader = read.ExecuteReader();
        using var write = Open(); using var tx = write.BeginTransaction();
        using var folder = Command(write, "INSERT OR IGNORE INTO Folders(RunId,Path,Parent) VALUES($run,$path,$parent)", ("$run", run), ("$path", ""), ("$parent", "")); folder.Transaction = tx;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var path = reader.GetString(0);
            while (!string.IsNullOrEmpty(path))
            {
                var parent = Path.GetDirectoryName(path) ?? "";
                folder.Parameters["$path"].Value = path; folder.Parameters["$parent"].Value = parent;
                if (folder.ExecuteNonQuery() == 0) break;
                path = parent;
            }
        }
        tx.Commit();
    }
    private const string ResultFrom = " FROM Files f WHERE f.RunId=$run AND f.IsResult=1";
    private const string Columns = "SELECT f.Id,f.RunId,f.Path,f.Name,f.Parent,f.Size,f.Modified,f.Created,f.Identity,f.Role,f.Hash,f.MatchKey,f.Status,f.Links";
    private static ResultRow ReadRow(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), RunId = r.GetInt64(1), Stamp = new(r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetString(8), r.GetInt32(13)),
        Role = (RootRole)r.GetInt32(9), Hash = r.IsDBNull(10) ? null : r.GetString(10), GroupKey = r.GetString(11), Status = r.GetString(12)
    };
    public ResultPage Query(long run, int page, int pageSize, string search = "", string sort = "重复组", bool descending = false, string? parent = null)
    {
        using var c = Open(); var filter = " AND ($search='' OR instr(upper(f.Path),upper($search))>0)" + (parent is null ? "" : " AND f.Parent=$parent");
        using var count = Command(c, "SELECT COUNT(*)" + ResultFrom + filter, ("$run", run), ("$search", search), ("$parent", parent)); var total = (long)count.ExecuteScalar()!;
        var order = sort switch { "名称" => "f.Name COLLATE NOCASE", "大小" => "f.Size", "路径" => "f.Path", _ => "f.MatchKey" };
        using var cmd = Command(c, Columns + ResultFrom + filter + " ORDER BY " + order + (descending ? " DESC" : " ASC") + ",f.Role,f.Id LIMIT $limit OFFSET $offset", ("$run", run), ("$search", search), ("$parent", parent), ("$limit", pageSize), ("$offset", (long)page * pageSize));
        using var r = cmd.ExecuteReader(); var rows = new List<ResultRow>(); while (r.Read()) rows.Add(ReadRow(r));
        return new(rows, total);
    }
    public List<string> ChildFolders(long run, string parent, int offset = 0)
    {
        using var c = Open(); using var cmd = Command(c, "SELECT Path FROM Folders WHERE RunId=$run AND Parent=$parent ORDER BY Path LIMIT 200 OFFSET $offset", ("$run", run), ("$parent", parent), ("$offset", offset));
        using var r = cmd.ExecuteReader(); var rows = new List<string>(); while (r.Read()) rows.Add(r.GetString(0)); return rows;
    }
    public bool FolderExists(long run, string path)
    {
        using var c = Open(); using var cmd = Command(c, "SELECT 1 FROM Folders WHERE RunId=$run AND Path=$path", ("$run", run), ("$path", path)); return cmd.ExecuteScalar() is not null;
    }
    public ResultRow? GetRow(long run, long id)
    {
        using var c = Open(); using var cmd = Command(c, Columns + " FROM Files f WHERE f.RunId=$run AND f.Id=$id", ("$run", run), ("$id", id)); using var r = cmd.ExecuteReader(); return r.Read() ? ReadRow(r) : null;
    }
    public IEnumerable<ResultRow> Keepers(ResultRow target)
    {
        using var c = Open(); using var cmd = Command(c, Columns + " FROM Files f WHERE f.RunId=$run AND f.MatchKey=$key AND f.Role=0 AND f.Identity<>$id ORDER BY f.Path", ("$run", target.RunId), ("$key", target.GroupKey), ("$id", target.Stamp.Identity));
        using var r = cmd.ExecuteReader(); while (r.Read()) yield return ReadRow(r);
    }
    public (long Count, long Bytes) TargetStats(long run, string? subtree = null)
    {
        using var c = Open(); using var cmd = Command(c, "SELECT COUNT(*),COALESCE(SUM(f.Size),0)" + ResultFrom + " AND f.Role=1 AND f.Status='待处理' AND ($prefix='' OR substr(f.Path,1,length($prefix))=$prefix COLLATE NOCASE)", ("$run", run), ("$prefix", subtree is null ? "" : subtree.TrimEnd('\\') + "\\")); using var r = cmd.ExecuteReader(); r.Read(); return (r.GetInt64(0), r.GetInt64(1));
    }
    public List<long> TargetIds(long run, string subtree, long after = 0)
    {
        using var c = Open(); using var cmd = Command(c, "SELECT f.Id" + ResultFrom + " AND f.Role=1 AND f.Status='待处理' AND f.Id>$after AND substr(f.Path,1,length($prefix))=$prefix COLLATE NOCASE ORDER BY f.Id LIMIT 100", ("$run", run), ("$prefix", subtree.TrimEnd('\\') + "\\"), ("$after", after));
        using var r = cmd.ExecuteReader(); var ids = new List<long>(); while (r.Read()) ids.Add(r.GetInt64(0)); return ids;
    }
    public void RecordOperation(ResultRow row, bool success, string message)
    {
        using var c = Open(); using var cmd = Command(c, "INSERT INTO Operations(RunId,Path,Time,Success,Message) VALUES($run,$path,$time,$success,$message); UPDATE Files SET Status=$status WHERE Id=$id AND RunId=$run;", ("$run", row.RunId), ("$path", row.Path), ("$time", DateTime.UtcNow.ToString("O")), ("$success", success ? 1 : 0), ("$message", message), ("$status", success ? "已移入回收站" : "已跳过：" + message), ("$id", row.Id)); cmd.ExecuteNonQuery();
    }
    public string ErrorReport(long run)
    {
        using var c = Open(); using var cmd = Command(c, "SELECT Path,Message FROM Errors WHERE RunId=$run UNION ALL SELECT Path,Message FROM Operations WHERE RunId=$run AND Success=0 LIMIT 1000", ("$run", run)); using var r = cmd.ExecuteReader(); var text = new System.Text.StringBuilder(); while (r.Read()) text.AppendLine(r.GetString(0) + "\n  " + r.GetString(1)); return text.Length == 0 ? "没有错误记录。" : text.ToString();
    }
    public void ClearHashes() { using var c = Open(); using var cmd = Command(c, "DELETE FROM Fingerprints; PRAGMA wal_checkpoint(TRUNCATE);"); cmd.ExecuteNonQuery(); }
}
