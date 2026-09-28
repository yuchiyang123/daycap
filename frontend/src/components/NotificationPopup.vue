<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import Sheet from './Sheet.vue'
import { markPopupShown } from '../api/endpoints'
import { store } from '../lib/store'

/**
 * 當天要跳出的通知（發薪日前 5 天提醒、結算結果），一次一則。
 * 關掉只代表「今天不再跳」，紅點會留著，要到鈴鐺裡看過才消失。
 */
const router = useRouter()
const current = computed(() => store.notifications.items.find((n) => n.showPopup) ?? null)

async function close(goTo?: string) {
  const n = current.value
  if (!n) return
  n.showPopup = false
  await markPopupShown(n.id).catch(() => undefined)
  if (goTo) router.push(goTo)
}
</script>

<template>
  <Sheet v-if="current" :title="current.title" @close="close()">
    <div class="body" :class="current.tone">
      <p v-for="(l, i) in current.lines" :key="i" :class="{ lead: i === 0 }">{{ l }}</p>
    </div>
    <div class="actions">
      <button v-if="current.kind === 'reminder' && current.tone === 'warn'" type="button" class="btn" @click="close('/overview')">去總覽調整</button>
      <button type="button" class="btn primary" @click="close()">知道了</button>
    </div>
  </Sheet>
</template>

<style scoped>
.body {
  display: flex;
  flex-direction: column;
  gap: 6px;
  font-size: 14px;
  color: var(--ink-2);
  border-left: 3px solid var(--ink-2);
  padding-left: 12px;
}
.body.warn {
  border-left-color: var(--st-crit);
}
.lead {
  color: var(--ink);
  font-size: 15px;
}
.actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
