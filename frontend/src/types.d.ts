export interface ActiveWindowRecord {
  window_title: string
  event_type: string
  start_time: string
  end_time: string
}

export interface BootDuration {
  date: string
  total_hours: number
}

export interface ActiveWindowDuration {
  window_title: string
  duration_hours: number
}

export interface WebViewMessage {
  type: string
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  data: any
}

export interface WebViewMessageEvent {
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
