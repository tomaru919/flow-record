# Project Context: FlowRecord

## Data Handling & Charting
- **Daily Activity Chart**: Displays the last 7 days of PC usage.
- **Missing Data**: Even if the PC was not turned on, the chart shows 0 hours for that day.
- **SQL Implementation**: Uses PostgreSQL's `generate_series` to create a date range for the last 7 days and performs a `LEFT JOIN` with the `boot_shutdown` table.
- **Session Duration**: Calculates the sum of durations for all boot/shutdown sessions within a single day. If a session is currently active (no `shutdown_time`), it uses `@now` (the current local time passed from the application) as the end time for calculation.

### Database Query Logic (MonitorService.cs)
```sql
SELECT
    ds.date,
    COALESCE(SUM(EXTRACT(EPOCH FROM (COALESCE(bs.shutdown_time, @now) - bs.boot_time))), 0) / 3600 AS total_hours
FROM (
    SELECT (@today - (i || ' day')::interval)::date AS date
    FROM generate_series(0, 6) i
) ds
LEFT JOIN boot_shutdown bs ON DATE(bs.boot_time) = ds.date AND bs.pc_name_id = @pc_name_id
GROUP BY ds.date
ORDER BY ds.date ASC
```

## Session Log

### 2026-03-15: Fix Negative Usage Display on Startup
- **Issue**: Daily usage was displayed as a negative value when the app was first started for the day.
- **Cause**: PostgreSQL's `CURRENT_TIMESTAMP` and `CURRENT_DATE` were defaulting to UTC, which caused a mismatch when compared with the local time `boot_time` recorded by the C# application.
- **Fix**: Modified `MonitorService.GetDailyBootDurationJsonAsync` to pass local `DateTime.Now` and `DateTime.Today` as parameters (`@now`, `@today`) to the SQL query instead of relying on PostgreSQL's server-side current time.
- **Impact**: Ensures that usage calculations are always performed using the same local time reference as the recorded events.
