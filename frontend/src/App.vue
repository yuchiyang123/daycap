<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted } from 'vue'
import { RouterLink, RouterView, useRoute } from 'vue-router'
import { store, loadCurrentPeriod, loadNotifications } from './lib/store'
import NotStarted from './components/NotStarted.vue'
import NotificationBell from './components/NotificationBell.vue'
import NotificationPopup from './components/NotificationPopup.vue'
import { logicalToday } from './lib/format'
import LockScreen from './components/LockScreen.vue'
import { lockState } from './lib/applock'
import { dismissFailed, offlineState } from './lib/offline'

const route = useRoute()
const keepAlive = ['TodayView', 'MonthView', 'LedgerView', 'OverviewView', 'AssetsView', 'SettingsView']
const isPublic = computed(() => !!route.meta.public)
const needsPeriod = computed(() => !!route.meta.needsPeriod)

const tabs = [
  { to: '/', label: '今天' },
  { to: '/month', label: '本期' },
  { to: '/ledger', label: '記帳' },
  { to: '/overview', label: '總覽' },
  { to: '/assets', label: '資產' },
  { to: '/settings', label: '設定' },
]

// PWA 常常整晚開著：回到前景時如果已經換日，重新抓本期資料。
async function onVisible() {
  if (document.visibilityState !== 'visible') return
  const today = logicalToday(store.period?.logicalDayStart)
  if (store.period && store.period.today !== today) await loadCurrentPeriod(true)
  // 等開始日期的時候，到了那天回到 app 就自動開始
  else if (store.notStarted && store.notStarted.startDate <= today) await loadCurrentPeriod(true)
  if (store.me) loadNotifications()
}
onMounted(() => document.addEventListener('visibilitychange', onVisible))
onBeforeUnmount(() => document.removeEventListener('visibilitychange', onVisible))
</script>

<template>
  <RouterView v-if="isPublic" />
  <template v-else>
    <header class="topbar">
      <div class="topbar-in">
        <RouterLink to="/" class="brand">日額</RouterLink>
        <div class="top-right">
          <nav class="top-nav" aria-label="主要">
            <RouterLink v-for="t in tabs" :key="t.to" :to="t.to" class="top-link">{{ t.label }}</RouterLink>
          </nav>
          <NotificationBell />
        </div>
      </div>
    </header>

    <div v-if="!offlineState.online || offlineState.pending" class="offline-bar" role="status">
      {{ !offlineState.online ? '離線中：滑卡確認會先記在這台，連線後自動送出。' : '' }}
      <template v-if="offlineState.pending">還有 {{ offlineState.pending }} 筆等著送出。</template>
    </div>
    <div v-if="offlineState.failed.length" class="offline-bar bad-bar" role="alert">
      離線時記的 {{ offlineState.failed.length }} 筆沒送出：{{ offlineState.failed.map((f) => `${f.label}（${f.message}）`).join('、') }}
      <button type="button" class="btn quiet sm" @click="dismissFailed">知道了</button>
    </div>

    <main>
      <div v-if="needsPeriod && store.periodError" class="page">
        <p class="error-box">讀不到本期資料：{{ store.periodError }}</p>
        <button class="btn" @click="loadCurrentPeriod(true)">重試</button>
      </div>
      <NotStarted v-else-if="needsPeriod && store.notStarted" :info="store.notStarted" />
      <div v-else-if="needsPeriod && !store.period" class="page">
        <p class="muted">載入中</p>
      </div>
      <!-- 主要分頁留在記憶體：切回來立刻顯示上次的畫面，資料在背景更新（每個請求經過 Tunnel 約 1 秒） -->
      <RouterView v-else v-slot="{ Component, route: r }">
        <KeepAlive :include="keepAlive" :max="6">
          <component :is="Component" :key="r.meta.needsPeriod ? `${r.path}-${store.period?.id ?? 0}` : r.path" />
        </KeepAlive>
      </RouterView>
    </main>

    <NotificationPopup />

    <nav class="tabbar" aria-label="主要">
      <RouterLink v-for="t in tabs" :key="t.to" :to="t.to" class="tab">{{ t.label }}</RouterLink>
    </nav>
  </template>
  <LockScreen v-if="lockState.locked && !isPublic" />
</template>

<style scoped>
.topbar {
  position: sticky;
  top: 0;
  z-index: 20;
  background: var(--page);
  border-bottom: 1px solid var(--line);
  padding-top: env(safe-area-inset-top, 0px);
}
.topbar-in {
  max-width: 1040px;
  margin: 0 auto;
  padding: 0 var(--gutter);
  height: 52px;
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.brand {
  font-weight: 700;
  font-size: 18px;
  letter-spacing: 0.14em;
  color: var(--ink);
  text-decoration: none;
}
.top-right {
  display: flex;
  align-items: center;
  gap: 8px;
}
.top-nav {
  display: flex;
  gap: 2px;
}
.top-link {
  color: var(--ink-2);
  text-decoration: none;
  font-size: 14px;
  padding: 6px 12px;
  border-radius: 4px;
}
.top-link:hover {
  background: var(--sunk);
}
.top-link.router-link-exact-active {
  color: var(--ink);
  font-weight: 600;
  border-bottom: 2px solid var(--accent);
  border-radius: 0;
  padding-bottom: 4px;
}

.tabbar {
  display: none;
}

@media (max-width: 719px) {
  .top-nav {
    display: none;
  }
  .topbar-in {
    height: 44px;
  }
  .tabbar {
    position: fixed;
    left: 0;
    right: 0;
    bottom: 0;
    z-index: 30;
    display: grid;
    grid-template-columns: repeat(6, 1fr);
    background: var(--surface);
    border-top: 1px solid var(--line);
    padding-bottom: env(safe-area-inset-bottom, 0px);
  }
  .tab {
    height: var(--nav-h);
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 14px;
    color: var(--muted);
    text-decoration: none;
    position: relative;
  }
  .tab.router-link-exact-active {
    color: var(--ink);
    font-weight: 600;
  }
  .tab.router-link-exact-active::before {
    content: '';
    position: absolute;
    top: -1px;
    left: 30%;
    right: 30%;
    height: 2px;
    background: var(--accent);
  }
}
.offline-bar {
  padding: 6px 16px;
  font-size: 13px;
  background: var(--sunk);
  border-bottom: 1px solid var(--line);
}
.bad-bar {
  color: var(--bad-text);
}
</style>
