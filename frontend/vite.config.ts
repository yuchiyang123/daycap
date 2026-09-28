import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// 本機開發：/api/auth 轉給 Mini-SSO（:12080，有跑才會通），其餘 /api 轉給 DayCap.Api（:5230）。
// 後端 Development 模式預設開 DevAuth，沒跑 Mini-SSO 也能直接用。
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5190,
    strictPort: true,
    proxy: {
      '/api/auth': { target: 'http://localhost:12080', changeOrigin: true },
      '/api': { target: 'http://localhost:5230', changeOrigin: true },
    },
  },
})
