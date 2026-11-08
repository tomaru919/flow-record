using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace FlowRecord.Monitor;

// 今進行中の行の ID。集計で終了時刻が NULL の行を @now まで延長してよいかの判定に使う
public sealed record CurrentRecordIds(long? BootShutdownId, long? WindowRecordId, long? SleepWakeId);

public sealed record ActivityRecord(string WindowTitle, string EventType, string StartTime, string EndTime);
public sealed record BootDuration(string Date, double TotalHours, double SleepHours);
public sealed record HourlyUsage(int Hour, double UsageHours);
public sealed record ActiveWindowDuration(string WindowTitle, double DurationHours, string? Icon);

// 画面用の集計（読み取りだけ）。状態は持たず、今進行中の ID と現在時刻は引数で受け取る。
// 結果はデータで返し、WebView2 に送る JSON にするのは MainWindow 側
public class UsageQueries(string connectionString) {
    public async Task<List<ActivityRecord>> GetRecordsAsync() {
        try {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();
            const string query = @"
SELECT 'active_window' AS event_type,
    COALESCE(n.display_name, aw.window_title) AS window_title,
    aw.start_time,
    aw.end_time
FROM active_window aw
    LEFT JOIN app_name n ON n.process_name = aw.window_title
UNION ALL
SELECT 'startup' AS event_type,
    'system' AS window_title,
    bs.boot_time AS start_time,
    NULL AS end_time
FROM boot_shutdown bs
UNION ALL
SELECT 'shutdown' AS event_type,
    'system' AS window_title,
    bs.shutdown_time AS start_time,
    NULL AS end_time
FROM boot_shutdown bs
WHERE bs.shutdown_time IS NOT NULL
ORDER BY start_time DESC
LIMIT 100";
            await using var cmd = new SqliteCommand(query, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            var results = new List<ActivityRecord>();
            while (await reader.ReadAsync()) {
                results.Add(new ActivityRecord(
                    WindowTitle: reader["window_title"].ToString() ?? "",
                    EventType: reader["event_type"].ToString() ?? "",
                    StartTime: reader["start_time"].ToString() ?? "",
                    EndTime: reader["end_time"] == DBNull.Value ? "" : reader["end_time"].ToString() ?? ""
                ));
            }
            return results;
        } catch (Exception ex) {
            Debug.WriteLine($"GetRecordsAsync error: {ex.Message}");
            return [];
        }
    }

    public async Task<List<BootDuration>> GetDailyBootDurationsAsync(int weekOffset, CurrentRecordIds ids, DateTime now) {
        try {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();

            // 日曜日始まりの週の開始日を計算し、weekOffset 週分ずらす
            var today = now.Date;
            var currentWeekSunday = today.AddDays(-(int)today.DayOfWeek);
            var weekStart = currentWeekSunday.AddDays(weekOffset * 7);
            var dates = Enumerable.Range(0, 7).Select(i => weekStart.AddDays(i)).ToArray();

            var dateParamNames = string.Join(", ", Enumerable.Range(0, 7).Select(i => $"(@d{i})"));

            // @now まで稼働中とみなしてよいのは ids.BootShutdownId の現在のセッション（スリープは ids.SleepWakeId）だけ。
            // 他の shutdown_time/wake_time が NULL の行は孤立（クラッシュ等）のため boot_time/sleep_time にフォールバックする。
            // total_hours（boot_time〜shutdown_time）にはスリープしていた時間も含まれてしまうため、
            // sleep_hours を別途集計し、フロント側で total_hours - sleep_hours を「実際に使用していた時間」として表示する
            var query = $@"
WITH ds(date) AS (
    VALUES {dateParamNames}
)
SELECT ds.date AS date,
    COALESCE(
        (
            SELECT SUM(
                    (
                        MIN(
                            julianday(
                                COALESCE(
                                    bs.shutdown_time,
                                    CASE
                                        WHEN bs.id = @currentBootId THEN @now
                                        ELSE bs.boot_time
                                    END
                                )
                            ),
                            julianday(datetime(ds.date, '+1 day'))
                        ) - MAX(julianday(bs.boot_time), julianday(ds.date))
                    ) * 24.0
                )
            FROM boot_shutdown bs
            WHERE julianday(bs.boot_time) < julianday(datetime(ds.date, '+1 day'))
                AND julianday(
                    COALESCE(
                        bs.shutdown_time,
                        CASE
                            WHEN bs.id = @currentBootId THEN @now
                            ELSE bs.boot_time
                        END
                    )
                ) >= julianday(ds.date)
        ),
        0
    ) AS total_hours,
    COALESCE(
        (
            SELECT SUM(
                    (
                        MIN(
                            julianday(
                                COALESCE(
                                    sw.wake_time,
                                    CASE
                                        WHEN sw.id = @currentSleepId THEN @now
                                        ELSE sw.sleep_time
                                    END
                                )
                            ),
                            julianday(datetime(ds.date, '+1 day'))
                        ) - MAX(julianday(sw.sleep_time), julianday(ds.date))
                    ) * 24.0
                )
            FROM sleep_wake sw
            WHERE sw.sleep_time IS NOT NULL
                AND julianday(sw.sleep_time) < julianday(datetime(ds.date, '+1 day'))
                AND julianday(
                    COALESCE(
                        sw.wake_time,
                        CASE
                            WHEN sw.id = @currentSleepId THEN @now
                            ELSE sw.sleep_time
                        END
                    )
                ) >= julianday(ds.date)
        ),
        0
    ) AS sleep_hours
FROM ds
ORDER BY ds.date ASC";

            await using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@currentBootId", (object?)ids.BootShutdownId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@currentSleepId", (object?)ids.SleepWakeId ?? DBNull.Value);
            for (int i = 0; i < dates.Length; i++) {
                cmd.Parameters.AddWithValue($"@d{i}", dates[i].ToString("yyyy-MM-dd"));
            }
            await using var reader = await cmd.ExecuteReaderAsync();
            var results = new List<BootDuration>();
            while (await reader.ReadAsync()) {
                var totalHours = Convert.ToDouble(reader["total_hours"]);
                var sleepHours = Math.Min(Convert.ToDouble(reader["sleep_hours"]), totalHours);
                results.Add(new BootDuration(
                    Date: reader["date"].ToString() ?? "",
                    TotalHours: Math.Round(totalHours, 2),
                    SleepHours: Math.Round(sleepHours, 2)
                ));
            }
            return results;
        } catch (Exception ex) {
            Debug.WriteLine($"GetDailyBootDurationsAsync error: {ex.Message}");
            return [];
        }
    }

    // day の日付の使用時間を 0〜23 時の 1 時間ごとに集計する。
    // 週のグラフと同じく「起動していた時間 - スリープしていた時間」を使用時間とし、各行を 1 時間の枠に切り詰めて足す
    public async Task<List<HourlyUsage>> GetHourlyUsageAsync(DateTime day, CurrentRecordIds ids, DateTime now) {
        day = day.Date;
        try {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();

            var hourParamNames = string.Join(", ", Enumerable.Range(0, 24).Select(i => $"({i}, @h{i})"));

            // NULL の終了時刻の扱いは GetDailyBootDurationsAsync と同じ。
            // julianday(hs.hour_start) 以降に終わった行だけを対象にするので、まだ来ていない時間帯は 0 になる
            var query = $@"
WITH hs(hour, hour_start) AS (
    VALUES {hourParamNames}
)
SELECT hs.hour AS hour,
    COALESCE(
        (
            SELECT SUM(
                    (
                        MIN(
                            julianday(
                                COALESCE(
                                    bs.shutdown_time,
                                    CASE
                                        WHEN bs.id = @currentBootId THEN @now
                                        ELSE bs.boot_time
                                    END
                                )
                            ),
                            julianday(datetime(hs.hour_start, '+1 hour'))
                        ) - MAX(julianday(bs.boot_time), julianday(hs.hour_start))
                    ) * 24.0
                )
            FROM boot_shutdown bs
            WHERE julianday(bs.boot_time) < julianday(datetime(hs.hour_start, '+1 hour'))
                AND julianday(
                    COALESCE(
                        bs.shutdown_time,
                        CASE
                            WHEN bs.id = @currentBootId THEN @now
                            ELSE bs.boot_time
                        END
                    )
                ) >= julianday(hs.hour_start)
        ),
        0
    ) AS total_hours,
    COALESCE(
        (
            SELECT SUM(
                    (
                        MIN(
                            julianday(
                                COALESCE(
                                    sw.wake_time,
                                    CASE
                                        WHEN sw.id = @currentSleepId THEN @now
                                        ELSE sw.sleep_time
                                    END
                                )
                            ),
                            julianday(datetime(hs.hour_start, '+1 hour'))
                        ) - MAX(julianday(sw.sleep_time), julianday(hs.hour_start))
                    ) * 24.0
                )
            FROM sleep_wake sw
            WHERE sw.sleep_time IS NOT NULL
                AND julianday(sw.sleep_time) < julianday(datetime(hs.hour_start, '+1 hour'))
                AND julianday(
                    COALESCE(
                        sw.wake_time,
                        CASE
                            WHEN sw.id = @currentSleepId THEN @now
                            ELSE sw.sleep_time
                        END
                    )
                ) >= julianday(hs.hour_start)
        ),
        0
    ) AS sleep_hours
FROM hs
ORDER BY hs.hour ASC";

            await using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@currentBootId", (object?)ids.BootShutdownId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@currentSleepId", (object?)ids.SleepWakeId ?? DBNull.Value);
            for (int i = 0; i < 24; i++) {
                cmd.Parameters.AddWithValue($"@h{i}", day.AddHours(i).ToString("yyyy-MM-dd HH:mm:ss"));
            }

            await using var reader = await cmd.ExecuteReaderAsync();
            var results = new List<HourlyUsage>();
            while (await reader.ReadAsync()) {
                var usageHours = Convert.ToDouble(reader["total_hours"]) - Convert.ToDouble(reader["sleep_hours"]);
                results.Add(new HourlyUsage(
                    Hour: Convert.ToInt32(reader["hour"]),
                    UsageHours: Math.Round(Math.Max(0, usageHours), 4)
                ));
            }
            return results;
        } catch (Exception ex) {
            Debug.WriteLine($"GetHourlyUsageAsync error: {ex.Message}");
            return [];
        }
    }

    // dayOffset: 0 が今日、-1 が昨日
    public async Task<List<ActiveWindowDuration>> GetActiveWindowDurationsAsync(int dayOffset, CurrentRecordIds ids, DateTime now) {
        try {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync();

            // 対象日の0時から翌日の0時までの範囲で計算
            var dayStart = now.Date.AddDays(dayOffset);
            var dayEnd = dayStart.AddDays(1);

            // @now まで表示中とみなしてよいのは ids.WindowRecordId の行だけ。他の end_time NULL 行は孤立（強制終了等）のため start_time にフォールバックする。
            // 表示名（app_name）があればそれで集計し、なければ記録したプロセス名のまま表示する
            const string query = @"
SELECT COALESCE(n.display_name, aw.window_title) AS window_title,
    SUM(
        (
            julianday(
                MIN(
                    COALESCE(
                        aw.end_time,
                        CASE
                            WHEN aw.id = @currentWindowRecordId THEN @now
                            ELSE aw.start_time
                        END
                    ),
                    @dayEnd
                )
            ) - julianday(aw.start_time)
        ) * 24.0
    ) AS duration_hours,
    MAX(n.icon) AS icon
FROM active_window aw
    LEFT JOIN app_name n ON n.process_name = aw.window_title
WHERE aw.start_time >= @dayStart
    AND aw.start_time < @dayEnd
GROUP BY COALESCE(n.display_name, aw.window_title)
HAVING duration_hours > 0
ORDER BY duration_hours DESC";

            await using var cmd = new SqliteCommand(query, conn);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@dayStart", dayStart);
            cmd.Parameters.AddWithValue("@dayEnd", dayEnd);
            cmd.Parameters.AddWithValue("@currentWindowRecordId", (object?)ids.WindowRecordId ?? DBNull.Value);

            await using var reader = await cmd.ExecuteReaderAsync();
            var results = new List<ActiveWindowDuration>();
            while (await reader.ReadAsync()) {
                results.Add(new ActiveWindowDuration(
                    WindowTitle: reader["window_title"].ToString() ?? "",
                    DurationHours: Math.Round(Convert.ToDouble(reader["duration_hours"]), 4),
                    Icon: reader["icon"] is byte[] bytes
                        ? "data:image/png;base64," + Convert.ToBase64String(bytes)
                        : null
                ));
            }
            return results;
        } catch (Exception ex) {
            Debug.WriteLine($"GetActiveWindowDurationsAsync error: {ex.Message}");
            return [];
        }
    }
}
