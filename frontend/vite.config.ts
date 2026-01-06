import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react-swc'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../FlowRecord/bin/Release/net10.0-windows/win-x64/publish/wwwroot',
    emptyOutDir: true,
  }
})
