import {
  Chart as ChartJS,
  CategoryScale,
  LinearScale,
  BarElement,
  Title,
  Tooltip,
  Legend,
  type TooltipItem,
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
}

const DailyActivityChart = ({ bootDurations }: DailyActivityChartProps) => {
  const chartData = {
    labels: bootDurations.map(d => {
      const date = new Date(d.date);
      return `${date.getMonth() + 1}/${date.getDate()}`;
    }),
    datasets: [
      {
        label: 'PC 稼働時間',
        data: bootDurations.map(d => d.total_hours),
        backgroundColor: '#36a2eb',
        borderRadius: 4,
        hoverBackgroundColor: '#2980b9',
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
        display: false,
      },
      title: {
        display: true,
        text: '過去7日間の稼働時間',
        color: '#888',
        font: {
          size: 14,
          weight: 'normal' as const
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
            return `稼働時間: ${hours}時間${minutes}分`
          }
        }
      }
    },
    scales: {
      x: {
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
      <Bar data={chartData} options={chartOptions} />
    </div>
  )
}

export default DailyActivityChart
