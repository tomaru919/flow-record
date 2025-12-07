import { useEffect, useState } from 'react'
import './App.css'

interface Record {
  window_title: string
  event_type: string
  start_time: string
  end_time: string
  duration: number | null
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

  return (
    <div className="container">
      <h1>FlowRecord Daily Log</h1>
      <button onClick={refreshData}>Refresh</button>
      <div className="table-wrapper">
        <table>
          <thead>
            <tr>
              <th>Time</th>
              <th>Event</th>
              <th>Details</th>
              <th>Duration (s)</th>
            </tr>
          </thead>
          <tbody>
            {records.map((r, i) => (
              <tr key={i}>
                <td>{new Date(r.start_time).toLocaleTimeString()}</td>
                <td>{r.event_type}</td>
                <td>{r.window_title}</td>
                <td>{r.duration}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

export default App
