import { useEffect, useRef, useState } from 'react'
import './App.css'
import DailyActivityChart from './components/DailyActivityChart'
import ActiveWindowChart from './components/ActiveWindowChart'
import type {
  ActiveWindowRecord,
  BootDuration,
  HourlyUsage,
  ActiveWindowDuration,
  WebViewMessageEvent,
} from './types'

export default function App() {
  const [activeWindowRecords, setActiveWindowRecords] = useState<ActiveWindowRecord[]>([])
  const [bootDurations, setBootDurations] = useState<BootDuration[]>([])
  const [activeWindowDurations, setActiveWindowDurations] = useState<ActiveWindowDuration[]>([])
  const [weekOffset, setWeekOffset] = useState(0)
  const [dayOffset, setDayOffset] = useState(0)
  // 時間別に表示している日（yyyy-MM-dd）。null のときは週のグラフを表示する
  const [selectedDate, setSelectedDate] = useState<string | null>(null)
  const [hourlyUsage, setHourlyUsage] = useState<HourlyUsage[]>([])
  // useEffect 内のメッセージハンドラは最初の描画時の refreshData を持ち続けるので、
  // state を直接読むと常に初期値の 0 になる。最新の値を読めるよう ref にも入れておく
  const weekOffsetRef = useRef(0)
  const dayOffsetRef = useRef(0)
  const selectedDateRef = useRef<string | null>(null)

  const requestHourlyUsage = (date: string) => {
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage(`getHourlyUsage:${date}`)
    }
  }

  const requestBootDurations = (offset: number) => {
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage(`getBootDurations:${offset}`)
    }
  }

  const requestActiveWindowDurations = (offset: number) => {
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage(`getActiveWindowDurations:${offset}`)
    }
  }

  const refreshData = () => {
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage('getActiveWindowRecords')
      requestBootDurations(weekOffsetRef.current)
      requestActiveWindowDurations(dayOffsetRef.current)
      if (selectedDateRef.current) requestHourlyUsage(selectedDateRef.current)
    } else {
      console.warn("Not running in WebView2")
    }
  }

  const changeWeek = (newOffset: number) => {
    weekOffsetRef.current = newOffset
    setWeekOffset(newOffset)
    requestBootDurations(newOffset)
  }

  const handlePrevWeek = () => changeWeek(weekOffset - 1)

  const handleNextWeek = () => {
    if (weekOffset >= 0) return
    changeWeek(weekOffset + 1)
  }

  const changeSelectedDate = (date: string | null) => {
    selectedDateRef.current = date
    setSelectedDate(date)
    // 前に選んだ日のグラフが一瞬表示されないように空にしておく
    setHourlyUsage([])
    if (date) requestHourlyUsage(date)
  }

  const changeDay = (newOffset: number) => {
    dayOffsetRef.current = newOffset
    setDayOffset(newOffset)
    requestActiveWindowDurations(newOffset)
  }

  const handlePrevDay = () => changeDay(dayOffset - 1)

  const handleNextDay = () => {
    if (dayOffset >= 0) return
    changeDay(dayOffset + 1)
  }

  useEffect(() => {
    if (window.chrome?.webview) {
      const handleMessage = (event: WebViewMessageEvent) => {
        const message = event.data
        
        if (message.type === 'activeWindowRecords') {
          setActiveWindowRecords(message.data)
        } else if (message.type === 'bootDurations') {
          setBootDurations(message.data)
        } else if (message.type === 'hourlyUsage') {
          // 応答が届く前に別の日へ切り替えていたら、古い応答は捨てる
          if (message.date === selectedDateRef.current) setHourlyUsage(message.data)
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
          selectedDate={selectedDate}
          hourlyUsage={hourlyUsage}
          onSelectDate={changeSelectedDate}
        />
        <ActiveWindowChart
          activeWindowDurations={activeWindowDurations}
          dayOffset={dayOffset}
          onPrevDay={handlePrevDay}
          onNextDay={handleNextDay}
        />
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
            {activeWindowRecords.map((record, index) => (
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
