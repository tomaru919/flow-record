# Session Log

FlowRecord の開発セッションごとの変更・修正の記録。

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

### 2026-09-25: Round the Top of Usage-Only Bars
- **Issue**: On days with no sleep time, the daily activity bar consists only of the blue `使用時間` segment, but its top corners were square — the stacked-bar change (2026-09-14) had hardcoded `topLeft/topRight: 0` on that dataset, assuming the green sleep segment would always sit on top.
- **Fix (`DailyActivityChart.tsx`)**: The `使用時間` dataset's `borderRadius` is now a scriptable option (`ScriptableContext<'bar'>`) that rounds the top corners only when `sleepHours[ctx.dataIndex]` is 0, so whichever segment is topmost gets the rounded corners.

### 2026-09-26: Build Backend and Frontend with One Command
- **Change**: `dotnet publish -c Release` now also builds the frontend. A new `PublishFrontend` target in `FlowRecord.csproj` (`AfterTargets="ComputeFilesToPublish"`) runs `npm install` (only if `node_modules` is missing) and `npm run build`, deletes the old `publish/wwwroot`, then adds `frontend/dist/**` to `ResolvedFileToPublish` with `RelativePath` `wwwroot\...` and `ExcludeFromSingleFile=true` (so `PublishSingleFile` doesn't embed them in the exe, where WebView2's virtual host mapping couldn't read them).
- **Vite**: Removed the hardcoded `outDir` (`../FlowRecord/bin/Release/net10.0-windows/win-x64/publish/wwwroot`) from `vite.config.ts`; Vite now builds to its default `frontend/dist`, so the frontend no longer depends on the .NET output path.
- **Scope**: The target only runs during publish; `dotnet build` (Debug, which loads the Vite dev server at `localhost:5173`) is unaffected.
- **Verified**: Ran `dotnet publish -c Release` — `publish/wwwroot` contained `index.html` and `assets/` with the hashed files `index.html` references. Confirmed `dotnet build` does not invoke Vite. README build instructions updated.

### 2026-09-27: Move the Backend Project to the Repository Root
- **Change**: Moved everything in `FlowRecord/` (csproj, sln, `.cs`/`.xaml`, `app.ico`, `.editorconfig`, `.gitignore`, and the untracked `.env`) to the repository root with `git mv`, so `frontend/` now sits inside the backend project directory. Deleted the old `FlowRecord/bin` and `FlowRecord/obj` build outputs.
- **`FlowRecord.csproj`**: `FrontendDir` changed from `..\frontend\` to `frontend\`. Added `frontend\**;docs\**` to `DefaultItemExcludes` — SDK-style projects glob every file under the project folder, so without this `node_modules` would be scanned on every build.
- **Paths updated**: `.vscode/settings.json` (`dotnet.defaultSolution`), `.vscode/tasks.json`, README build instructions (run `dotnet publish -c Release` at the root; output under `bin/Release/...`), `docs/SemaphoreSlim.md`.
- **Verified**: `dotnet publish -c Release` built the frontend and put `index.html` and `assets/` into `publish/wwwroot`; `dotnet build` finished with 0 errors.

### 2026-09-28: Fixed Colors per App in the Active Window Pie Chart
- **Issue**: `ActiveWindowChart` passed a fixed color array to `backgroundColor`, so colors were assigned by rank; when an app's rank changed, its color changed too.
- **Fix**: Added `colorForTitle`, which hashes the window title (`hash * 31 + codePoint`, kept 32-bit with `| 0`) into an HSL hue (`hsl(hue, 65%, 60%)`). `backgroundColor` is now mapped per entry, so the same app always gets the same color. `その他` uses a fixed gray (`#c9cbcf`).
- **Trade-off**: Two titles can hash to similar hues; accepted over a fixed palette, where collisions would be more frequent.

### 2026-09-28 (2): More Distinct Colors in the Active Window Pie Chart
- **Issue**: With `hsl(hash % 360, ...)`, hues fell anywhere on the wheel, so a day's apps could all land in the blue–green range and be hard to tell apart.
- **Fix (`ActiveWindowChart.tsx`)**: Replaced `colorForTitle` with `assignColors`. It uses a 9-color palette with well-separated hues. Each title's preferred slot is its FNV-1a hash mod the palette size. On a collision it takes the next free slot, so no two slices in one chart share a color. It falls back to an HSL hue only after the palette runs out.
- **Stability**: Titles claim slots in name order, not rank order, so a rank change doesn't change which app wins a contested color. An app's color can still change on a day when a colliding app appears. `その他` stays gray; the literal is now the `OTHER_TITLE` constant.

### 2026-09-29: Show Daily Activity Tooltip Anywhere in the Day's Column
- **Issue**: Short bars were hard to hover, because the tooltip only appeared when the cursor was exactly on a bar.
- **Fix (`DailyActivityChart.tsx`)**: Added `interaction: { mode: 'index', intersect: false }`. Hovering anywhere along a day's x position now shows that day's usage and sleep time together. README TODO checked off.

### 2026-09-29 (2): Disable DevTools in Release Builds
- **Change (`MainWindow.xaml.cs`)**: In the `#else` (Release) branch of `InitializeWebView`, set `CoreWebView2.Settings.AreDevToolsEnabled = false` before navigating. F12, Ctrl+Shift+I and the context menu's 「検査」 no longer open DevTools. Debug builds are unchanged.
- **Verified**: `dotnet build -c Release` — 0 errors, 0 warnings. Not yet checked in the running app.

### 2026-09-30: Browse Past Days in the Active Window Pie Chart
- **Backend**: `GetActiveWindowDurationJsonAsync` takes `dayOffset` (0 = today, -1 = yesterday) and shifts the queried day; the SQL is unchanged. `MainWindow` parses `getActiveWindowDurations:{offset}`, the same way as `getBootDurations:{offset}`.
- **Frontend**: `App.tsx` holds `dayOffset` and passes `onPrevDay` / `onNextDay` to `ActiveWindowChart`, which shows `<` / `>` buttons and a title such as `9/29 (月)のアクティブウィンドウ内訳`. `>` is disabled on today. A day with no rows shows `記録がありません`.
- **Bug fixed along the way**: The `refresh` message handler is created once in `useEffect`, so it kept the first render's `refreshData` and always requested week 0. Offsets are now also kept in refs (`weekOffsetRef`, `dayOffsetRef`), so a refresh reloads the week and day being shown.
- **Known limit**: A window row is counted on the day it started; time past midnight is cut off and not carried to the next day (unchanged behavior).
- **Verified**: `tsc -b`, `eslint` and `dotnet build` pass. Not yet checked in the running app.

### 2026-10-01: Rename `today` / `tomorrow` in the Active Window Query
- **Change (`MonitorService.cs`)**: In `GetActiveWindowDurationJsonAsync`, renamed `today` / `tomorrow` to `dayStart` / `dayEnd` (SQL parameters `@dayStart` / `@dayEnd`). With `dayOffset`, the variables no longer always mean today and tomorrow. Behavior is unchanged; `dotnet build` passes.

### 2026-10-02: Release from GitHub
- **Change**: Added `.github/workflows/release.yml`. Pushing a `v*` tag runs the workflow on `windows-latest`: it sets up .NET 10.0.x and Node 24 (npm cache), runs `npm ci` in `frontend`, then `dotnet publish -c Release -p:Version=<tag without v> -o publish`, which also builds the frontend via `PublishFrontend`. It removes `*.pdb` / `*.xml`, zips the output as `FlowRecord-<tag>-win-x64.zip`, and publishes it with `softprops/action-gh-release@v2` (`generate_release_notes: true`, `permissions: contents: write`).
- **No secrets needed**: The untracked `.env` only holds old Supabase credentials and is not read by the current code.
- **Verified locally**: Ran the publish and zip steps with `-p:Version=0.9.0`. The zip contains `FlowRecord.exe` (~177 MB, self-contained single file) and `wwwroot/` (`index.html`, `assets/`), and the exe's ProductVersion is `0.9.0+<commit>`. The workflow itself has not run on GitHub yet.
- README: added a release section and checked off the TODO.

### 2026-10-02 (2): Fix Release Workflow Warnings
- **NETSDK1194**: On the runner (SDK 10.0.401), `dotnet publish` with no path built `FlowRecord.sln` (the root holds both the `.sln` and the `.csproj`), and `-o` is not supported for solutions. The workflow now runs `dotnet publish FlowRecord.csproj ...`.
- **Node 20 deprecation**: Bumped the actions to their latest majors, all `runs.using: node24` (checked in each repo's `action.yml`): `actions/checkout@v7`, `actions/setup-dotnet@v6`, `actions/setup-node@v7`, `softprops/action-gh-release@v3`. Their release notes list no breaking changes for the inputs used here.
- Also fixed the step name `Create zi p` → `Create zip`.
- **Verified locally**: `dotnet publish FlowRecord.csproj -c Release -p:Version=0.9.1 -o <dir>` builds with no warnings. The workflow has not been re-run on GitHub yet.

### 2026-10-04: Move C# Sources to `src/`
- **Change**: Moved `App.xaml(.cs)`, `MainWindow.xaml(.cs)`, `AssemblyInfo.cs`, `MonitorService.cs` and `PowerLogger.cs` into `src/` with `git mv`. `FlowRecord.csproj`, `FlowRecord.sln` and `app.ico` stay at the root. The SDK's default globs pick up `src/**/*.cs` and `src/**/*.xaml`, so the files need no explicit listing.
- **`App.xaml`**: The WPF SDK only makes `App.xaml` at the project root an `ApplicationDefinition` (`Microsoft.NET.Sdk.WindowsDesktop.props`). In `src/` it became a plain `Page`, so no `Main` was generated (CS5001). The csproj now has `<Page Remove="src\App.xaml" />` and `<ApplicationDefinition Include="src\App.xaml" />`. `EnableDefaultApplicationDefinition=false` was tried and dropped, because it also disables the default `None Remove="**/*.xaml"`.
- **Icon**: `MainWindow.xaml`'s `Icon="app.ico"` is resolved relative to the XAML file, so it would point at `src/app.ico` after the move. Changed it to `Icon="/app.ico"`, the assembly root, which is the same resource `App.xaml.cs` loads via `pack://application:,,,/app.ico`.
- **Verified**: `dotnet build --no-incremental` and `dotnet publish FlowRecord.csproj -c Release` pass with 0 errors / 0 warnings. The app was not launched: running a copy from another folder would re-register the startup entry to that path.

### 2026-10-04 (2): Show App Names Like Task Manager
- **Issue**: The active window chart showed process names such as `Code` and `Taskmgr`.
- **Change (`MonitorService.cs`)**: Added an `app_name (process_name PRIMARY KEY, display_name)` table. When a new foreground app starts, `SaveDisplayNameAsync` reads the exe's `FileVersionInfo.FileDescription` (e.g. `Visual Studio Code`, `タスク マネージャー`, the same text Task Manager shows) and upserts it. The exe path comes from `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageNameW`, because `Process.MainModule`/`Process.Path` fail for elevated processes like Task Manager. `_savedDisplayNames` stops repeat writes within a session. A failed lookup is retried at the next switch.
- **Queries**: `GetActiveWindowDurationJsonAsync` and `GetRecordsJsonAsync` `LEFT JOIN app_name` and show `COALESCE(display_name, window_title)`. The duration query groups by that expression. `active_window.window_title` still stores the process name, so past rows also show the friendly name once that app has been seen again.
- **P/Invoke**: `QueryFullProcessImageNameW` takes `[Out] char[]`, which `LibraryImport` only marshals with `StringMarshalling = StringMarshalling.Utf16` (otherwise SYSLIB1051).
- **Verified**: Compiles (the Debug exe copy failed only because FlowRecord.exe was running). Ran the new duration SQL on a copy of the release DB with sample `app_name` rows: `Code` was shown as `Visual Studio Code`, and unmapped names fell back to the process name. Checked the limited-access path lookup from PowerShell against `Code`. Task Manager was not running, so the elevated case wasn't tested.

### 2026-10-09: Start Vite Before .NET on F5
- **Change (`.vscode/launch.json`, new)**: A `coreclr` launch config `FlowRecord (Vite + .NET)` runs `bin/Debug/net10.0-windows/win-x64/FlowRecord.exe`, with `preLaunchTask: start debug` and `postDebugTask: stop vite dev`.
- **Change (`.vscode/tasks.json`)**: Added the background task `vite dev` (`npm run dev` in `frontend`). Its problem matcher treats Vite's `VITE vX.Y.Z  ready in N ms` line as ready, so the next task only starts after the server is up. `start debug` runs `vite dev` then `build FlowRecord` in sequence. `stop vite dev` calls `workbench.action.tasks.terminate` with `vite dev` through a command input, which stops the server when debugging ends.
- **Change (`frontend/vite.config.ts`)**: `server: { port: 5173, strictPort: true }`. The DEBUG build always loads `http://localhost:5173`, so Vite now fails rather than switching to another port if 5173 is busy.
- **Verified**: Checked that `npm run dev` prints `VITE` and `ready in` as plain text inside its color codes. F5 itself was not run in VS Code.
- **Fix**: The background matcher property was misspelled `activeBegin`; the schema name is `activeOnStart`.
- **Fix**: With Git Bash as the default terminal, `build FlowRecord` (a `shell` task) lost the backslashes in `${workspaceFolder}` (`C:Userstaku_programming...`, MSB1009). Changed it to `"type": "process"` so `dotnet` runs without a shell.

### 2026-10-17: Hide Module Load Messages in the Debug Console
- **Change (`.vscode/launch.json`)**: Added `"logging": { "moduleLoad": false }` to the `FlowRecord (Vite + .NET)` config. The debug console no longer prints a line for every loaded DLL, so `Debug.WriteLine` output is easier to find.

### 2026-10-18: Disable Ctrl+R Reload in the Release Build
- **Change (`MainWindow.xaml.cs`)**: In the `#else` (Release) branch of `InitializeWebView`, set `CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false`. This turns off browser-only shortcuts: reload (Ctrl+R, F5), find (Ctrl+F), print (Ctrl+P), zoom (Ctrl+Plus/Minus), and back/forward. Typing, copying, and pasting still work. The DEBUG build keeps the shortcuts, so reload still works during development.
- **Verified**: The Release build compiles to the scratchpad. The app was not launched.

### 2026-10-19: Move the Answer Destination from Google Drive to Notion
- **Change (Notion)**: Copied the 39 `.md` files from the Google Drive `FlowRecord` folder as child pages of the Notion `FlowRecord` page (`3fd1b689e47e81c4a258e1a3d72fa410`). Tables became Notion tables, and relative links between files became bold text. The Drive originals were left in place.
- **Change (`CLAUDE.md`)**: The `document` rule now saves answers as child pages of the Notion `FlowRecord` page. The Drive folder is kept as the old location, and no new files go there.

### 2026-10-19: Merge feature/recharts (Chart.js → Recharts)
- **Change (`feature/recharts`, before merging)**: Cleaned up `DailyActivityChart.tsx`, which still assumed the sleep bar existed.
  - The bar's top corners are now always rounded with `radius`. The `renderAwakeBar` shape is gone.
  - The y-axis maximum now uses only `awake`, not `total_hours`.
  - Removed `Legend`, the `sleep` field, and the Tooltip `itemSorter`.
  - Later removed `stackId`.
  - Added `icon: string | null` to `ActiveWindowDuration` in `types.d.ts`.
- **Change (merge)**: Merged into `main` (`a695204`).
  - Kept `main`'s version of `SESSION_LOG.md`.
  - Resolved the `DailyActivityChart.tsx` conflict with the branch's Recharts version.
- **Verified**: `tsc -b`, `npm run lint`, and `vite build` (output to the scratchpad) passed before the merge. The app was not launched.

### 2026-10-20: Show App Icons in the Active Window Tooltip
- **Change (`ActiveWindowChart.tsx`)**: Added an `ActiveWindowTooltip` component, passed as the Tooltip's `content`. It shows the app's icon (the `icon` data URL), its name, and its time and percentage.
  - The icon comes from `payload[0].payload`, the data item of the hovered slice.
  - `groupMinorWindows` gives the "その他" row `icon: null`.

### 2026-10-20: Close the Current Active Window on OS Shutdown
- **Problem**: `RecordShutdownSync`, called from `SessionEnding`, only set `boot_shutdown.shutdown_time`. The current `active_window` row kept `end_time = NULL`, so it later counted as an orphan (0 hours). This lost the time of the app used just before shutdown.
- **Change (`MonitorService.RecordShutdownSync`)**:
  - The same command now also runs `UPDATE active_window SET end_time = @end_time WHERE id = @currentWindowRecordId AND end_time IS NULL` (Microsoft.Data.Sqlite batching).
  - The method now holds `_windowLock` and sets `_isSuspended = true`, so the monitoring loop cannot open a new row during shutdown. It also clears `currentWindow` and `_currentWindowRecordId`, like `SuspendMonitoring`.
- **Known limitation**: If the user cancels the shutdown, monitoring stays paused until the next wake from sleep or app restart. `boot_shutdown` also stays closed.
- **Verified**: The Release build compiles to the scratchpad. An actual OS shutdown was not tested.

### 2026-10-22: Recover Sessions Cut Off by a Crash or Power Loss (`last_seen` Heartbeat)
- **Problem**: End times (`boot_shutdown.shutdown_time`, `active_window.end_time`) are only written by the tray Exit and by `SessionEnding`.
  - A power loss, a held power button, a blue screen, a Task Manager kill, a crash, or stopping the debugger leaves them `NULL`.
  - The queries treat such orphan rows as ending at their start time, so the whole session, and the window in use at the time, counted as 0 hours.
- **Schema (`InitializeDatabase`)**: Added `last_seen TEXT NULL` to `boot_shutdown` and `active_window`, both in `CREATE TABLE` and for existing DBs.
  - New helper `AddColumnIfMissing(conn, table, column, definition)` checks `pragma_table_info(@table)` and runs `ALTER TABLE ... ADD COLUMN` only if the column is missing.
  - The earlier inline `icon` check now uses this helper too.
  - Table and column names are interpolated, because SQL parameters can only hold values. Only fixed strings from code are passed.
- **Heartbeat (`MonitoringLoop`)**: Inside `if (!_isSuspended)` and under `_windowLock`, the loop calls the new `UpdateLastSeenAsync(now)` once every `HeartbeatInterval` (1 minute), tracked with `_lastHeartbeat`.
  - `UpdateLastSeenAsync` updates `last_seen` on the current `boot_shutdown` and `active_window` rows in one batched command.
  - A null id passes `DBNull` and matches nothing. Errors are logged and the loop keeps running.
  - `_lastHeartbeat` starts at `DateTime.MinValue`, so the first loop after startup or after waking writes immediately.
  - The heartbeat is skipped during sleep, so sleep time is not counted as usage.
- **Repair on startup (`RepairOrphanRecords`)**: Called at the top of `Start()`, before a new boot row exists.
  - It runs `UPDATE ... SET shutdown_time = last_seen` and `UPDATE ... SET end_time = last_seen` on rows whose end time is `NULL` and `last_seen` is not.
  - The aggregation queries are unchanged.
  - Rows with no `last_seen`, such as those from before this change, are left as they are.
  - Failures are caught and logged, so startup is not blocked.
- **Comments**: Added comments explaining the new fields, helper, heartbeat, and repair. Renamed the repair method from its placeholder name `Foo`.
- **Known limits**:
  - **Loss per crash**: Up to one `HeartbeatInterval` of time is lost per crash.
  - **Windows opened just before a crash**: A window row created after the last heartbeat has no `last_seen`, so it stays an orphan. That loses at most about a minute. New rows don't get `last_seen` at insert time, but adding it would only mark the row closed (0 hours), not recover any time.
  - **Crash during sleep**: `sleep_wake` is not repaired. Repair needs an estimated wake time (the first `active_window` start after `sleep_time`, capped at the session end), which is not implemented.
  - **Second instance**: There is still no single-instance guard. A second instance would close the running instance's open rows during its own repair. Add a `Mutex` before release.
- **Verified**: The Release build compiles to the scratchpad with 0 warnings and 0 errors. Neither a real crash nor the repair was tested in the running app.

### 2026-10-22: Turn the Notion Answer Pages into a Searchable Database
- **Change (Notion)**: Created the `FlowRecord ナレッジ` database under the `FlowRecord` page and moved all 54 answer pages into it, including the nested ones. The two Legend style pages had already been deleted, so they were not moved.
  - **Properties**:
    - `名前`: the title.
    - `カテゴリ`: select, one of `C#・.NET`, `WPF・WebView2`, `Windows API`, `SQLite`, `React・フロントエンド`, `Git`, `ビルド・リリース`, `設計・改善`. Assigned to each page by its title and content.
    - `親項目` / `サブ項目`: a two-way self-relation that keeps the former nesting.
    - `作成日時` and `更新日時`.
  - **Views**: The default table, `新しい順` (newest first, with a カテゴリ quick filter), and `カテゴリ別` (a board grouped by カテゴリ).
- **Change (`CLAUDE.md`)**: The `document` rule now saves answers as rows of this database (data source `collection://b9890974-7f71-4dfc-b17f-eb3488e88107`). Each row gets a `カテゴリ`, and a follow-up gets `親項目` set to the earlier page.

### 2026-10-22: Prevent a Second Instance with a Named Mutex
- **Problem**: Nothing stopped a second `FlowRecord.exe`.
  - The time would be recorded twice, and a second tray icon would appear.
  - Since the `last_seen` change, a second instance's `RepairOrphanRecords` would also close the running instance's open rows.
- **Change (`App.xaml.cs`)**: `OnStartup` creates the named mutex `Local\{InstanceName}.Mutex` with `new Mutex(true, ..., out bool createdNew)`. If `createdNew` is false, it disposes the mutex, calls `Shutdown()`, and returns before `MainWindow`, `MonitorService`, or the tray icon are created.
  - **Field, not local**: The mutex is kept in the `_instanceMutex` field so it lives as long as the app.
  - **Release on exit**: `OnExit` calls `ReleaseMutex()` and `Dispose()`. `OnStartup` and `OnExit` both run on the UI thread, which owns the mutex.
  - **Separate names**: `InstanceName` is `FlowRecord.Debug` in DEBUG and `FlowRecord` in Release, since they use different DBs. A Debug session can run next to the installed Release app.
  - **Crash**: If the process dies, Windows closes the handle, so the next launch gets `createdNew = true`.
- **Not done**: Launching a second copy doesn't bring the running one's window forward, so it looks like nothing happened. A named `EventWaitHandle` could signal the first instance to call `OnOpenClick`.
- **Verified**:
  - Started the Debug build with F5, then launched the same Debug exe with `Start-Process`.
  - Afterwards only one `FlowRecord.exe` was running: PID 4076, whose parent is `vsdbg-ui`, the F5 instance.
  - `flowrecord.debug.db` got no new `boot_shutdown` row after the F5 session's row (id 57), so the second process exited before `MonitorService` started.
  - Not yet checked: relaunching after tray Exit, and relaunching after stopping the debugger.

### 2026-10-22: Remove the Unused System.Management Package
- **Change (`FlowRecord.csproj`)**: Removed `<PackageReference Include="System.Management" Version="10.0.1" />`. No C# code uses WMI (`System.Management`), and it had been unused since the first commit. Removing it keeps `System.Management.dll` and `System.CodeDom.dll` out of the self-contained single-file exe.

### 2026-10-26: Stop Counting Time While Away (Idle Detection)
- **Problem**: If the user walked away with the screen on, the foreground app's time kept growing.
- **Change (`MonitorService.cs`)**: Added `GetIdleTime()`. It calls Win32 `GetLastInputInfo` and returns the time since the last keyboard or mouse input.
  - **Wraparound**: `GetLastInputInfo` and `Environment.TickCount` both count milliseconds since boot and wrap after about 49.7 days. The difference is computed as `uint` inside `unchecked`, so it stays correct across the wrap.
  - **API failure**: If the call fails, it returns `TimeSpan.Zero`, which counts as "in use", so recording never stops by mistake.
- **Change (`MonitoringLoop`)**:
  - **Going idle**: When `idle >= IdleThreshold` (5 minutes), the first such loop closes the current `active_window` row at the last input time (`now - idle`). That time is clamped to `windowStartTime`, so the duration is never negative when a window took focus without input, such as a notification. `_isIdle` keeps the row from being closed again on later loops.
  - **Coming back**: When input resumes, `_isIdle` is reset. Since `currentWindow` is empty, the normal switch logic starts a new row for the foreground window.
  - **Heartbeat**: The `last_seen` heartbeat keeps running while idle, so `boot_shutdown` still records the session as alive.
- **Trade-off**: Watching a video or listening to a meeting for more than 5 minutes without input also counts as away.

### 2026-10-28: Stop Recording While the Screen Is Locked
- **Problem**: With idle detection alone, the first 5 minutes after Win+L still counted as usage.
- **Change (`MainWindow.xaml.cs`)**: Subscribes to `SystemEvents.SessionSwitch` in the constructor and unsubscribes in `OnClosed`, since `SystemEvents` is static and would otherwise keep the window alive. `SessionLock` calls `LockMonitoring(DateTime.Now)`, and `SessionUnlock` calls `UnlockMonitoring()`. Both events are logged with `PowerLogger` so they show in Release builds.
- **Change (`MonitorService.cs`)**:
  - **`LockMonitoring`**: Takes `_windowLock`, sets `_isLocked`, closes the current row synchronously at the lock time (like `SuspendMonitoring`), and clears `currentWindow` and `_currentWindowRecordId`.
  - **`UnlockMonitoring`**: Clears `_isLocked`. The next loop starts a new row.
  - **Loop**: The monitoring loop now runs only when `!_isSuspended && !_isLocked`.
  - **Why a separate flag**: If `_isSuspended` were reused, then locking, sleeping, and waking would let `ResumeMonitoring` clear the flag and record the lock screen as usage.
  - **Heartbeat**: `last_seen` is not written while locked, so a crash during a lock ends the boot session at the lock time.
- **Leftover**: `0890faa` added a debugging line, `Debug.WriteLine($"path={path}")`, in `GetAppInfo`.
- **Verified**: Not recorded in this log. Lock and unlock leave `SessionLock` / `SessionUnlock` lines in `%LocalAppData%\FlowRecord\power.log` (`power.debug.log` in DEBUG), which can be used to check.

### 2026-10-29: Repair Orphaned sleep_wake Rows at Startup
- **Problem**: A `sleep_wake` row keeps `wake_time = NULL` when the wake is never written. This happens on a power loss during sleep, a shutdown or Exit within the 5-second wake confirmation, or a failed `RecordWakeAsync` (its `finally` clears `_sleepWakeId`, so it is never retried).
  - The weekly chart treats such a row as 0 hours of sleep, but usage is `total_hours - sleep_hours`.
  - **Power loss during sleep**: Harmless. `last_seen` is not written while asleep, so the boot session is repaired to end just before the sleep.
  - **Woke but no `wake_time`**: Wrong. For example, sleeping at 23:00, waking at 7:00, and shutting down at 7:00:03 counted the whole night (8 hours) as usage.
- **Change (`MonitorService.RepairOrphanRecords`)**: Added an `UPDATE sleep_wake ... FROM (...) AS s` after the existing `boot_shutdown` / `active_window` repairs, in the same `ExecuteNonQuery`.
  - **Session end**: For each row with `wake_time IS NULL`, the subquery `s` finds the boot session that contains the sleep (the latest `boot_time <= sleep_time`) and takes its `shutdown_time` as `session_end`. It is computed once and used twice in `SET`.
  - **Wake time**: The start of the first `active_window` row between `sleep_time` and `session_end`. The monitoring loop starts a row right after waking, so this is close to the real wake time.
  - **No window**: If the session lasted past the sleep (for example, shut down from the lock screen), `session_end` is used. Otherwise (power loss during sleep, or `session_end` is NULL), `sleep_time` is used, which means 0 hours.
  - **Order**: It must run after the `boot_shutdown` repair, because it reads the repaired `shutdown_time`.
  - **Rerun-safe**: Every statement only touches rows whose end time is NULL, so a failed repair is simply retried on the next launch.
  - Comments explaining the above were added to the method and inside the SQL.
- **Formatting**: Reformatted the SQL in `GetRecordsJsonAsync`, `GetDailyBootDurationJsonAsync`, and `GetActiveWindowDurationJsonAsync`. Compared with whitespace and comments removed, all of them are identical to before.
- **Limits**:
  - The repair runs only at startup, so after a failed `RecordWakeAsync` the chart overcounts until the next launch.
  - If the lock screen appears after waking, no window is recorded until unlock, so the unlock time becomes the wake time.
- **Verified**:
  - `dotnet build` passes with 0 warnings and 0 errors.
  - The app bundles SQLite 3.53.4 (via `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5), which supports `UPDATE ... FROM` (3.33+).
  - The `RepairOrphanRecords` query string, copied from the source, was run against an in-memory DB with `node:sqlite`:
    - Power loss during sleep gave `wake_time = sleep_time`.
    - Shutting down 3 seconds after waking gave the first window's start time (7:00:01).
    - A normal row was unchanged.
  - On 2026-10-30, the DEBUG build was started with F5 against `flowrecord.debug.db` containing hand-inserted orphan rows dated January 2026:
    - Power loss during sleep: `wake_time` became the sleep time (1/1 23:00:00), and the `boot_shutdown` row closed at its `last_seen` (22:59:30). The next day's window was not picked up.
    - Shutting down 3 seconds after waking: `wake_time` became the first window's start time (1/4 7:00:01).
    - Shutting down from the lock screen (no window): `wake_time` became the session end (1/6 7:05:00).
    - The real `sleep_wake` rows (ids 1 to 5) were unchanged.
    - The DB was then restored from a backup, which also removed that F5 session's `boot_shutdown` row.
  - Not checked: the chart on screen (per-day usage was only computed with an equivalent SQL query in `node:sqlite`), and an orphan row produced by a real sleep and wake.
