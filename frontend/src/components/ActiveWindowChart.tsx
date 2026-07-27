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
}

const pieColors = [
  '#36a2eb', '#ff6384', '#ffce56', '#4bc0c0', '#9966ff',
  '#ff9f40', '#c9cbcf', '#70a1ff', '#7bed9f', '#ff4757'
]

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
  return [...major, { window_title: 'その他', duration_hours: otherHours }]
}

export default function ActiveWindowChart({ activeWindowDurations }: ActiveWindowChartProps) {
  const groupedDurations = groupMinorWindows(activeWindowDurations)

  const pieChartData = {
    labels: groupedDurations.map(d => d.window_title),
    datasets: [
      {
        data: groupedDurations.map(d => d.duration_hours),
        backgroundColor: pieColors,
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
      <p className="chart-title">今日のアクティブウィンドウ内訳</p>
      <div className="pie-canvas-wrapper">
        <Pie data={pieChartData} options={pieChartOptions} />
      </div>
    </div>
  )
}
