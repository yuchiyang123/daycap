import { createApp } from 'vue'
import App from './App.vue'
import { router } from './router'
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
