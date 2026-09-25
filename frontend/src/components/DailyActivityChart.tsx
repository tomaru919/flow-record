import {
  Chart as ChartJS,
  CategoryScale,
  LinearScale,
  BarElement,
  Title,
  Tooltip,
  Legend,
  type TooltipItem,
  type ScriptableContext,
} from "chart.js"
import { Bar } from "react-chartjs-2"
import type { BootDuration } from "../types"

ChartJS.register(
  CategoryScale,
  LinearScale,
  BarElement,
  Title,
  Tooltip,
  Legend
)

interface DailyActivityChartProps {
  bootDurations: BootDuration[]
  weekOffset: number
  onPrevWeek: () => void
  onNextWeek: () => void
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

export default function DailyActivityChart({ bootDurations, weekOffset, onPrevWeek, onNextWeek }: DailyActivityChartProps) {
  const awakeHours = bootDurations.map(d => Math.max(0, d.total_hours - d.sleep_hours))
  const sleepHours = bootDurations.map(d => d.sleep_hours)

  const chartData = {
    labels: bootDurations.map(d => {
      const date = new Date(d.date)
      return `${date.getMonth() + 1}/${date.getDate()}`
    }),
    datasets: [
      {
        label: '使用時間',
        data: awakeHours,
        backgroundColor: '#36a2eb',
        // スリープ時間がない日は使用時間がバーの先頭になるので、上の角も丸める
        borderRadius: (ctx: ScriptableContext<'bar'>) => {
          const top = sleepHours[ctx.dataIndex] > 0 ? 0 : 4
          return { topLeft: top, topRight: top, bottomLeft: 4, bottomRight: 4 }
        },
        hoverBackgroundColor: '#2980b9',
        stack: 'usage',
      },
      {
        label: 'スリープしていた時間',
        data: sleepHours,
        backgroundColor: '#4caf50',
        borderRadius: { topLeft: 4, topRight: 4, bottomLeft: 0, bottomRight: 0 },
        hoverBackgroundColor: '#3d8b40',
        stack: 'usage',
      }
    ]
  }

  const maxHours = Math.max(...bootDurations.map(d => d.total_hours), 0)
  const stepSize = maxHours > 1.5 ? 30 / 60 : 10 / 60

  const chartOptions = {
    responsive: true,
    maintainAspectRatio: false,
    plugins: {
      legend: {
        display: true,
        position: 'top' as const,
        labels: {
          boxWidth: 12,
          padding: 12,
          color: '#888',
          font: {
            size: 11
          }
        }
      },
      tooltip: {
        backgroundColor: 'rgba(0, 0, 0, 0.8)',
        padding: 12,
        titleFont: {
          size: 14,
        },
        bodyFont: {
          size: 13,
        },
        callbacks: {
          label: (context: TooltipItem<'bar'>) => {
            const value = context.raw as number
            const totalMinutes = Math.round(value * 60)
            const hours = Math.floor(totalMinutes / 60)
            const minutes = totalMinutes % 60
            return `${context.dataset.label}: ${hours}時間${minutes}分`
          }
        }
      }
    },
    scales: {
      x: {
        stacked: true,
        grid: {
          display: false,
        },
        ticks: {
          font: {
            family: "system-ui, -apple-system, sans-serif",
          }
        }
      },
      y: {
        stacked: true,
        beginAtZero: true,
        grid: {
          color: 'rgba(200, 200, 200, 0.1)',
        },
        ticks: {
          font: {
            family: "system-ui, -apple-system, sans-serif",
          },
          stepSize,
          callback: (value: string | number) => {
            const totalMinutes = Math.round((value as number) * 60)
            const hours = Math.floor(totalMinutes / 60)
            const minutes = totalMinutes % 60
            if (hours === 0) return `${minutes}分`
            if (minutes === 0) return `${hours}時間`
            return `${hours}時間${minutes}分`
          }
        }
      }
    }
  }

  return (
    <div className="chart-wrapper bar-chart">
      <div className="week-nav">
        <button className="week-nav-button" onClick={onPrevWeek}>{'<'}</button>
        <p className="chart-title">{getWeekRangeLabel(weekOffset)}</p>
        <button className="week-nav-button" onClick={onNextWeek} disabled={weekOffset >= 0}>{'>'}</button>
      </div>
      <div className="pie-canvas-wrapper">
        <Bar data={chartData} options={chartOptions} />
      </div>
    </div>
  )
}
