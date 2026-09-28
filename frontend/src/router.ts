import { createRouter, createWebHistory } from 'vue-router'
import { checkAuth, loadCurrentPeriod, loadNotifications } from './lib/store'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', component: () => import('./views/LoginView.vue'), meta: { public: true } },
    { path: '/sso/callback', redirect: '/' },
    { path: '/', component: () => import('./views/TodayView.vue'), meta: { needsPeriod: true } },
    { path: '/month', component: () => import('./views/MonthView.vue'), meta: { needsPeriod: true } },
    { path: '/ledger', component: () => import('./views/LedgerView.vue'), meta: { needsPeriod: true } },
    { path: '/overview', component: () => import('./views/OverviewView.vue'), meta: { needsPeriod: true } },
    { path: '/assets', component: () => import('./views/AssetsView.vue') },
    // 設定頁沒有本期也要能用（還沒到開始日期時就是在這裡設定）
    { path: '/settings', component: () => import('./views/SettingsView.vue'), meta: { wantsPeriod: true } },
    { path: '/:rest(.*)*', redirect: '/' },
  ],
})

let notificationsLoaded = false

router.beforeEach(async (to) => {
  if (to.meta.public) return true
  if (!(await checkAuth())) return { path: '/login', query: { redirect: to.fullPath } }
  if (to.meta.needsPeriod || to.meta.wantsPeriod) await loadCurrentPeriod()
  // 通知要等本期讀完再讀：兩個請求同時都可能需要建立新的一期
  if (!notificationsLoaded) {
    notificationsLoaded = true
    loadNotifications()
  }
  return true
})
