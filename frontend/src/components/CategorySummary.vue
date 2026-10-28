<script setup lang="ts">
import { computed } from 'vue'
import Meter from '../charts/Meter.vue'
import type { CategoryView, PeriodView } from '../api/types'
import { money } from '../lib/format'

/**
 * 分類總覽：預計會花（已花 + 之後的排程）對照整期額度。
 * 超支攤提後，之後的排程會變少，所以「預計」會回到額度內——這條就是連動的結果。
 */
const props = defineProps<{ category: CategoryView; period: PeriodView }>()

const c = computed(() => props.category)
// 少花的錢已經轉進待分配池，分類的實際額度要扣掉，不然「剩」會跟池子重複算（設定裡的額度不變）
const toPool = computed(() => c.value.toPool ?? 0)
const budget = computed(() => c.value.budget - toPool.value)
const remaining = computed(() => budget.value - c.value.projected)
const daysLeft = computed(() => props.period.days.filter((d) => d.status === 'future').length)
const perDay = computed(() => (daysLeft.value > 0 ? Math.round(c.value.plannedRemaining / daysLeft.value) : 0))
// 每日額度類本來就會照排程把額度用完，所以只看「預計會不會超過」；
// 月額度類才看用掉幾成（超過 85% 提醒）。
const state = computed(() => {
  const used = c.value.mode === 'Daily' ? c.value.projected : c.value.spent
  if (used > budget.value) return { text: '會超過', tone: 'bad', level: 'over' as const }
  if (c.value.mode === 'Daily') return { text: '照計畫', tone: 'muted', level: 'ok' as const }
  const r = budget.value > 0 ? used / budget.value : 0
  return r > 0.85 ? { text: '接近', tone: 'warn', level: 'near' as const } : { text: '正常', tone: 'muted', level: 'ok' as const }
})
</script>

<template>
  <div class="cat">
    <div class="top">
      <span class="name">{{ c.name }}</span>
      <span class="num">
        <b>{{ money(c.mode === 'Daily' ? c.projected : c.spent) }}</b>
        <span class="muted"> / {{ money(budget) }}</span>
      </span>
    </div>
    <Meter :value="c.mode === 'Daily' ? c.projected : c.spent" :max="budget" :level="state.level" />
    <div class="bottom num">
      <span :class="state.tone">{{ state.text }}・剩 {{ money(remaining) }}</span>
      <span v-if="c.mode === 'Daily'" class="muted">已花 {{ money(c.spent) }}・之後每天約 {{ money(perDay) }}</span>
      <span v-else-if="c.mode === 'Envelope'" class="muted">月額度</span>
      <span v-else class="muted">固定</span>
    </div>
    <div v-if="toPool !== 0" class="pool-note muted num">
      <template v-if="toPool > 0">額度 {{ money(c.budget) }}，少花的 {{ money(toPool) }} 已轉進待分配池</template>
      <template v-else>額度 {{ money(c.budget) }}，超支的 {{ money(-toPool) }} 由待分配池補</template>
    </div>
  </div>
</template>

<style scoped>
.cat {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 12px 16px;
}
.top,
.bottom {
  display: flex;
  justify-content: space-between;
  align-items: baseline;
  gap: 8px;
  flex-wrap: wrap;
}
.name {
  font-weight: 600;
}
.top b {
  font-size: 16px;
  font-weight: 600;
}
.bottom,
.pool-note {
  font-size: 12px;
}
</style>
