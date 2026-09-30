<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { createEntry, deleteEntry } from '../api/endpoints'
import type { SlotView } from '../api/types'
import { setPeriod, store } from '../lib/store'
import { enqueue, isNetworkError } from '../lib/offline'
import { haptic } from '../lib/haptics'
import HapticButton from './HapticButton.vue'
import { money, shortDate } from '../lib/format'

/**
 * 滑卡確認（§9.2）：已經到時間、還沒回報的時段疊成卡片。
 * 右滑＝照預算，左滑＝修正（開回報畫面）。拖過卡片寬度 35% 才算數，放手不夠就彈回。
 * 滑完底部顯示幾秒「已記… ［復原］」。昨天沒回報的也會出現，可以確認到前一個邏輯日（§9.4）。
 * 發票卡、對帳差額卡：發票（§19）還沒做；對帳差額在對帳當下就處理掉，所以目前只有時段卡。
 */
const emit = defineEmits<{ correct: [slot: SlotView, categoryName: string, date: string] }>()

const period = computed(() => store.period!)
const catName = (id: number) => period.value.categories.find((c) => c.categoryId === id)?.name ?? ''

function minutes(hhmm: string) {
  const [h, m] = hhmm.split(':').map(Number)
  return (h || 0) * 60 + (m || 0)
}
/** 邏輯日內經過了幾分鐘（跨午夜的時段也對得上） */
const sinceDayStart = (hhmm: string) => (minutes(hhmm) - minutes(period.value.logicalDayStart) + 1440) % 1440
const nowTick = ref(Date.now())
const timer = setInterval(() => (nowTick.value = Date.now()), 60_000)
onBeforeUnmount(() => clearInterval(timer))

interface Card {
  key: string
  date: string
  slot: SlotView
  categoryName: string
}
const skipped = ref(new Set<string>())
const cards = computed<Card[]>(() => {
  if (period.value.closed) return []
  const d = new Date(nowTick.value)
  const now = sinceDayStart(`${d.getHours()}:${d.getMinutes()}`)
  const todayIdx = period.value.days.findIndex((x) => x.status === 'today')
  if (todayIdx < 0) return []
  const out: Card[] = []
  for (const day of period.value.days.slice(Math.max(0, todayIdx - 1), todayIdx + 1)) {
    const isToday = day.status === 'today'
    for (const s of day.slots) {
      if (s.actual !== null || s.planned <= 0) continue
      if (isToday && s.start && sinceDayStart(s.start) > now) continue
      const key = `${day.date}-${s.categoryId}-${s.slotId}`
      if (skipped.value.has(key)) continue
      out.push({ key, date: day.date, slot: s, categoryName: catName(s.categoryId) })
    }
  }
  return out
})
const top = computed(() => cards.value[0] ?? null)

// ---- 拖曳 ----
const dx = ref(0)
const dy = ref(0)
const dragging = ref(false)
const flying = ref<'left' | 'right' | null>(null)
let startX = 0
let startY = 0
let width = 300
const THRESHOLD = 0.35
/** 拖過門檻（放手就會成立）時輕震一下；拉回來再拉過去會再震 */
let pastThreshold = false
/** iPhone 只在 touchend / click 裡允許觸覺回饋：pointerup 判定成立後，touchend 再補震一次（重複的會被略過） */
let hapticPending = false
function onTouchEnd() {
  if (!hapticPending) return
  hapticPending = false
  haptic('confirm')
}

function down(e: PointerEvent) {
  if (busy.value || flying.value) return
  dragging.value = true
  startX = e.clientX
  startY = e.clientY
  width = (e.currentTarget as HTMLElement).offsetWidth || 300
  pastThreshold = false
  ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
}
function move(e: PointerEvent) {
  if (!dragging.value) return
  dx.value = e.clientX - startX
  dy.value = (e.clientY - startY) * 0.3
  const past = Math.abs(dx.value / width) > THRESHOLD
  if (past && !pastThreshold) haptic('tick')
  pastThreshold = past
}
function up() {
  if (!dragging.value) return
  dragging.value = false
  const ratio = dx.value / width
  if (ratio > THRESHOLD) fling('right')
  else if (ratio < -THRESHOLD) fling('left')
  else {
    dx.value = 0
    dy.value = 0
  }
}
const intent = computed(() => {
  const r = dx.value / width
  if (r > 0.12) return 'right'
  if (r < -0.12) return 'left'
  return null
})
const cardStyle = computed(() => {
  const x = flying.value === 'right' ? width * 1.4 : flying.value === 'left' ? -width * 1.4 : dx.value
  return {
    transform: `translate(${x}px, ${dy.value}px) rotate(${x / 24}deg)`,
    transition: dragging.value ? 'none' : 'transform 0.22s ease-out',
  }
})

function fling(dir: 'left' | 'right') {
  const card = top.value
  if (!card) return
  haptic('confirm')
  hapticPending = true
  flying.value = dir
  setTimeout(async () => {
    flying.value = null
    dx.value = 0
    dy.value = 0
    if (dir === 'right') await confirm(card)
    else {
      skipped.value.add(card.key) // 修正畫面關掉之前先移出疊；回報後自然不會再出現
      skipped.value = new Set(skipped.value)
      emit('correct', card.slot, card.categoryName, card.date)
    }
  }, 180)
}

// ---- 確認 / 復原 ----
const busy = ref(false)
const toast = ref<{ text: string; entryIds: number[]; keys: string[] } | null>(null)
let toastTimer: ReturnType<typeof setTimeout> | undefined
function showToast(text: string, entryIds: number[], keys: string[]) {
  clearTimeout(toastTimer)
  toast.value = { text, entryIds, keys }
  toastTimer = setTimeout(() => (toast.value = null), 5000)
}

async function report(card: Card): Promise<number | null> {
  const body = {
    date: card.date,
    categoryId: card.slot.categoryId,
    slotId: card.slot.slotId,
    inputMode: 'Actual' as const,
    amount: card.slot.planned,
    usePool: true,
    note: null,
    subscription: null,
  }
  let view
  try {
    view = await createEntry(period.value.id, body)
  } catch (e) {
    if (!isNetworkError(e)) throw e
    // 離線（§21.4）：先排隊，卡片先拿掉，連線後自動送出
    enqueue(period.value.id, body, `${card.slot.name} ${money(card.slot.planned)}`)
    skipped.value.add(card.key)
    skipped.value = new Set(skipped.value)
    return null
  }
  setPeriod(view)
  return view.days.find((d) => d.date === card.date)?.slots.find((s) => s.slotId === card.slot.slotId && s.categoryId === card.slot.categoryId)?.entryId ?? null
}

async function confirm(card: Card) {
  busy.value = true
  try {
    const id = await report(card)
    showToast(`已記 ${card.slot.name} ${money(card.slot.planned)}`, id ? [id] : [], [card.key])
  } finally {
    busy.value = false
  }
}

async function confirmAll() {
  const all = [...cards.value]
  if (!all.length) return
  haptic('confirm')
  busy.value = true
  const ids: number[] = []
  try {
    for (const c of all) {
      const id = await report(c)
      if (id) ids.push(id)
    }
    showToast(`已照預算確認 ${all.length} 個時段`, ids, all.map((c) => c.key))
  } finally {
    busy.value = false
  }
}

async function undo() {
  const t = toast.value
  if (!t) return
  toast.value = null
  busy.value = true
  try {
    for (const id of [...t.entryIds].reverse()) setPeriod(await deleteEntry(period.value.id, id))
  } finally {
    busy.value = false
  }
}

function dateTag(c: Card) {
  return c.date === period.value.today ? '今天' : `昨天 ${shortDate(c.date)}`
}
</script>

<template>
  <section v-if="top || toast" class="swipe">
    <div v-if="top" class="swipe-head">
      <span class="muted small">待確認 {{ cards.length }} 個時段・右滑照預算，左滑修正</span>
      <HapticButton v-if="cards.length > 1" class="quiet sm" :disabled="busy" @press="confirmAll">全部照預算</HapticButton>
    </div>

    <div v-if="top" class="deck">
      <div v-if="cards[1]" class="card under" aria-hidden="true"></div>
      <div
        :key="top.key"
        class="card"
        :class="{ right: intent === 'right', left: intent === 'left' }"
        :style="cardStyle"
        @pointerdown="down"
        @pointermove="move"
        @pointerup="up"
        @pointercancel="up"
        @touchend="onTouchEnd"
      >
        <span class="edge edge-r">→ 照預算</span>
        <span class="edge edge-l">改金額 ←</span>
        <span class="muted small">{{ dateTag(top) }}・{{ top.categoryName }}<template v-if="top.slot.start">・{{ top.slot.start }}</template></span>
        <b class="c-name">{{ top.slot.name }}</b>
        <span class="c-amt num">{{ money(top.slot.planned) }}</span>
        <span class="muted small">花的剛好是這個數就右滑</span>
      </div>
    </div>
    <div v-if="top" class="swipe-btns">
      <HapticButton :disabled="busy" @press="fling('left')">修正</HapticButton>
      <HapticButton class="primary" :disabled="busy" @press="fling('right')">照預算</HapticButton>
    </div>

    <div v-if="toast" class="toast" role="status">
      <span>{{ toast.text }}</span>
      <button v-if="toast.entryIds.length" type="button" class="btn quiet sm" :disabled="busy" @click="undo">復原</button>
    </div>
  </section>
</template>

<style scoped>
.swipe {
  display: flex;
  flex-direction: column;
  gap: 10px;
  /* 卡片滑出去的部分裁掉，不然會把整頁撐寬、出現左右捲動 */
  overflow-x: clip;
  overflow-y: visible;
}
.swipe-head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 8px;
}
.small {
  font-size: 12px;
}
.deck {
  position: relative;
  /* 和螢幕邊緣留距離，避開 iOS 邊緣返回手勢 */
  margin: 0 20px;
  height: 168px;
}
.card {
  position: absolute;
  inset: 0;
  background: var(--surface);
  border: 1px solid var(--ink);
  border-radius: 6px;
  padding: 16px 18px;
  display: flex;
  flex-direction: column;
  gap: 4px;
  touch-action: pan-y;
  user-select: none;
  cursor: grab;
  overflow: hidden;
}
.card.under {
  transform: translateY(6px) scale(0.97);
  border-color: var(--line);
  cursor: default;
}
.card.right {
  border-color: var(--good-text);
}
.card.left {
  border-color: var(--accent);
}
.c-name {
  font-size: 20px;
  font-weight: 600;
}
.c-amt {
  font-size: 30px;
  font-weight: 600;
}
.edge {
  position: absolute;
  top: 14px;
  font-size: 13px;
  font-weight: 600;
  opacity: 0;
  transition: opacity 0.1s;
}
.edge-r {
  right: 16px;
  color: var(--good-text);
}
.edge-l {
  right: 16px;
  color: var(--accent);
}
.card.right .edge-r,
.card.left .edge-l {
  opacity: 1;
}
.swipe-btns {
  display: flex;
  gap: 8px;
  margin: 0 20px;
}
.swipe-btns .btn {
  flex: 1;
}
.toast {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border: 1px solid var(--line);
  border-radius: 4px;
  background: var(--sunk);
  font-size: 14px;
}
</style>
