import { useEffect, useState } from 'react'
import {
  Chart as ChartJS,
  CategoryScale,
  LinearScale,
  BarElement,
  Title,
  Tooltip,
  Legend,
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

interface WebViewMessageEvent {
  data: Record[]
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
  
  const refreshData = () => {
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage('getRecords')
    } else {
      console.warn("Not running in WebView2")
    }
  }

  useEffect(() => {
    // WebView2からのメッセージ受信設定
    if (window.chrome?.webview) {
      window.chrome.webview.addEventListener('message', (event) => {
        const data = event.data // JSON object already parsed or string
        setRecords(data)
      })

      // 初回ロード時にデータ要求
      refreshData()
    }
  }, [])

  useEffect(() => {
    console.log("Received records:", records)
  }, [records])

  return (
    <div className="container">
      <Bar data={{
        labels: ['Work', 'Break', 'Meeting'],
        datasets: [
          {
            data: [30, 20, 50],
            backgroundColor: [
              '#FF6384',
              '#36A2EB',
              '#FFCE56'
            ],
            borderWidth: 1
          }
        ]
      }} />
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
