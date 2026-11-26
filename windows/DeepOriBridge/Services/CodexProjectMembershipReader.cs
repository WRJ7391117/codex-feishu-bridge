using System.Runtime.InteropServices;

namespace DeepOriBridge.Services;

// The list API omits some empty/new Desktop threads. Supplement it with exact
// current project IDs from the same local state DB, never legacy UI assignments.
// Windows' SQLite API opens READONLY; this reader cannot rename/move/archive Tasks.
internal static class CodexProjectMembershipReader
{
    internal static IReadOnlySet<string> Read(string projectId, string? codexDirectory = null)
    {
        codexDirectory ??= Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        string? path = Directory.EnumerateFiles(codexDirectory, "state_*.sqlite")
            .Select(p => (Path: p, Version: int.TryParse(Path.GetFileNameWithoutExtension(p).AsSpan(6), out int n) ? n : -1))
            .Where(p => p.Version >= 0).OrderByDescending(p => p.Version).Select(p => p.Path).FirstOrDefault();
        if (path is null) throw new InvalidOperationException("Codex 当前项目数据库不可用；无法核实 Task 归属，请稍后刷新。");
        IntPtr db = IntPtr.Zero, statement = IntPtr.Zero;
        try
        {
            Require(sqlite3_open_v2(path, out db, 1, IntPtr.Zero)); // SQLITE_OPEN_READONLY
            Require(sqlite3_busy_timeout(db, 500));
            Require(sqlite3_prepare_v2(db,
                "SELECT id FROM threads WHERE (project_id = ?1 OR (?1 = '' AND project_id IS NULL)) AND archived = 0 ORDER BY updated_at DESC, id",
                -1, out statement, IntPtr.Zero));
            Require(sqlite3_bind_text(statement, 1, projectId, -1, new IntPtr(-1)));
            var ids = new HashSet<string>(StringComparer.Ordinal);
            int step;
            while ((step = sqlite3_step(statement)) == 100)
            {
                string? id = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, 0));
                if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
            }
            if (step != 101) Require(step);
            return ids;
        }
        finally
        {
            if (statement != IntPtr.Zero) sqlite3_finalize(statement);
            if (db != IntPtr.Zero) sqlite3_close(db);
        }
    }

    private static void Require(int code)
    {
        if (code != 0) throw new InvalidOperationException($"Codex 项目归属读取失败（SQLite {code}）；未使用旧映射替代。");
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_bind_text(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int bytes, IntPtr destructor);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
}
