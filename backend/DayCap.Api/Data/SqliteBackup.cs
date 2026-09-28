using Microsoft.Data.Sqlite;

namespace DayCap.Api.Data;

/// <summary>
/// 給主機的每日備份腳本用：<c>docker exec daycap-api-1 dotnet DayCap.Api.dll backup /tmp/daycap.db</c>。
/// 用 SQLite 的 VACUUM INTO 產生一致的快照——資料庫是 WAL 模式，直接複製 .db 檔可能漏掉
/// 還在 -wal 檔裡的交易；執行中的 API 同時在寫也沒關係。
/// </summary>
public static class SqliteBackup
{
    public static int Run(string dbPath, string target)
    {
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"database not found: {dbPath}");
            return 1;
        }
        if (File.Exists(target)) File.Delete(target); // VACUUM INTO 不會覆蓋既有檔案

        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $target";
        cmd.Parameters.AddWithValue("$target", target);
        cmd.ExecuteNonQuery();

        Console.WriteLine($"backup written: {target} ({new FileInfo(target).Length} bytes)");
        return 0;
    }
}
