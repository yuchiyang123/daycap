import { createApp } from 'vue'
import App from './App.vue'
import { router } from './router'
import { applyTheme } from './lib/theme'
import './style.css'
import { registerServiceWorker } from './lib/push'

applyTheme()
createApp(App).use(router).mount('#app')
registerServiceWorker()
