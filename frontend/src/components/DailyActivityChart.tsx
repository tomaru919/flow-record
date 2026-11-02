import {
  Bar,
  BarChart,
  CartesianGrid,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts"
import type { BootDuration, HourlyUsage } from "../types"

interface DailyActivityChartProps {
  bootDurations: BootDuration[]
  weekOffset: number
  onPrevWeek: () => void
  onNextWeek: () => void
  selectedDate: string | null
  hourlyUsage: HourlyUsage[]
  onSelectDate: (date: string | null) => void
}

interface DayRow {
  label: string
  awake: number
}

const AWAKE_COLOR = '#36a2eb'
const AWAKE_HOVER_COLOR = '#2980b9'
const BAR_RADIUS = 4
const WEEKDAYS = ['日', '月', '火', '水', '木', '金', '土']
// 時間別のグラフは 1 時間が上限なので、目盛りは 0〜60 分の 10 分刻みで固定する
const HOURLY_TICKS = [0, 10, 20, 30, 40, 50, 60].map(minutes => minutes / 60)

const getDayLabel = (date: string) => {
  const d = new Date(`${date}T00:00:00`)
  return `${d.getMonth() + 1}/${d.getDate()} (${WEEKDAYS[d.getDay()]}) の時間別`
}

const getWeekRangeLabel = (weekOffset: number) => {
  const today = new Date()
  today.setHours(0, 0, 0, 0)
  const sunday = new Date(today)
  sunday.setDate(today.getDate() - today.getDay() + weekOffset * 7)
  const saturday = new Date(sunday)
  saturday.setDate(sunday.getDate() + 6)
  const format = (d: Date) => `${d.getMonth() + 1}/${d.getDate()}`
  return `${format(sunday)} (日) 〜 ${format(saturday)} (土)`
}

const formatDuration = (hours: number) => {
  const totalMinutes = Math.round(hours * 60)
  return `${Math.floor(totalMinutes / 60)}時間${totalMinutes % 60}分`
}

const formatTick = (hours: number) => {
  const totalMinutes = Math.round(hours * 60)
  const h = Math.floor(totalMinutes / 60)
  const m = totalMinutes % 60
  if (h === 0) return `${m}分`
  if (m === 0) return `${h}時間`
  return `${h}時間${m}分`
}

// 目盛りが多すぎると Recharts が一部を間引いて「1時間30分, 2時間30分…」のような半端な位置だけ残るので、
// 最大値に応じて刻みを広げ、目盛りの数をおおよそ6個以下に抑える（3時間超は1時間、1.5時間超は30分、それ以下は10分刻み）
const getTicks = (maxHours: number) => {
  const step = maxHours > 3 ? 1 : maxHours > 1.5 ? 30 / 60 : 10 / 60
  const count = Math.max(1, Math.ceil(maxHours / step))
  return Array.from({ length: count + 1 }, (_, i) => i * step)
}

export default function DailyActivityChart({
  bootDurations,
  weekOffset,
  onPrevWeek,
  onNextWeek,
  selectedDate,
  hourlyUsage,
  onSelectDate,
}: DailyActivityChartProps) {
  const rows: DayRow[] = selectedDate
    ? hourlyUsage.map(h => ({ label: `${h.hour}時`, awake: h.usage_hours }))
    : bootDurations.map(d => {
      const date = new Date(d.date)
      return {
        label: `${date.getMonth() + 1}/${date.getDate()}`,
        awake: Math.max(0, d.total_hours - d.sleep_hours),
      }
    })

  const maxHours = Math.max(...rows.map(r => r.awake), 0)
  const ticks = selectedDate ? HOURLY_TICKS : getTicks(maxHours)

  return (
    <div className="chart-wrapper bar-chart">
      <div className="week-nav">
        {selectedDate
          ? (
            <>
              <button className="week-nav-button" onClick={() => onSelectDate(null)}>週に戻る</button>
              <p className="chart-title">{getDayLabel(selectedDate)}</p>
            </>
          )
          : (
            <>
              <button className="week-nav-button" onClick={onPrevWeek}>{'<'}</button>
              <p className="chart-title">{getWeekRangeLabel(weekOffset)}</p>
              <button className="week-nav-button" onClick={onNextWeek} disabled={weekOffset >= 0}>{'>'}</button>
            </>
          )}
      </div>
      <div className="pie-canvas-wrapper">
        <ResponsiveContainer width="100%" height="100%">
          <BarChart data={rows} margin={{ top: 4, right: 8, bottom: 0, left: 8 }}>
            <CartesianGrid vertical={false} stroke="rgba(200, 200, 200, 0.1)" />
            <XAxis
              dataKey="label"
              // 既定では文字が重なりそうな目盛りを間引くので、全部表示する。
              // 時間別は 24 個並んで「時」まで入らないので、目盛りは数字だけにする（Tooltip は label のまま「13時」と出る）
              interval={0}
              tickFormatter={selectedDate ? (label: string) => label.replace('時', '') : undefined}
              tick={{ fill: '#888', fontSize: 12 }}
              tickLine={false}
              axisLine={{ stroke: '#444' }}
            />
            <YAxis
              ticks={ticks}
              domain={[0, ticks[ticks.length - 1]]}
              tickFormatter={formatTick}
              // 既定では文字が重なりそうな目盛りを間引くが、getTicks で数を抑えているので全部表示する
              interval={0}
              tick={{ fill: '#888', fontSize: 12 }}
              tickLine={false}
              axisLine={false}
              width={70}
            />
            {/* Tooltip は既定でカーソルがある日（x軸の位置）の全系列を表示するので、短いバーでも列のどこでも情報が見える */}
            <Tooltip
              cursor={{ fill: 'rgba(255, 255, 255, 0.05)' }}
              formatter={(value) => formatDuration(Number(value))}
              contentStyle={{ backgroundColor: 'rgba(0, 0, 0, 0.8)', border: 'none', borderRadius: 6, padding: 12 }}
              labelStyle={{ color: '#fff', fontSize: 14, marginBottom: 4 }}
              itemStyle={{ fontSize: 13 }}
            />
            <Bar
              dataKey="awake"
              name="使用時間"
              fill={AWAKE_COLOR}
              radius={[BAR_RADIUS, BAR_RADIUS, 0, 0]}
              activeBar={{ fill: AWAKE_HOVER_COLOR }}
              // 週のグラフのバーをクリックすると、その日の時間別のグラフに切り替える
              cursor={selectedDate ? undefined : 'pointer'}
              onClick={selectedDate ? undefined : (_, index) => onSelectDate(bootDurations[index].date)}
            />
          </BarChart>
        </ResponsiveContainer>
      </div>
    </div>
  )
}
