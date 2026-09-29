<script setup lang="ts">
import { computed, ref } from 'vue'
import type { DayView, SlotView } from '../api/types'
import { store, setPeriod } from '../lib/store'
import { deleteEntry, setDay } from '../api/endpoints'
import { money, signed } from '../lib/format'

/**
 * 一天的檢核清單：每個時段一列，點下去回報。沒回報 = 照預算。
 * 下面是這天的額外花費，可以刪除。
 */
const props = defineProps<{ day: DayView }>()
const emit = defineEmits<{ report: [slot: SlotView, categoryName: string]; extra: [] }>()

const period = computed(() => store.period!)
const catName = (id: number) => period.value.categories.find((c) => c.categoryId === id)?.name ?? '—'

const groups = computed(() => {
  const map = new Map<number, SlotView[]>()
  for (const s of props.day.slots) {
    if (!map.has(s.categoryId)) map.set(s.categoryId, [])
    map.get(s.categoryId)!.push(s)
  }
  return [...map.entries()].map(([id, slots]) => ({
    id,
    name: catName(id),
    slots,
    planned: slots.reduce((a, s) => a + s.planned, 0),
  }))
})

const extras = computed(() =>
  props.day.extraEntryIds.map((id) => period.value.entries.find((e) => e.id === id)).filter((e) => !!e),
)

function status(s: SlotView): { text: string; tone: string } {
  if (s.actual === null) {
    return props.day.status === 'future' ? { text: '還沒到', tone: 'muted' } : { text: '照預算', tone: 'muted' }
  }
  const diff = s.actual - s.planned
  if (diff === 0) return { text: `實際 ${money(s.actual)}・剛好`, tone: 'ink-2' }
  return diff > 0
    ? { text: `實際 ${money(s.actual)}・超支 ${money(diff)}`, tone: 'bad' }
    : { text: `實際 ${money(s.actual)}・少花 ${money(-diff)}`, tone: 'good' }
}

const removing = ref<number | null>(null)

// ---- 全天例外（§9.3）：今天全部 0 / 今天全部照預算，按兩次確認 ----
const hasUnreported = computed(() => props.day.slots.some((s) => s.actual === null))
const armed = ref<'zero' | 'planned' | null>(null)
async function wholeDay(mode: 'zero' | 'planned') {
  if (armed.value !== mode) {
    armed.value = mode
    setTimeout(() => {
      if (armed.value === mode) armed.value = null
    }, 3000)
    return
  }
  armed.value = null
  setPeriod(await setDay(period.value.id, props.day.date, mode))
}
async function removeExtra(id: number) {
  if (removing.value !== id) {
    removing.value = id
    setTimeout(() => {
      if (removing.value === id) removing.value = null
    }, 3000)
    return
  }
  setPeriod(await deleteEntry(period.value.id, id))
  removing.value = null
}
</script>

<template>
  <div class="day">
    <div v-if="groups.length === 0" class="panel empty">這天沒有排每日額度。</div>
    <div v-else-if="hasUnreported && day.status !== 'future' && !period.closed" class="whole-day">
      <button type="button" class="btn sm" @click="wholeDay('zero')">{{ armed === 'zero' ? '確認：這天全部 0' : '這天全部 0' }}</button>
      <button type="button" class="btn sm" @click="wholeDay('planned')">{{ armed === 'planned' ? '確認：全部照預算' : '這天全部照預算' }}</button>
      <span class="muted small">在家吃、有人請客就按「全部 0」</span>
    </div>

    <div v-for="g in groups" :key="g.id" class="panel">
      <div class="g-head">
        <span class="g-name">{{ g.name }}</span>
        <span class="num muted">這天 {{ money(g.planned) }}</span>
      </div>
      <ul class="list">
        <li v-for="s in g.slots" :key="s.slotId">
          <button type="button" class="slot" @click="emit('report', s, g.name)">
            <span class="s-main">
              <span class="s-name">{{ s.name }}</span>
              <span class="s-status" :class="status(s).tone">{{ status(s).text }}</span>
            </span>
            <span class="s-amt num">
              <b>{{ money(s.planned) }}</b>
              <span v-if="s.planned !== s.basePlanned" class="orig">原 {{ money(s.basePlanned) }}</span>
            </span>
            <span class="s-go">{{ s.actual === null ? '回報' : '修改' }}</span>
          </button>
        </li>
      </ul>
    </div>

    <div class="panel">
      <div class="g-head">
        <span class="g-name">額外花費</span>
        <button type="button" class="btn sm" @click="emit('extra')">新增</button>
      </div>
      <ul v-if="extras.length" class="list">
        <li v-for="e in extras" :key="e!.id" class="extra">
          <span class="s-main">
            <span class="s-name">
              {{ e!.note || e!.categoryName }}
              <span v-if="e!.isSubscription" class="tag accent">訂閱</span>
            </span>
            <span class="s-status muted">
              {{ e!.categoryName }}
              <template v-if="e!.fromPool">・待分配池 {{ signed(-e!.fromPool) }}</template>
              <template v-if="e!.spread">・之後 {{ e!.spreadDays }} 天每天少約 {{ money(e!.spreadPerDay) }}</template>
              <template v-if="e!.envelopeOver">・超出月額度 {{ money(e!.envelopeOver) }}</template>
            </span>
          </span>
          <span class="s-amt num"><b>{{ money(e!.actual) }}</b></span>
          <button type="button" class="btn quiet sm danger" @click="removeExtra(e!.id)">
            {{ removing === e!.id ? '確認刪除' : '刪除' }}
          </button>
        </li>
      </ul>
      <p v-else class="empty">沒有額外花費。</p>
    </div>
  </div>
</template>

<style scoped>
.day {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.whole-day {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
}
.small {
  font-size: 12px;
}
.g-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 10px 16px;
  border-bottom: 1px solid var(--line);
  font-size: 13px;
}
.g-name {
  font-weight: 600;
  letter-spacing: 0.04em;
}
.slot,
.extra {
  width: 100%;
  display: grid;
  grid-template-columns: 1fr auto auto;
  align-items: center;
  gap: 12px;
  padding: 12px 16px;
  min-height: 56px;
  text-align: left;
  background: none;
  border: 0;
}
.slot {
  cursor: pointer;
}
.slot:hover {
  background: var(--sunk);
}
.s-main {
  display: flex;
  flex-direction: column;
  min-width: 0;
}
.s-name {
  font-size: 15px;
  font-weight: 500;
}
.s-status {
  font-size: 12px;
}
.s-amt {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
}
.s-amt b {
  font-size: 17px;
  font-weight: 600;
}
.orig {
  font-size: 11px;
  color: var(--muted);
  text-decoration: line-through;
}
.s-go {
  font-size: 13px;
  color: var(--accent);
  font-weight: 500;
  min-width: 28px;
  text-align: right;
}
</style>
