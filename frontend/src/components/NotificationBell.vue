<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { readAllNotifications } from '../api/endpoints'
import { store } from '../lib/store'

/**
 * 右上角鈴鐺：有未讀時一個紅點；點開看過就全部標成已讀、紅點消失。
 * 圖示是一條線畫的平面鈴鐺，不用任何圖示字型或 emoji。
 */
const open = ref(false)
const root = ref<HTMLElement | null>(null)

async function toggle() {
  open.value = !open.value
  if (open.value && store.notifications.unread > 0) {
    // 先讓使用者看到哪幾則是新的，再在背景標成已讀
    await readAllNotifications().catch(() => undefined)
    store.notifications.unread = 0
  }
  if (!open.value) store.notifications.items.forEach((n) => (n.read = true))
}

function onDoc(e: MouseEvent) {
  if (open.value && root.value && !root.value.contains(e.target as Node)) {
    open.value = false
    store.notifications.items.forEach((n) => (n.read = true))
  }
}
onMounted(() => document.addEventListener('click', onDoc))
onBeforeUnmount(() => document.removeEventListener('click', onDoc))

function when(iso: string) {
  const d = new Date(iso)
  return `${d.getMonth() + 1}/${d.getDate()}`
}
</script>

<template>
  <div ref="root" class="bell-wrap">
    <button
      type="button"
      class="bell"
      :aria-label="store.notifications.unread ? `通知（${store.notifications.unread} 則未讀）` : '通知'"
      :aria-expanded="open"
      @click="toggle"
    >
      <svg width="20" height="20" viewBox="0 0 20 20" aria-hidden="true">
        <path
          d="M10 2.5c-2.8 0-4.75 2.1-4.75 4.9v2.9L3.6 13.2c-.25.45.07 1 .58 1h11.64c.51 0 .83-.55.58-1l-1.65-2.9V7.4c0-2.8-1.95-4.9-4.75-4.9Z"
          fill="none"
          stroke="currentColor"
          stroke-width="1.5"
          stroke-linejoin="round"
        />
        <path d="M8 16.2a2 2 0 0 0 4 0" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" />
      </svg>
      <span v-if="store.notifications.unread" class="dot" />
    </button>

    <div v-if="open" class="panel drop" role="dialog" aria-label="通知">
      <div class="drop-head">通知</div>
      <p v-if="store.notifications.items.length === 0" class="empty">沒有通知。</p>
      <ul v-else class="list">
        <li v-for="n in store.notifications.items" :key="n.id" class="item" :class="{ unread: !n.read }">
          <div class="it-head">
            <b>{{ n.title }}</b>
            <span class="muted num">{{ when(n.createdAt) }}</span>
          </div>
          <p v-for="(l, i) in n.lines" :key="i" :class="{ first: i === 0 }">{{ l }}</p>
        </li>
      </ul>
    </div>
  </div>
</template>

<style scoped>
.bell-wrap {
  position: relative;
}
.bell {
  position: relative;
  width: 36px;
  height: 36px;
  display: grid;
  place-items: center;
  border: 0;
  background: none;
  color: var(--ink-2);
  cursor: pointer;
  border-radius: 4px;
}
.bell:hover {
  background: var(--sunk);
  color: var(--ink);
}
.dot {
  position: absolute;
  top: 6px;
  right: 7px;
  width: 9px;
  height: 9px;
  border-radius: 50%;
  background: var(--st-crit);
  border: 2px solid var(--page);
}
.drop {
  position: absolute;
  right: 0;
  top: 42px;
  width: min(360px, calc(100vw - 24px));
  max-height: 70dvh;
  overflow-y: auto;
  z-index: 40;
}
@media (max-width: 480px) {
  .drop {
    position: fixed;
    left: 12px;
    right: 12px;
    top: calc(48px + env(safe-area-inset-top, 0px));
    width: auto;
  }
}
.drop-head {
  padding: 10px 14px;
  font-size: 13px;
  font-weight: 600;
  border-bottom: 1px solid var(--line);
}
.item {
  padding: 12px 14px;
  font-size: 13px;
  color: var(--ink-2);
  display: flex;
  flex-direction: column;
  gap: 3px;
}
.item.unread {
  border-left: 3px solid var(--st-crit);
  padding-left: 11px;
}
.it-head {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  color: var(--ink);
  font-size: 14px;
}
.first {
  color: var(--ink);
}
</style>
