using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;

namespace FlowRecord.Monitor;

public class Database {
    private static string AppDataDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowRecord"
        );

#if DEBUG
    private static string DbPath => Path.Combine(AppDataDir, "flowrecord.debug.db");
#else
    private static string DbPath => Path.Combine(AppDataDir, "flowrecord.db");
#endif
    public string ConnectionString { get; } = $"Data Source={DbPath}";

    public void Initialize() {
        Directory.CreateDirectory(AppDataDir);
        InitializeDatabase();
        RepairOrphanRecords();
    }

    private void InitializeDatabase() {
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();

        const string schema = @"
CREATE TABLE IF NOT EXISTS active_window (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    window_title TEXT NOT NULL,
    start_time TEXT NOT NULL,
    end_time TEXT NULL,
    created_at TEXT NOT NULL,
    last_seen TEXT NULL
);

CREATE TABLE IF NOT EXISTS boot_shutdown (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    boot_time TEXT NOT NULL,
    shutdown_time TEXT NULL,
    created_at TEXT NOT NULL,
    last_seen TEXT NULL
);

CREATE TABLE IF NOT EXISTS sleep_wake (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    sleep_time TEXT NULL,
    wake_time TEXT NULL,
    created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_active_window_start_time ON active_window (start_time DESC);
CREATE INDEX IF NOT EXISTS idx_boot_shutdown_boot_time ON boot_shutdown (boot_time DESC);

-- active_window.window_title にはプロセス名（例: Code）を記録し、表示用の名前（例: Visual Studio Code）はここで対応付ける
CREATE TABLE IF NOT EXISTS app_name (
    process_name TEXT PRIMARY KEY,
    display_name TEXT NOT NULL,
    icon BLOB NULL
);";

        using var cmd = new SqliteCommand(schema, conn);
        cmd.ExecuteNonQuery();

        // CREATE TABLE IF NOT EXISTS はテーブルが既にあると何もしないので、後から増やした列は既存の DB にここで追加する
        AddColumnIfMissing(conn, "app_name", "icon", "BLOB");
        AddColumnIfMissing(conn, "boot_shutdown", "last_seen", "TEXT");
        AddColumnIfMissing(conn, "active_window", "last_seen", "TEXT");
    }

    // pragma_table_info でテーブルの列一覧を調べ、指定した列が無ければ ALTER TABLE で追加する。
    // テーブル名・列名はパラメーターにできない（値にしか使えない）ので文字列に埋め込む。コード内の固定文字列だけを渡すこと
    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string definition) {
        using var check = new SqliteCommand("SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name = @column", conn);
        check.Parameters.AddWithValue("@table", table);
        check.Parameters.AddWithValue("@column", column);
        if ((long)check.ExecuteScalar()! == 1) return;

        using var alter = new SqliteCommand($"ALTER TABLE {table} ADD COLUMN {column} {definition}", conn);
        alter.ExecuteNonQuery();
    }

    private void RepairOrphanRecords() {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        try {
            using var conn = new SqliteConnection(ConnectionString);
            conn.Open();

            const string query = @"
UPDATE boot_shutdown SET shutdown_time = last_seen WHERE shutdown_time IS NULL AND last_seen IS NOT NULL;
UPDATE active_window SET end_time = last_seen WHERE end_time IS NULL AND last_seen IS NOT NULL;

-- 終了時刻が NULL のスリープ行は、復帰時刻を書く前に終わった孤立行。
-- 同じ起動セッションの中で、スリープ後に最初に始まったウィンドウの開始時刻を復帰時刻とみなす。
-- ウィンドウがなければ、セッションがスリープ後まで続いていたらその終了時刻（ロック画面のままシャットダウンなど）、
-- 続いていなければスリープ時刻（スリープ中の電源断）にする。
-- セッションの終了時刻を使うので、上の boot_shutdown の修復より後に実行する
UPDATE sleep_wake
SET wake_time = COALESCE(
        -- 復帰すると監視ループがすぐにウィンドウの記録を始めるので、最初のウィンドウの開始時刻が復帰時刻に近い。
        -- 次の起動のウィンドウを拾わないよう、session_end までに始まったものに限る
        (
            SELECT aw.start_time
            FROM active_window aw
            WHERE julianday(aw.start_time) >= julianday(sleep_wake.sleep_time)
                AND julianday(aw.start_time) <= julianday(s.session_end)
            ORDER BY julianday(aw.start_time)
            LIMIT 1
        ), CASE
            WHEN julianday(s.session_end) > julianday(sleep_wake.sleep_time) THEN s.session_end
            ELSE sleep_wake.sleep_time
        END
    )
-- 直す行（wake_time が NULL）ごとに、そのスリープを含む起動セッション（スリープ直前に起動したもの）の終了時刻を求める。
-- SET の 2 か所で使うので、ここで 1 回だけ計算して s.session_end として使い回す
FROM (
        SELECT sw.id,
            (
                SELECT bs.shutdown_time
                FROM boot_shutdown bs
                WHERE julianday(bs.boot_time) <= julianday(sw.sleep_time)
                ORDER BY julianday(bs.boot_time) DESC
                LIMIT 1
            ) AS session_end
        FROM sleep_wake sw
        WHERE sw.wake_time IS NULL
            AND sw.sleep_time IS NOT NULL
    ) AS s
WHERE sleep_wake.id = s.id;";

            using var cmd = new SqliteCommand(query, conn);
            cmd.ExecuteNonQuery();
        } catch (Exception ex) {
            // 修復に失敗しても記録は始められるので、起動は止めない
            Debug.WriteLine($"RepairOrphanRecords error: {ex}");
        }
    }
}
