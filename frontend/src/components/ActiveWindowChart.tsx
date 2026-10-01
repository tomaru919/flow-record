import {
  Chart as ChartJS,
  ArcElement,
  Tooltip,
  Legend,
  type TooltipItem,
} from "chart.js"
import { Pie } from "react-chartjs-2"
import type { ActiveWindowDuration } from "../types"

ChartJS.register(
  ArcElement,
  Tooltip,
  Legend
)

interface ActiveWindowChartProps {
  activeWindowDurations: ActiveWindowDuration[]
  dayOffset: number
  onPrevDay: () => void
  onNextDay: () => void
}

const WEEKDAYS = ['日', '月', '火', '水', '木', '金', '土']

const getDayLabel = (dayOffset: number) => {
  if (dayOffset === 0) return '今日'
  const date = new Date()
  date.setDate(date.getDate() + dayOffset)
  return `${date.getMonth() + 1}/${date.getDate()} (${WEEKDAYS[date.getDay()]})`
}

const OTHER_TITLE = 'その他'
const OTHER_COLOR = '#c9cbcf'

// 暗い背景でも見分けやすい、色相が離れた色
const PALETTE = [
  '#36a2eb', // 青
  '#ff6384', // 赤
  '#ffce56', // 黄
  '#4bc0c0', // 青緑
  '#9966ff', // 紫
  '#ff9f40', // オレンジ
  '#7bd67b', // 緑
  '#f78fd6', // ピンク
  '#b08968', // 茶
]

// FNV-1a ハッシュ: 同じ文字列からは必ず同じ数値が出る。似た名前でも値が大きくばらける
const hashTitle = (title: string) => {
  let hash = 0x811c9dc5
  for (const char of title) {
    hash ^= char.codePointAt(0)!
    hash = Math.imul(hash, 0x01000193)
  }
  return hash >>> 0
}

// 順位ではなくウィンドウ名から色を決めることで、順位が入れ替わっても同じアプリは同じ色になる。
// 名前から決まる色が他のアプリと重なったときは、パレットの次の空き色を使う
const assignColors = (titles: string[]) => {
  const usedIndexes = new Set<number>()
  const colorByTitle = new Map<string, string>()

  // 順位順に処理すると、順位が変わったときに色の取り合いの勝者が変わってしまうので、名前順に処理する
  const sortedTitles = titles.filter(t => t !== OTHER_TITLE).sort()
  for (const title of sortedTitles) {
    const hash = hashTitle(title)
    const start = hash % PALETTE.length
    let color = `hsl(${hash % 360}, 65%, 60%)` // パレットを使い切ったときの予備
    for (let i = 0; i < PALETTE.length; i++) {
      const index = (start + i) % PALETTE.length
      if (!usedIndexes.has(index)) {
        usedIndexes.add(index)
        color = PALETTE[index]
        break
      }
    }
    colorByTitle.set(title, color)
  }

  return titles.map(t => t === OTHER_TITLE ? OTHER_COLOR : colorByTitle.get(t)!)
}

const OTHER_THRESHOLD = 0.03

const groupMinorWindows = (durations: ActiveWindowDuration[]) => {
  const total = durations.reduce((sum, d) => sum + d.duration_hours, 0)
  if (total === 0) return durations

  const sorted = [...durations].sort((a, b) => b.duration_hours - a.duration_hours)
  const major = sorted.filter(d => d.duration_hours / total >= OTHER_THRESHOLD)
  const minor = sorted.filter(d => d.duration_hours / total < OTHER_THRESHOLD)

  if (minor.length === 0) return major
  if (minor.length === 1) return sorted

  const otherHours = minor.reduce((sum, d) => sum + d.duration_hours, 0)
  return [...major, { window_title: OTHER_TITLE, duration_hours: otherHours }]
}

export default function ActiveWindowChart({ activeWindowDurations, dayOffset, onPrevDay, onNextDay }: ActiveWindowChartProps) {
  const groupedDurations = groupMinorWindows(activeWindowDurations)

  const pieChartData = {
    labels: groupedDurations.map(d => d.window_title),
    datasets: [
      {
        data: groupedDurations.map(d => d.duration_hours),
        backgroundColor: assignColors(groupedDurations.map(d => d.window_title)),
        borderColor: 'transparent',
        hoverOffset: 4
      }
    ]
  }

  const pieChartOptions = {
    responsive: true,
    maintainAspectRatio: false,
    plugins: {
      legend: {
        position: 'right' as const,
        labels: {
          boxWidth: 12,
          padding: 15,
          color: '#888',
          font: {
            size: 11
          }
        }
      },
      tooltip: {
        callbacks: {
          label: (context: TooltipItem<'pie'>) => {
            const value = context.raw as number
            const total = (context.dataset.data as number[]).reduce((a, b) => a + b, 0)
            const percentage = ((value / total) * 100).toFixed(1)

            const totalMinutes = Math.round(value * 60)
            if (totalMinutes < 1) {
              const totalSeconds = Math.round(value * 3600)
              return ` ${totalSeconds}秒 (${percentage}%)`
            }

            const hours = Math.floor(totalMinutes / 60)
            const minutes = totalMinutes % 60
            return ` ${hours}時間${minutes}分 (${percentage}%)`
          }
        }
      }
    }
  }

  return (
    <div className="chart-wrapper pie-chart">
      <div className="week-nav">
        <button className="week-nav-button" onClick={onPrevDay}>{'<'}</button>
        <p className="chart-title">{getDayLabel(dayOffset)}のアクティブウィンドウ内訳</p>
        <button className="week-nav-button" onClick={onNextDay} disabled={dayOffset >= 0}>{'>'}</button>
      </div>
      <div className="pie-canvas-wrapper">
        {groupedDurations.length === 0
          ? <p className="chart-empty">記録がありません</p>
          : <Pie data={pieChartData} options={pieChartOptions} />}
      </div>
    </div>
  )
}
