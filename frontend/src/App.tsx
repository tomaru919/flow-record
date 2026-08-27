import { useEffect, useState } from 'react'
import './App.css'
import DailyActivityChart from './components/DailyActivityChart'
import ActiveWindowChart from './components/ActiveWindowChart'
import type { Record, BootDuration, ActiveWindowDuration, WebViewMessageEvent } from './types'

export default function App() {
  const [records, setRecords] = useState<Record[]>([])
  const [bootDurations, setBootDurations] = useState<BootDuration[]>([])
  const [activeWindowDurations, setActiveWindowDurations] = useState<ActiveWindowDuration[]>([])
  const [weekOffset, setWeekOffset] = useState(0)

  const requestBootDurations = (offset: number) => {
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage(`getBootDurations:${offset}`)
    }
  }

  const refreshData = () => {
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage('getRecords')
      requestBootDurations(weekOffset)
      window.chrome.webview.postMessage('getActiveWindowDurations')
    } else {
      console.warn("Not running in WebView2")
    }
  }

  const handlePrevWeek = () => {
    const newOffset = weekOffset - 1
    setWeekOffset(newOffset)
    requestBootDurations(newOffset)
  }

  const handleNextWeek = () => {
    if (weekOffset >= 0) return
    const newOffset = weekOffset + 1
    setWeekOffset(newOffset)
    requestBootDurations(newOffset)
  }

  useEffect(() => {
    if (window.chrome?.webview) {
      const handleMessage = (event: WebViewMessageEvent) => {
        const message = event.data
        
        if (message.type === 'records') {
          setRecords(message.data)
        } else if (message.type === 'bootDurations') {
          setBootDurations(message.data)
        } else if (message.type === 'activeWindowDurations') {
          setActiveWindowDurations(message.data)
        } else if (message.type === 'refresh') {
          refreshData()
        }
      }

      window.chrome.webview.addEventListener('message', handleMessage)
      refreshData()

      return () => {
        window.chrome?.webview?.removeEventListener('message', handleMessage)
      }
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  return (
    <div className="container">
      <header className="header">
        <h1>FlowRecord Daily Log</h1>
        <button onClick={refreshData}>Refresh</button>
      </header>

      <div className="charts-container">
        <DailyActivityChart
          bootDurations={bootDurations}
          weekOffset={weekOffset}
          onPrevWeek={handlePrevWeek}
          onNextWeek={handleNextWeek}
        />
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
