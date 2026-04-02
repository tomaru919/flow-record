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

### 2026-04-02: Make UI Responsive
- **Flexibility**: Updated `header`, `chart-wrapper`, and `table-wrapper` to expand with window width. Increased `max-width` limit to 1200px.
- **Dynamic Height**: Changed `chart-wrapper` height to `50vh` (min 300px, max 500px) so the chart resizes based on window height.
- **Stability**: Applied `box-sizing: border-box` to all elements to prevent layout issues from padding.
- **Mobile Support**: Added media queries to adjust the header layout (vertical stack) on narrow screens (under 600px).
