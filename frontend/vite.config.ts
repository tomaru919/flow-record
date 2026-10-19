import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react-swc'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  // DEBUG ビルドの WebView2 は http://localhost:5173 固定で読み込むので、
  // ポートが使用中でも別のポートにずらさず、起動エラーにする
  server: {
    port: 5173,
    strictPort: true,
  },
  build: {
    chunkSizeWarningLimit: 1000
  }
})
