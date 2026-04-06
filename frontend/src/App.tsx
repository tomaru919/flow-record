import { useEffect, useState } from 'react'
import {
  Chart as ChartJS,
  CategoryScale,
  LinearScale,
  BarElement,
  Title,
  Tooltip,
  Legend,
  ArcElement,
  type TooltipItem,
} from "chart.js"
import { Bar, Pie } from "react-chartjs-2"
import './App.css'

ChartJS.register(
  CategoryScale,
  LinearScale,
  BarElement,
  Title,
  Tooltip,
  Legend,
  ArcElement
)

interface Record {
  window_title: string
  event_type: string
  start_time: string
  end_time: string
}

interface BootDuration {
  date: string
  total_hours: number
}

interface ActiveWindowDuration {
  window_title: string
  duration_hours: number
}

interface WebViewMessage {
  type: string
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  data: any
}

interface WebViewMessageEvent {
  data: WebViewMessage
}

declare global {
  interface Window {
    chrome?: {
      webview: {
        postMessage(message: string): void
        addEventListener(type: string, listener: (event: WebViewMessageEvent) => void): void
        removeEventListener(type: string, listener: (event: WebViewMessageEvent) => void): void
      }
    }
  }
}

function App() {
  const [records, setRecords] = useState<Record[]>([])
  const [bootDurations, setBootDurations] = useState<BootDuration[]>([])
  const [activeWindowDurations, setActiveWindowDurations] = useState<ActiveWindowDuration[]>([])
  
  const refreshData = () => {
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage('getRecords')
      window.chrome.webview.postMessage('getBootDurations')
      window.chrome.webview.postMessage('getActiveWindowDurations')
    } else {
      console.warn("Not running in WebView2")
    }
  }

  useEffect(() => {
    if (window.chrome?.webview) {
      const handleMessage = (event: WebViewMessageEvent) => {
        const message = event.data
        
        if (message.type === 'records') {
          console.log('Received records:', message)
          setRecords(message.data)
        } else if (message.type === 'bootDurations') {
          console.log('Received boot durations:', message)
          setBootDurations(message.data)
        } else if (message.type === 'activeWindowDurations') {
          console.log('Received active window durations:', message)
          setActiveWindowDurations(message.data)
        }
      }

      window.chrome.webview.addEventListener('message', handleMessage)
      refreshData()

      return () => {
        window.chrome?.webview?.removeEventListener('message', handleMessage)
      }
    }
  }, [])

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
          callback: (value: string | number) => `${value}h`
        }
      }
    }
  }

  const pieColors = [
    '#36a2eb', '#ff6384', '#ffce56', '#4bc0c0', '#9966ff',
    '#ff9f40', '#c9cbcf', '#70a1ff', '#7bed9f', '#ff4757'
  ]

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
      title: {
        display: true,
        text: '今日のアクティブウィンドウ内訳',
        color: '#888',
        font: {
          size: 14,
          weight: 'normal' as const
        }
      },
      tooltip: {
        callbacks: {
          label: (context: TooltipItem<'pie'>) => {
            const value = context.raw as number
            const totalMinutes = Math.round(value * 60)
            const hours = Math.floor(totalMinutes / 60)
            const minutes = totalMinutes % 60
            
            const total = (context.dataset.data as number[]).reduce((a, b) => a + b, 0)
            const percentage = ((value / total) * 100).toFixed(1)
            
            return ` ${hours}時間${minutes}分 (${percentage}%)`
          }
        }
      }
    }
  }

  return (
    <div className="container">
      <header className="header">
        <h1>FlowRecord Daily Log</h1>
        <button onClick={refreshData}>Refresh</button>
      </header>

      <div className="charts-container">
        <div className="chart-wrapper bar-chart">
          <Bar data={chartData} options={chartOptions} />
        </div>
        <div className="chart-wrapper pie-chart">
          <Pie data={pieChartData} options={pieChartOptions} />
        </div>
      </div>
      
      <div className="table-wrapper">
        <table>
          <thead>
            <tr>
              <th>Time</th>
              <th>Event</th>
              <th>Details</th>
            </tr>
          </thead>
          <tbody>
            {records.map((record, index) => (
              <tr key={index}>
                <td>{new Date(record.start_time).toLocaleTimeString()}</td>
                <td>{record.event_type}</td>
                <td>{record.window_title}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

export default App
