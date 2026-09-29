import { createApp } from 'vue'
import App from './App.vue'
import { prefetchViews, router } from './router'
import { applyTheme } from './lib/theme'
import './style.css'
import { registerServiceWorker } from './lib/push'
import { initAppLock } from './lib/applock'
import { initOffline } from './lib/offline'
import { loadCurrentPeriod } from './lib/store'

applyTheme()
createApp(App).use(router).mount('#app')
registerServiceWorker()
initAppLock()
initOffline(() => loadCurrentPeriod(true))
// 第一個畫面出來後再預載其他頁，不跟它搶頻寬
router.isReady().then(() => {
  const idle = (window as unknown as { requestIdleCallback?: (cb: () => void) => void }).requestIdleCallback
  if (idle) idle(prefetchViews)
  else setTimeout(prefetchViews, 1500)
})
