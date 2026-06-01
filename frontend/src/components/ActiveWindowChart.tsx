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

const ActiveWindowChart = ({ activeWindowDurations }: ActiveWindowChartProps) => {
  const pieChartData = {
    labels: activeWindowDurations.map(d => d.window_title),
    datasets: [
      {
        data: activeWindowDurations.map(d => d.duration_hours),
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
      <Pie data={pieChartData} options={pieChartOptions} />
    </div>
  )
}

export default ActiveWindowChart
