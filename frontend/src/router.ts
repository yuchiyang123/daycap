import { createRouter, createWebHistory } from 'vue-router'
import { checkAuth, loadCurrentPeriod, loadNotifications, store } from './lib/store'

// 每一頁的程式分開載入；經過 Cloudflare Tunnel 每個請求約 1 秒，第一次切到某頁會卡在下載。
// app 開好後趁空閒先全部預載（service worker 也會快取起來），切頁就不用等。
const views = {
  today: () => import('./views/TodayView.vue'),
  month: () => import('./views/MonthView.vue'),
  ledger: () => import('./views/LedgerView.vue'),
  overview: () => import('./views/OverviewView.vue'),
  assets: () => import('./views/AssetsView.vue'),
  settings: () => import('./views/SettingsView.vue'),
  monthEnd: () => import('./views/MonthEndView.vue'),
}

export function prefetchViews() {
  for (const load of Object.values(views)) void load().catch(() => undefined)
}

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', component: () => import('./views/LoginView.vue'), meta: { public: true } },
    { path: '/sso/callback', redirect: '/' },
    { path: '/welcome', component: () => import('./views/WelcomeView.vue') },
    { path: '/', component: views.today, meta: { needsPeriod: true } },
    { path: '/month', component: views.month, meta: { needsPeriod: true } },
    { path: '/ledger', component: views.ledger, meta: { needsPeriod: true } },
    { path: '/overview', component: views.overview, meta: { needsPeriod: true } },
    { path: '/month-end/:periodId', component: views.monthEnd },
    { path: '/assets', component: views.assets },
    // 設定頁沒有本期也要能用（還沒到開始日期時就是在這裡設定）
    { path: '/settings', component: views.settings, meta: { wantsPeriod: true } },
    { path: '/:rest(.*)*', redirect: '/' },
  ],
})

let notificationsLoaded = false

router.beforeEach(async (to) => {
  if (to.meta.public) return true
  if (!(await checkAuth())) return { path: '/login', query: { redirect: to.fullPath } }
  // 新使用者先走引導（§20）；在引導畫面時不要去建立期間
  if (store.me && !store.me.onboarded && to.path !== '/welcome') return '/welcome'
  if (to.path === '/welcome') return true
  if (to.meta.needsPeriod || to.meta.wantsPeriod) await loadCurrentPeriod()
  // 通知要等本期讀完再讀：兩個請求同時都可能需要建立新的一期
  if (!notificationsLoaded) {
    notificationsLoaded = true
    loadNotifications()
  }
  return true
})
