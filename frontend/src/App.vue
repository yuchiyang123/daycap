<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted } from 'vue'
import { RouterLink, RouterView, useRoute } from 'vue-router'
import { store, loadCurrentPeriod } from './lib/store'
import { toIso } from './lib/format'

const route = useRoute()
const isPublic = computed(() => !!route.meta.public)
const needsPeriod = computed(() => !!route.meta.needsPeriod)

const tabs = [
  { to: '/', label: '今天' },
  { to: '/month', label: '本期' },
  { to: '/overview', label: '總覽' },
  { to: '/assets', label: '資產' },
  { to: '/settings', label: '設定' },
]

// PWA 常常整晚開著：回到前景時如果已經換日，重新抓本期資料。
function onVisible() {
  if (document.visibilityState !== 'visible' || !store.period) return
  if (store.period.today !== toIso(new Date())) loadCurrentPeriod(true)
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
        <nav class="top-nav" aria-label="主要">
          <RouterLink v-for="t in tabs" :key="t.to" :to="t.to" class="top-link">{{ t.label }}</RouterLink>
        </nav>
      </div>
    </header>

    <main>
      <div v-if="needsPeriod && store.periodError" class="page">
        <p class="error-box">讀不到本期資料：{{ store.periodError }}</p>
        <button class="btn" @click="loadCurrentPeriod(true)">重試</button>
      </div>
      <div v-else-if="needsPeriod && !store.period" class="page">
        <p class="muted">載入中</p>
      </div>
      <RouterView v-else :key="store.period?.id" />
    </main>

    <nav class="tabbar" aria-label="主要">
      <RouterLink v-for="t in tabs" :key="t.to" :to="t.to" class="tab">{{ t.label }}</RouterLink>
    </nav>
  </template>
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
.top-nav {
  display: flex;
  gap: 4px;
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
    grid-template-columns: repeat(5, 1fr);
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
</style>
