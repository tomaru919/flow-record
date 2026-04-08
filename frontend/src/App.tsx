import { useEffect, useState } from 'react'
import './App.css'
import DailyActivityChart from './components/DailyActivityChart'
import ActiveWindowChart from './components/ActiveWindowChart'

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
          console.log('Received records:', message.data)
          setRecords(message.data)
        } else if (message.type === 'bootDurations') {
          console.log('Received boot durations:', message.data)
          setBootDurations(message.data)
        } else if (message.type === 'activeWindowDurations') {
          console.log('Received active window durations:', message.data)
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

  return (
    <div className="container">
      <header className="header">
        <h1>FlowRecord Daily Log</h1>
        <button onClick={refreshData}>Refresh</button>
      </header>

      <div className="charts-container">
        <DailyActivityChart bootDurations={bootDurations} />
        <ActiveWindowChart activeWindowDurations={activeWindowDurations} />
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
