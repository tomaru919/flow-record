import { useEffect, useState } from 'react'
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
import './App.css'

ChartJS.register(
  CategoryScale,
  LinearScale,
  BarElement,
  Title,
  Tooltip,
  Legend
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
  
  const refreshData = () => {
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage('getRecords')
      window.chrome.webview.postMessage('getBootDurations')
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
    labels: bootDurations.map(d => d.date),
    datasets: [
      {
        label: 'PC 稼働時間',
        data: bootDurations.map(d => d.total_hours),
        backgroundColor: 'rgba(54, 162, 235, 0.6)',
        borderColor: 'rgba(54, 162, 235, 1)',
        borderWidth: 1
      }
    ]
  }

  const chartOptions = {
    responsive: true,
    plugins: {
      legend: {
        position: 'top' as const,
      },
      title: {
        display: true,
        text: 'Daily PC Usage',
      },
      tooltip: {
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
      y: {
        beginAtZero: true,
        title: {
          display: true,
          text: '時間'
        }
      }
    }
  }

  return (
    <div className="container">
      <div className="chart-wrapper">
        <Bar data={chartData} options={chartOptions} />
      </div>
      
      <h1>FlowRecord Daily Log</h1>
      <button onClick={refreshData}>Refresh</button>
      
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
