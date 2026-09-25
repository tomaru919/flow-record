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
`@currentBootId` is `_bootShutdownId`, the in-memory id of the session actually running right now (set once at startup by `CreateBootRecordAsync`). Only that row may extrapolate its open-ended duration to `@now`; any other row with `shutdown_time IS NULL` is an orphan (the process was killed/crashed without reaching `RecordShutdownSync`/`RecordShutdownAndStopAsync`) and falls back to its own `boot_time` (0 hours) instead — see the 2026-09-12 (2) Session Log entry for why this matters.
```sql
WITH ds(date) AS (VALUES (@d0), (@d1), (@d2), (@d3), (@d4), (@d5), (@d6))
SELECT
    ds.date AS date,
    COALESCE(SUM(
        CASE WHEN bs.boot_time IS NOT NULL THEN
            (MIN(julianday(COALESCE(bs.shutdown_time, CASE WHEN bs.id = @currentBootId THEN @now ELSE bs.boot_time END)), julianday(datetime(ds.date, '+1 day')))
             - MAX(julianday(bs.boot_time), julianday(ds.date))) * 24.0
        END
    ), 0) AS total_hours
FROM ds
LEFT JOIN boot_shutdown bs
    ON julianday(bs.boot_time) < julianday(datetime(ds.date, '+1 day'))
   AND julianday(COALESCE(bs.shutdown_time, CASE WHEN bs.id = @currentBootId THEN @now ELSE bs.boot_time END)) >= julianday(ds.date)
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

### 2026-08-30: Fix Layout Clipping on Small Windows
- **Issue**: When the app window was smaller than full screen, the header (title + Refresh button) was invisible/cut off and both scrollbars appeared in odd positions. Full-screen (maximized) display looked fine.
- **Cause**: `frontend/src/index.css`'s leftover Vite template styling had `body { display: flex; place-items: center; min-height: 100vh; }`. When page content is taller than the viewport, flex's vertical centering pushes the overflow equally above and below the viewport — the top overflow (the header) becomes inaccessible because a page can only be scrolled toward the bottom overflow, not the top.
- **Fix**: Removed `display: flex; place-items: center;` from `body` in `index.css`. Horizontal centering of `.container` is already handled independently via `margin: 0 auto` + `max-width: 1200px` in `App.css`, so visual centering is unaffected — only the vertical-centering-induced clipping is gone.

### 2026-08-30 (2): Fix Sleep/Wake Not Detected on Some Machines
- **Issue**: On some machines (a Modern Standby-only laptop, and separately a desktop that supports S3), suspending/resuming produced no `sleep_wake` DB row and no `Debug.WriteLine` output at all — as if the suspend/resume handlers never ran.
- **Cause**: The only detection mechanism was `RegisterSuspendResumeNotification` + `WM_POWERBROADCAST` (`PBT_APMSUSPEND`/`PBT_APMRESUMEAUTOMATIC`), which is the legacy notification for traditional ACPI sleep (S1–S4). `powercfg /a` on the Modern-Standby-only laptop showed no S1–S4 support at all (Standby (S0 Low Power Idle) only), so those messages never fire there. Surprisingly, when the same fix was tested on an S3-capable desktop, diagnostic logging showed `PBT_APMSUSPEND`/`PBT_APMRESUMEAUTOMATIC` never fired there either — only the away-mode notification below did — meaning the legacy notification is unreliable even on S3 hardware on this Windows build, not just on Modern Standby machines.
- **Fix (`MainWindow.xaml.cs`)**: Added a second, Microsoft-recommended detection path via `RegisterPowerSettingNotification` with `GUID_SYSTEM_AWAYMODE` (`98a7f580-01f7-48aa-9c0f-44352c29e5c0`). `WM_POWERBROADCAST` with `wParam = PBT_POWERSETTINGCHANGE` (0x8013) carries a `POWERBROADCAST_SETTING` struct in `lParam`; `Data == 1` means entering away mode (sleep), `0` means leaving (resume). Both this and the original `PBT_APMSUSPEND`/`RESUMEAUTOMATIC` path now funnel into shared `HandleSuspend()`/`HandleResume()` methods, guarded by the existing `_isSleeping` flag so whichever notification fires first (or both) doesn't cause double-recording.
- **Diagnostics**: Added `MonitorService.LogPower(string)`, which appends timestamped lines to `%LocalAppData%\FlowRecord\power.log` — unlike `Debug.WriteLine` (invisible unless a debugger is attached), this file is inspectable after a real sleep/resume test on any machine. It confirmed the root cause (`wParam=0x8013`/`PBT_POWERSETTINGCHANGE` fired; `PBT_APMSUSPEND`/`RESUMEAUTOMATIC` never did, even on the S3-capable desktop) and that the fix worked (DB rows recorded correctly). Initially removed after confirming the fix, then reinstated (per user request) since sleep/wake detection has repeatedly broken across machine/Windows-version combinations and is hard to reproduce/debug locally otherwise. Logs registration handle results (with `GetLastWin32Error` on failure) in `OnSourceInitialized`, the raw `wParam` (and which `PBT_*` case matched, including `PBT_APMSUSPEND`/`PBT_APMRESUMEAUTOMATIC`) of every `WM_POWERBROADCAST` in `WndProc`, and every call/skip-reason/result in `RecordSleep`/`ScheduleWakeConfirmation`/`RecordWakeAsync`.

### 2026-09-12: Fix Daily Activity Chart Missing Data for Days After an Overnight Session
- **Issue**: When FlowRecord ran continuously across midnight (PC left on, no reboot), the daily activity chart showed 0 hours for the second day even though the PC was actually in use.
- **Cause**: `GetDailyBootDurationJsonAsync`'s query joined `boot_shutdown` to each calendar day with `ON date(bs.boot_time) = ds.date` — an exact match on the session's *boot* date only. A session that started on day 1 and was still open (or was shut down) on day 2 has `boot_time` dated day 1, so it never matched day 2's row, leaving that day with no `bs` match and therefore 0 hours, regardless of how long the session had actually been running into day 2.
- **Fix**: Changed the join to an overlap test — `ON julianday(bs.boot_time) < julianday(datetime(ds.date, '+1 day')) AND julianday(COALESCE(bs.shutdown_time, @now)) >= julianday(ds.date)` — so a session matches every day its active range `[boot_time, COALESCE(shutdown_time, @now)]` overlaps, not just its start day. The existing `MIN`/`MAX`-based clamping in the `SELECT` (unchanged, from the 2026-04-08 fix) already correctly bounds each day's contribution to that day's 24 hours once the row is matched.
- **Impact**: A session spanning multiple days (or more than two) now contributes the correct partial hours to every day it overlaps, including the still-open final day.

### 2026-09-12 (2): Fix Past Days Showing Over 24 Hours
- **Issue**: Immediately after the fix above, past days started showing impossible totals (e.g. 27時間 for a single day), confirmed via `sqlite3` against `%LocalAppData%\FlowRecord\flowrecord.debug.db`.
- **Cause**: An orphaned `boot_shutdown` row from an earlier dev session (killed via `taskkill` mid-build rather than exiting cleanly) had `shutdown_time IS NULL` and was never reconciled — a known, previously-accepted tradeoff from the 2026-08-28 (2) entry. Under the *old* exact-day join, an orphan like this only ever inflated its own boot day. The new overlap join (see the fix above) is correct for genuinely-still-running sessions, but it applied `COALESCE(shutdown_time, @now)` to *any* NULL-shutdown row — so this months-old orphan now matched and contributed a full day's worth of hours to *every* day between its boot date and today, stacking on top of that day's real session data.
- **Fix**: `GetDailyBootDurationJsonAsync` now takes a `@currentBootId` parameter (the in-memory `_bootShutdownId` of the session actually running right now). Both the `SELECT`'s duration calc and the `JOIN`'s overlap test replace the bare `COALESCE(bs.shutdown_time, @now)` with `COALESCE(bs.shutdown_time, CASE WHEN bs.id = @currentBootId THEN @now ELSE bs.boot_time END)` — only the current session may extrapolate to `@now`; any other orphaned row falls back to its own `boot_time`, contributing 0 hours instead of spreading phantom uptime forward indefinitely. No data migration needed — existing orphan rows are neutralized by this fallback automatically.
- **Verified**: Ran the corrected query directly against the live `flowrecord.debug.db` with `sqlite3` before committing to code — the previously-27h day dropped to the correct ~4.3h once the orphan row (id 5, boot 2026-08-30 with no shutdown) stopped being treated as still open.

### 2026-09-12 (3): Fix Active Window Chart Showing Hours Not Reflected in Daily Activity Chart
- **Issue**: On the production DB (`flowrecord.db`), the active-window pie chart showed "Code" at 11時間5分 (90.7%) for today, while the daily activity bar chart showed only ~1.5h of PC usage for that same day.
- **Cause**: Identical root cause to the 2026-09-12 (2) entry, but in `active_window` instead of `boot_shutdown`. `GetActiveWindowDurationJsonAsync`'s query used `COALESCE(end_time, @now)` for *any* row with `end_time IS NULL`, assuming any such row is the currently-displayed window. In practice, an earlier FlowRecord process instance was killed (e.g. via `taskkill` during a rebuild) while "Code" was the foreground window, leaving that row's `end_time` permanently `NULL` — confirmed via `sqlite3` (row start `07:12`, no `end_time`). The next process instance has no in-memory knowledge of that old row, so it sat there indefinitely; each time `GetActiveWindowDurationJsonAsync` ran, it computed "now − 07:12" (~11h) for that stale row and summed it in with today's real (much shorter) window-switch data.
- **Fix**: `GetActiveWindowDurationJsonAsync` now takes a `@currentWindowRecordId` parameter (the in-memory `_currentWindowRecordId` of the window actually being tracked right now). `COALESCE(end_time, @now)` became `COALESCE(end_time, CASE WHEN id = @currentWindowRecordId THEN @now ELSE start_time END)` in both the `SELECT` and `HAVING` clauses — only the currently-tracked row may extrapolate to `@now`; any other orphaned row falls back to its own `start_time` (0 duration).
- **Verified**: Ran the corrected query directly against the live `flowrecord.db` with `sqlite3` before committing to code — "Code" dropped from ~11.28h to ~0.18h, and `chrome` (the actual largest real contributor) became the top entry.

### 2026-09-14: Add Sleep Time Breakdown to Daily Activity Chart
- **Issue**: `total_hours` in the daily activity chart (`boot_time` to `shutdown_time`/`@now`) counts sleep periods within a boot session as "PC usage", since suspend/resume doesn't end the session.
- **Fix (`MonitorService.cs`)**: `GetDailyBootDurationJsonAsync` now also computes `sleep_hours` per day via a correlated subquery against `sleep_wake`, mirroring the existing `boot_shutdown` overlap/clamp logic (day-range overlap test, `MIN`/`MAX` clamping to the day's 24h, and a `@currentSleepId` parameter — the in-memory `_sleepWakeId` — so only the currently-pending sleep may extrapolate to `@now`; any orphaned NULL-`wake_time` row falls back to its own `sleep_time`, contributing 0, same reasoning as `@currentBootId`). Two separate correlated subqueries (one per table) are used instead of two `LEFT JOIN`s to avoid a cross-product between overlapping `boot_shutdown` and `sleep_wake` rows. `sleep_hours` is clamped to `total_hours` defensively before being returned.
- **Frontend**: `BootDuration` gained `sleep_hours`. `DailyActivityChart.tsx` changed from a single-dataset bar chart to a stacked bar chart (`使用時間` = `total_hours - sleep_hours` in blue, stacked with `スリープしていた時間` = `sleep_hours` in green on top), with the legend re-enabled to distinguish the two segments.
- **Impact**: The bar's total height (`total_hours`) is unchanged, but the "usage" portion now excludes time spent asleep, and that excluded time is visible as its own segment instead of silently disappearing.

### 2026-09-14 (2): Exclude Sleep Time from Active Window Durations
- **Issue**: Reported as an inconsistency — the active window chart showed "Code" at ~10 minutes while the (just-fixed) daily activity chart's usage time for the same day was only ~9 minutes, i.e. a single window appeared to exceed total PC usage time.
- **Cause**: Same underlying bug as the 2026-09-14 entry above, but in `GetActiveWindowDurationJsonAsync`, which had never accounted for `sleep_wake` at all. The monitoring loop is frozen for the duration of a real suspend, so if the foreground window is unchanged across a sleep/resume cycle, the `active_window` row spanning that switch silently includes the entire sleep duration as if the window were actively used. Confirmed via `sqlite3`: row id 577 ("Code", `15:20:00.46`–`15:25:17.44`, ~5m17s) fully contained sleep_wake row id 20 (`15:20:08.97`–`15:25:07.80`, ~4m59s). Once the daily activity chart started correctly subtracting sleep from its total, this pre-existing per-window inflation became visible as an apparent contradiction between the two charts instead of two charts that were both (consistently) wrong in the same direction.
- **First attempt (reverted)**: Subtracted each `active_window` row's overlap with `sleep_wake` inside `GetActiveWindowDurationJsonAsync`'s query (a `row_durations` CTE with a per-row correlated subquery). It worked (row 577 dropped from ~5m17s to ~18s) but corrected the data at read time instead of preventing the bad row from being written.
- **Final fix — close the row at sleep time**: `GetActiveWindowDurationJsonAsync`'s query was reverted to its 2026-09-12 (3) form. Instead, `MainWindow.HandleSuspend` now calls the new `MonitorService.SuspendMonitoring(sleepTime)`, which synchronously sets the current `active_window` row's `end_time` to the sleep time (`CloseActiveWindowSync`), clears `currentWindow`/`_currentWindowRecordId`, and sets `_isSuspended`. `HandleResume` calls `ResumeMonitoring()` (clears `_isSuspended`) immediately — not after the 5-second wake debounce — so the next loop tick opens a fresh row starting at resume time.
- **Why the loop must also pause**: Just closing the row isn't enough — `MonitoringLoop` keeps ticking for up to ~1s before the real suspend (and may keep running under Modern Standby, where desktop processes aren't guaranteed to be frozen). It would see the same foreground window vs. the now-empty `currentWindow` and immediately open a new row that spans the sleep again. `MonitoringLoop` now skips detection while `_isSuspended` is true.
- **Thread safety**: `currentWindow`/`_currentWindowRecordId` are mutated both by the background loop and by `SuspendMonitoring` on the UI thread, so the loop body and `SuspendMonitoring` are serialized with a new `_windowLock` (`SemaphoreSlim`; `WaitAsync` in the loop, blocking `Wait` in `SuspendMonitoring` — safe because the loop's awaits run on the thread pool, not the UI thread).
- **Data**: The debug DB was reset by the user, so no existing rows spanning a sleep needed correction; rows recorded before this fix (if any remained) would still include sleep time, since the query no longer compensates.

### 2026-09-21: Extract Power Logging into Its Own Class
- **Change**: Moved `MonitorService.LogPower(string)` and its `PowerLogPath` field out into a new file, `FlowRecord/PowerLogger.cs` — `namespace FlowRecord.Logging`, `public static class PowerLogger`, method `PowerLogger.Log(string)`. All call sites in `MonitorService.cs` and `MainWindow.xaml.cs` now use `PowerLogger.Log(...)`.
- **Debug/Release split**: The log file now follows the same convention as the database — `power.debug.log` in DEBUG builds, `power.log` in Release, both under `%LocalAppData%\FlowRecord`. Previously both build configurations wrote to the same `power.log`.
- **Naming note**: The namespace (`FlowRecord.Logging`) deliberately differs from the class name (`PowerLogger`). An earlier attempt used `namespace FlowRecord.Log` with `class Log`, which made the unqualified name `Log` resolve to the *namespace* (a nested namespace of the file's own `FlowRecord` namespace) rather than the type, so `Log.LogPower(...)` failed to compile from `MainWindow.xaml.cs`.
- **Duplication accepted**: `PowerLogger` computes `%LocalAppData%\FlowRecord` itself rather than sharing `MonitorService.AppDataDir`, keeping the logger free of any dependency on `MonitorService`.


### 2026-09-21 (2): Run `Initialize`/`Start` from the `MonitorService` Constructor
- **Change**: `MonitorService` now has a public constructor that calls `Initialize()` then `Start()`; both methods became `private`. `MainWindow`'s constructor is reduced to `_monitorService = new MonitorService();`.
- **Reason**: `Start` depends on the `connectionString` that `Initialize` sets, and `MainWindow` was the only caller, always invoking them back-to-back in that order. Folding them into the constructor removes the possibility of a missed call or wrong ordering.
- **Tradeoff**: Constructing a `MonitorService` now starts the background monitoring loop as a side effect, which would make standalone instantiation (e.g. in tests) harder. Accepted since there is a single instance, created once in `MainWindow`.

### 2026-09-23: Use Display State to Confirm a Real Wake
- **Issue**: A sleep at 13:23:30 that actually lasted until 13:27:06 was recorded as waking at 13:23:35 — five seconds after the sleep. Only one occurrence out of several test sleeps.
- **Cause**: Windows' Kernel-Power event log showed the machine entered Modern Standby at 13:23:30, *exited* it at 13:23:32, and re-entered at 13:23:32, staying asleep until 13:27:06. The app received `PBT_APMRESUMEAUTOMATIC` for that 2-second exit (delivered at 13:23:35), but **no `PBT_APMSUSPEND` for the immediate re-entry**, so `ScheduleWakeConfirmation`'s 5-second debounce (which only cancels when a suspend notification arrives) was never cancelled and confirmed the wake at 13:23:40. `power.debug.log` also shows the process running at 13:23:35–13:23:40, i.e. after standby had already resumed — under Modern Standby the process is throttled but not hard-frozen, so its timers can still fire. The real 13:27:06 resume produced no notification at all, so the ~3.5 minutes of standby were counted as usage.
- **Fix (`MainWindow.xaml.cs`)**: Registered a third power-setting notification, `GUID_CONSOLE_DISPLAY_STATE` (`6fe69556-704a-47a0-8f24-c28d936fda47`), tracked in `_isDisplayOn` (`Data` 0 = off, 1 = on, 2 = dimmed/treated as on). `HandleResume` now returns early while the display is off, since a background standby exit never turns the screen on; when the display later turns on, the display-state handler calls `HandleResume` itself, so screen-on becomes the actual wake trigger and the resume/away-mode notifications act as the fallback for when the display is already on. The existing 5-second debounce is unchanged.
- **Tradeoff**: A genuine wake that leaves the display off (e.g. waking headless/remotely) is not recorded as a wake until the screen turns on. Acceptable — the PC isn't being used in that state, which is what these charts measure.
- **Not yet verified**: Built cleanly, but not yet reproduced against a real Modern Standby blip — the failure occurred once in several attempts.

### 2026-09-23 (2): Fix Charts Overflowing Their Card
- **Issue**: After the window had been resized (not maximized), the bar chart grew past the bottom of its card. Maximizing the window fixed it; it looked correct on first render.
- **Cause**: `.pie-canvas-wrapper` in `App.css` had both `position: relative` and `min-height: 0` commented out. Chart.js with `maintainAspectRatio: false` sizes its canvas from the parent element, so the parent must be a dedicated `position: relative` container. Without `min-height: 0`, a column flex item's automatic minimum size is its content size, so the wrapper could not shrink below the canvas — when `.chart-wrapper`'s `height: 40vh` shrank with the window, the canvas kept its larger height and spilled out of the card. Maximizing restored enough height for the canvas to fit again.
- **Fix**: Restored `position: relative` and `min-height: 0` on `.pie-canvas-wrapper`, with a comment explaining why both are required.
- **Verification gap**: Checked in Chrome against the Vite dev server that the rule applies and the wrapper tracks its container, but the resize loop itself could not be exercised — the automation tab runs in a hidden window (`document.hidden === true`), where Chart.js's `ResizeObserver`/`requestAnimationFrame` never fire. Needs a visual check in the running app.
