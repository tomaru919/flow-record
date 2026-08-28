# Project Context: FlowRecord
パソコンの起動やシャットダウン、アクティブウィンドウを記録するアプリです。

## Database
Uses a local SQLite file (`%LocalAppData%\FlowRecord\flowrecord.db`, or `flowrecord.debug.db` in DEBUG builds) via `Microsoft.Data.Sqlite`. There is no `pc_name` table or `pc_name_id` column — each installation owns a single local database file, so a PC-identifying key is unnecessary. The schema is created automatically on startup (`MonitorService.InitializeDatabase`).

## Database Table Design
```sql
create table if not exists active_window (
  id integer primary key autoincrement,
  window_title text not null,
  start_time text not null,
  end_time text null,
  created_at text not null
);

create table if not exists boot_shutdown (
  id integer primary key autoincrement,
  boot_time text not null,
  shutdown_time text null,
  created_at text not null
);

create table if not exists sleep_wake (
  id integer primary key autoincrement,
  sleep_time text null,
  wake_time text null,
  created_at text not null
);

create index if not exists idx_active_window_start_time on active_window (start_time desc);
create index if not exists idx_boot_shutdown_boot_time on boot_shutdown (boot_time desc);
```
All timestamp columns are `TEXT`. `DateTime` values are bound directly as SQLite parameters (ISO-8601-like text), and read back or compared using SQLite's `julianday()`/`datetime()`/`date()` functions rather than Postgres `timestamp` arithmetic.

## Data Handling & Charting
- **Daily Activity Chart**: Displays the last 7 days of PC usage.
- **Missing Data**: Even if the PC was not turned on, the chart shows 0 hours for that day.
- **SQL Implementation**: Since SQLite has no `generate_series`, the 7-day date range is computed in C# (`GetDailyBootDurationJsonAsync`) and passed in as a `VALUES (...)` CTE (`ds`), then `LEFT JOIN`ed with `boot_shutdown`.
- **Session Duration**: Calculates the sum of durations for all boot/shutdown sessions within a single day. If a session is currently active (no `shutdown_time`), it uses `@now` (the current local time passed from the application) as the end time for calculation. Postgres's `LEAST`/`GREATEST` become SQLite's 2-argument scalar `MIN`/`MAX`, and `EXTRACT(EPOCH FROM ...)` becomes a `julianday()` difference (`* 24.0` for hours).

### Database Query Logic (MonitorService.cs)
```sql
WITH ds(date) AS (VALUES (@d0), (@d1), (@d2), (@d3), (@d4), (@d5), (@d6))
SELECT
    ds.date AS date,
    COALESCE(SUM(
        CASE WHEN bs.boot_time IS NOT NULL THEN
            (MIN(julianday(COALESCE(bs.shutdown_time, @now)), julianday(datetime(ds.date, '+1 day')))
             - MAX(julianday(bs.boot_time), julianday(ds.date))) * 24.0
        END
    ), 0) AS total_hours
FROM ds
LEFT JOIN boot_shutdown bs ON date(bs.boot_time) = ds.date
GROUP BY ds.date
ORDER BY ds.date ASC
```

## Documentation Guidelines
- **Session Recording**: EVERY session must be recorded in the "Session Log" section of this file. Each entry should include the date, a brief title, and a summary of changes/fixes.

## Session Log

### 2026-03-15: Fix Negative Usage Display on Startup
- **Issue**: Daily usage was displayed as a negative value when the app was first started for the day.
- **Cause**: PostgreSQL's `CURRENT_TIMESTAMP` and `CURRENT_DATE` were defaulting to UTC, which caused a mismatch when compared with the local time `boot_time` recorded by the C# application.
- **Fix**: Modified `MonitorService.GetDailyBootDurationJsonAsync` to pass local `DateTime.Now` and `DateTime.Today` as parameters (`@now`, `@today`) to the SQL query instead of relying on PostgreSQL's server-side current time.
- **Impact**: Ensures that usage calculations are always performed using the same local time reference as the recorded events.

### 2026-03-24: Improve Uptime Display and Typing
- **Change**: Updated the daily activity chart to display uptime in "XX時間YY分" (hours and minutes) format in the tooltip.
- **Localization**: Changed chart labels and axis titles to Japanese ("PC 稼働時間", "時間").
- **Typing**: Added `TooltipItem<'bar'>` from `chart.js` to provide proper typing for the tooltip callback context in `App.tsx`.
- **Logic**: Refined the calculation to `Math.round(value * 60)` before splitting into hours and minutes to ensure accuracy and avoid rounding errors (e.g., "60分").

### 2026-04-01: Modernize UI and Chart Style
- **Chart Aesthetics**: Updated to a solid blue (#36a2eb) with `borderRadius: 4`. Simplified X-axis labels to "M/D" format and added "h" suffix to Y-axis ticks.
- **Layout Redesign**: Moved the title and "Refresh" button to a flex header at the top for a cleaner dashboard hierarchy.
- **Dark Mode Support**: Added theme-aware styles for `chart-wrapper` and `table-wrapper`. In dark mode, card backgrounds use `#2b2b2b` with subtle shadows.
- **Component Refinement**: Removed redundant chart legends and titles. Increased overall max-width to 900px for better visibility.
- **Table Styling**: Updated table headers and rows with better spacing, borders, and colors that respond to the system theme.

### 2026-04-08: Fix Daily Usage Exceeding 24 Hours
- **Issue**: `total_hours` for a past day (e.g., 2026-04-06) showed values over 24 hours (e.g., 32.4h).
- **Cause**: For sessions with no `shutdown_time` (still active), the query used `@now` (current time) as the end time. Since `@now` is a future date relative to the session's start day, the duration spanned multiple days, all attributed to the boot day.
- **Fix**: Modified `GetDailyBootDurationJsonAsync` query to use `CASE WHEN bs.boot_time IS NOT NULL THEN ... END` inside `SUM`, capping end time with `LEAST(..., ds.date::timestamp + INTERVAL '1 day')` and start time with `GREATEST(bs.boot_time, ds.date::timestamp)`, ensuring each day's usage is bounded within that day's 24 hours and days with no boot record return 0.

### 2026-04-09: Fix Active Window Duration Showing Inflated Values
- **Issue**: Active window duration for a single window showed impossibly large values (e.g., 429 hours) in the pie chart.
- **Cause**: The query used `start_time < @tomorrow AND COALESCE(end_time, @now) > @today` to filter sessions. Old sessions with `end_time = NULL` (stale unclosed sessions) satisfied this condition and were each counted as "now − midnight today" hours. Multiple stale sessions for the same window title were summed together, producing enormous values.
- **Fix**: Restricted the WHERE clause to `start_time >= @today AND start_time < @tomorrow`, so only sessions that **started today** are included. Removed the `GREATEST(start_time, @today)` wrapper since `start_time` is already bounded by `@today`.
- **Impact**: Ensures the active window pie chart reflects only today's activity and is not polluted by stale historical sessions.

### 2026-04-18: Fix Sleep/Wake Time Recording Bug
- **Issue**: Sleep time was not recorded in `sleep_time`, and the wake time was incorrectly recorded in both `sleep_time` and `wake_time`. After fixing the event source, wake data was still not recorded.
- **Cause (1)**: `SessionSwitchReason.SessionLock` fires again when Windows shows the login screen after waking from sleep. This caused `RecordSleepAsync` to be called at wake time.
- **Cause (2)**: `PowerModes.Suspend` fires just before the system suspends, leaving no time for Supabase (network DB) writes to complete. As a result, no `sleep_wake` row existed in the DB, so `RecordWakeAsync` found nothing to update and returned early.
- **Fix**: 
  - Moved sleep/wake recording from `SessionSwitch` to `SystemEvents.PowerModeChanged`. `SessionLock` now only calls `StopMonitoringAsync`.
  - `RecordSleepAsync` now writes sleep_time to a local file (`sleep.txt`) synchronously before the network call — same pattern as `shutdown.txt`.
  - `RecordWakeAsync` reads `sleep.txt`, inserts a single row with both `sleep_time` and `wake_time`, then deletes the file. No longer relies on finding a pre-existing DB row.
- **Impact**: Sleep and wake times are reliably recorded even when the DB write on suspend does not complete.

### 2026-05-25: Add Sleep/Wake Recording to Database
- **Feature**: Sleep and wake events are now recorded to the `sleep_wake` table in the database.
- **New Table**: Added `sleep_wake (id, pc_name_id, sleep_time nullable, wake_time, created_at)`.
- **Implementation**:
  - `MonitorService.RecordSleep(DateTime)`: writes sleep time to `sleep.txt` synchronously (same pattern as `shutdown.txt`). DB write is deferred to wake time.
  - `MonitorService.RecordWakeAsync(DateTime)`: reads `sleep.txt` to retrieve sleep time, inserts one complete row into `sleep_wake`, then deletes the file.
  - `InsertSleepWakeRecordAsync`: inserts `(pc_name_id, sleep_time, wake_time, created_at)`. `sleep_time` is nullable in case the file is missing.
  - Both methods are called from `WndProc` via `PBT_APMSUSPEND` / `PBT_APMRESUMEAUTOMATIC`, guarded by `_isSleeping` flag to prevent double processing.
- **Cleanup**: Removed debug `Log()` method and `SleepLogPath` from `MainWindow.xaml.cs`; `sleep.txt` is now owned solely by `MonitorService`.

### 2026-04-06: Add Active Window Distribution Pie Chart
- **Feature**: Added a pie chart to visualize the distribution of active window time for the current day.
- **Backend**: Implemented `GetActiveWindowDurationJsonAsync` in `MonitorService.cs` using a PostgreSQL query that calculates durations within the boundaries of "today" (00:00 to 23:59).
- **Frontend**: 
    - Integrated `Pie` chart component and registered `ArcElement`.
    - Created a responsive dual-chart layout (Bar chart for 7-day history, Pie chart for daily breakdown).
    - Enhanced tooltips to display time in "XX時間YY分" format along with the percentage of the total active time.
- **Styling**: Added `.charts-container` and refined `.chart-wrapper` to support side-by-side display on desktop and vertical stacking on mobile.

### 2026-08-28: Migrate Database from PostgreSQL (Supabase) to Local SQLite
- **Change**: Replaced Npgsql/Supabase with `Microsoft.Data.Sqlite`, storing data in a local file (`%LocalAppData%\FlowRecord\flowrecord.db`, `flowrecord.debug.db` for DEBUG builds) instead of a remote Postgres database.
- **Schema**: Removed the `pc_name` table and every `pc_name_id` foreign key column from `active_window`, `boot_shutdown`, and `sleep_wake` — a local SQLite file only ever holds one PC's data, so the PC-identity join was no longer needed. `EnsurePcNameIdAsync` and related lookup/insert logic were deleted from `MonitorService.cs`.
- **Schema creation**: The app now creates its own schema on startup (`MonitorService.InitializeDatabase`, run synchronously from `Initialize()`) instead of relying on SQL run manually against Supabase.
- **Query rewrites**: Postgres-specific SQL (`generate_series`, `EXTRACT(EPOCH FROM ...)`, `LEAST`/`GREATEST`, `RETURNING id`, interval arithmetic) was replaced with SQLite equivalents — a `VALUES` CTE built in C# for the 7-day range, `julianday()` differences for duration math, 2-argument scalar `MIN`/`MAX`, and `SELECT last_insert_rowid()` after inserts (Microsoft.Data.Sqlite has no `LastInsertRowId` connection property).
- **Removed**: `Npgsql` and `DotNetEnv` package references, the `.env` file's Supabase/production connection settings, and `NpgsqlConnection.ClearAllPools()` (a Postgres-network-specific workaround for stale connections after sleep — not applicable to a local SQLite file).
- **Dependency note**: `Microsoft.Data.Sqlite` 10.0.1 pulls in `SQLitePCLRaw.bundle_e_sqlite3` 2.1.11, which has a known high-severity advisory (GHSA-2m69-gcr7-jv3q); pinned an explicit `PackageReference` to `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5 in `FlowRecord.csproj` to resolve it.
- **Not done**: Existing production data in Supabase was not migrated/exported — this change only switches the app to a fresh local database going forward.

### 2026-08-28 (2): Write Sleep/Shutdown Times Directly to the Database
- **Change**: Removed the `sleep.txt` / `shutdown.txt` staging files. Sleep and OS-shutdown/logoff times are now written straight to the local SQLite database at the moment they occur, instead of being buffered to a text file and reconciled on the next boot.
- **Reason**: The file-buffering pattern existed because writes to the old network-hosted Supabase database could not reliably complete within the short time window before suspend/forced process termination. A local SQLite write is a fast, synchronous, non-network operation, so that safety net is no longer needed.
- **`RecordSleep(DateTime)`**: No longer static; now inserts a `sleep_wake` row synchronously with `sleep_time` set and `wake_time` left `NULL`, keeping the new row's id in `_sleepWakeId`. If `_sleepWakeId` is already set (an unconfirmed sleep is pending — e.g. a Modern Standby suspend/resume blip), it does nothing, preserving the original sleep time.
- **`RecordWakeAsync(DateTime)`**: No longer reads/deletes a file or inserts a new row; it now `UPDATE`s the pending row (`_sleepWakeId`) with `wake_time`, then clears `_sleepWakeId`. Still only called after `ScheduleWakeConfirmation`'s 5-second debounce (see the 2026-04-18/2026-05-25 entries) confirms the resume wasn't itself a blip.
- **`sleep_wake.wake_time`**: Changed from `NOT NULL` to nullable, since a row is now created at sleep time before the wake time is known.
- **`RecordShutdownSync(DateTime)`** (new, `MonitorService.cs`): Synchronous counterpart to `RecordShutdownAsync`, called from `App.xaml.cs`'s `OnSessionEnding` (via a new `MainWindow.RecordShutdownSync` passthrough) instead of appending to `shutdown.txt`. Updates the current `boot_shutdown` row's `shutdown_time` directly using a synchronous `SqliteConnection`.
- **Removed**: `ShutdownLogPath`, `SleepLogPath`, `ApplyShutdownLogToLastBootRecordAsync` (and its call from `Start()`), `InsertSleepWakeRecordAsync`, and `App.xaml.cs`'s `LogPath`/`AppendLine` helpers.
- **Known tradeoff**: If `RecordShutdownSync` doesn't finish before the process is force-killed during a real OS shutdown (should be rare — it's a single local file write), that boot session's `shutdown_time` stays `NULL` permanently, since there is no longer a next-boot reconciliation step. `GetDailyBootDurationJsonAsync` already caps such "still open" sessions to at most 24h/day, so this can't reproduce the multi-day inflation bug from 2026-04-08, but it would make each day since then show as fully active until the row is manually corrected.

