import { createRouter, createWebHistory } from 'vue-router'
import { checkAuth, loadCurrentPeriod } from './lib/store'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', component: () => import('./views/LoginView.vue'), meta: { public: true } },
    { path: '/sso/callback', redirect: '/' },
    { path: '/', component: () => import('./views/TodayView.vue'), meta: { needsPeriod: true } },
    { path: '/month', component: () => import('./views/MonthView.vue'), meta: { needsPeriod: true } },
    { path: '/overview', component: () => import('./views/OverviewView.vue'), meta: { needsPeriod: true } },
    { path: '/assets', component: () => import('./views/AssetsView.vue') },
    { path: '/settings', component: () => import('./views/SettingsView.vue'), meta: { needsPeriod: true } },
    { path: '/:rest(.*)*', redirect: '/' },
  ],
})

router.beforeEach(async (to) => {
  if (to.meta.public) return true
  if (!(await checkAuth())) return { path: '/login', query: { redirect: to.fullPath } }
  if (to.meta.needsPeriod) await loadCurrentPeriod()
  return true
})
