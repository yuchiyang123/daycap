<script setup lang="ts">
import { computed, ref } from 'vue'
import { allocatePreview } from '../api/endpoints'
import type { AllocationCell, CategoryDto, CategoryEstimate, SlotDto } from '../api/types'
import { money, signed } from '../lib/format'

/**
 * 餐費（每日類）設定表（§8.2）。同一張表兩種模式：
 * - 自己設定：直接填每格金額。即時合計超過額度就變紅，而且設定頁會擋住儲存。
 * - 自動分配：設權重、鎖定、底線、取整單位，由分配器 Allocator.Allocate 算金額。
 *   分配器由使用者實作（§8.1）；還沒實作時按鈕會顯示「分配器尚未實作」，金額維持手動值。
 */
const props = defineProps<{ cat: CategoryDto; est?: CategoryEstimate; weekdays: number; holidays: number; dayStart: string }>()
const emit = defineEmits<{ touch: [] }>()

const auto = computed(() => !!props.cat.auto?.enabled)
const budget = computed(() => props.est?.budget ?? 0)
const unit = computed(() => props.cat.auto?.roundingUnit || 1)

// ---- 即時合計（兩種模式都有，最先做）----
const wdSum = computed(() => props.cat.slots.reduce((a, s) => a + (Number(s.workdayAmount) || 0), 0))
const hdSum = computed(() => props.cat.slots.reduce((a, s) => a + (Number(s.holidayAmount) || 0), 0))
const wdTotal = computed(() => wdSum.value * props.weekdays)
const hdTotal = computed(() => hdSum.value * props.holidays)
const total = computed(() => wdTotal.value + hdTotal.value)
const remaining = computed(() => budget.value - total.value)

// ---- 時段 ----
const toMin = (t: string) => {
  const [h, m] = t.split(':').map(Number)
  return h * 60 + m
}
const fromMin = (x: number) => {
  const v = ((x % 1440) + 1440) % 1440
  return `${String(Math.floor(v / 60)).padStart(2, '0')}:${String(v % 60).padStart(2, '0')}`
}
function addSlot() {
  const last = props.cat.slots[props.cat.slots.length - 1]
  props.cat.slots.push({ id: 0, name: '', start: last ? fromMin(toMin(last.start) + 180) : props.dayStart, workdayAmount: 0, holidayAmount: 0, weight: 1 })
  emit('touch')
}
const slotEnd = (i: number) => props.cat.slots[i + 1]?.start ?? props.dayStart
const expanded = ref<number | null>(null)

// ---- 模式切換（§8.2）----
function toAuto() {
  // 以目前金額反推權重（每格 ÷ 基準格），切換當下數字不跳
  const base = Number(props.cat.slots[0]?.workdayAmount) || 1
  for (const s of props.cat.slots) {
    s.weight = round4((Number(s.workdayAmount) || 0) / base)
    s.holidayWeight = round4((Number(s.holidayAmount) || 0) / base)
  }
  props.cat.auto = { enabled: true, holidayMultiplier: props.cat.auto?.holidayMultiplier ?? 1.2, roundingUnit: props.cat.auto?.roundingUnit ?? 5 }
  emit('touch')
}
function toManual() {
  // 保留算出的金額當起點，解除所有鎖定
  for (const s of props.cat.slots) {
    s.workdayLock = null
    s.holidayLock = null
  }
  if (props.cat.auto) props.cat.auto.enabled = false
  emit('touch')
}
const round4 = (x: number) => Math.round(x * 10000) / 10000

// ---- 分配器 ----
function cells(override?: { key: string; lock: number }): AllocationCell[] {
  const m = props.cat.auto?.holidayMultiplier || 1
  return props.cat.slots.flatMap((s) => {
    const w = s.weight ?? 1
    const wk = `${s.id || `n${props.cat.slots.indexOf(s)}`}:w`
    const hk = `${s.id || `n${props.cat.slots.indexOf(s)}`}:h`
    return [
      { key: wk, days: props.weekdays, weight: w, lockedUnitAmount: override?.key === wk ? override.lock : s.workdayLock ?? null, floor: s.workdayFloor ?? null, tier: 0 },
      { key: hk, days: props.holidays, weight: s.holidayWeight ?? w * m, lockedUnitAmount: override?.key === hk ? override.lock : s.holidayLock ?? null, floor: s.holidayFloor ?? null, tier: 0 },
    ]
  })
}
const keyOf = (s: SlotDto, day: 'w' | 'h') => `${s.id || `n${props.cat.slots.indexOf(s)}`}:${day}`

const busy = ref(false)
const message = ref<string | null>(null)
const leftover = ref<number | null>(null)

async function runAllocator() {
  busy.value = true
  message.value = null
  try {
    const r = await allocatePreview(budget.value, cells(), unit.value)
    if (r.errors.length) {
      message.value = r.errors.join('；')
      return
    }
    for (const s of props.cat.slots) {
      s.workdayAmount = r.unitAmount[keyOf(s, 'w')] ?? s.workdayAmount
      s.holidayAmount = r.unitAmount[keyOf(s, 'h')] ?? s.holidayAmount
    }
    leftover.value = r.leftover
    if (r.warnings.length) message.value = r.warnings.join('；')
    emit('touch')
  } catch (e) {
    message.value = (e as Error).message
  } finally {
    busy.value = false
  }
}

// ---- 微調（＋／－）：以原值 ± 取整單位重算，其他未鎖定格吸收 ----
const tweak = ref<{ slot: SlotDto; day: 'w' | 'h'; delta: number; result: Record<string, number>; before: Record<string, number> } | null>(null)
async function nudge(s: SlotDto, day: 'w' | 'h', sign: 1 | -1) {
  const current = Number(day === 'w' ? s.workdayAmount : s.holidayAmount) || 0
  const next = current + sign * unit.value
  if (next < 0) return
  busy.value = true
  message.value = null
  try {
    const r = await allocatePreview(budget.value, cells({ key: keyOf(s, day), lock: next }), unit.value)
    if (r.errors.length) {
      message.value = r.errors.join('；')
      return
    }
    const before: Record<string, number> = {}
    for (const x of props.cat.slots) {
      before[keyOf(x, 'w')] = Number(x.workdayAmount) || 0
      before[keyOf(x, 'h')] = Number(x.holidayAmount) || 0
    }
    tweak.value = { slot: s, day, delta: sign * unit.value, result: r.unitAmount, before }
  } catch (e) {
    message.value = (e as Error).message
  } finally {
    busy.value = false
  }
}
/** 確認微調：新金額寫回，並反推成新權重（新權重 = 新金額 ÷ 每單位權重），不留下鎖定 */
function confirmTweak() {
  const t = tweak.value
  if (!t) return
  const first = props.cat.slots[0]
  const perWeight = (t.result[keyOf(first, 'w')] ?? Number(first.workdayAmount)) / (first.weight || 1) || 1
  for (const s of props.cat.slots) {
    s.workdayAmount = t.result[keyOf(s, 'w')] ?? s.workdayAmount
    s.holidayAmount = t.result[keyOf(s, 'h')] ?? s.holidayAmount
    s.weight = round4(Number(s.workdayAmount) / perWeight)
    s.holidayWeight = round4(Number(s.holidayAmount) / perWeight)
  }
  tweak.value = null
  emit('touch')
}
/** 畫面顯示其他格的實際變化（重算前後相減，不用比例推估） */
const tweakChanges = computed(() => {
  const t = tweak.value
  if (!t) return []
  return props.cat.slots.flatMap((s) =>
    (['w', 'h'] as const).map((d) => {
      const k = keyOf(s, d)
      return { label: `${s.name || '時段'}・${d === 'w' ? '平日' : '假日'}`, diff: (t.result[k] ?? 0) - (t.before[k] ?? 0) }
    }),
  ).filter((x) => x.diff !== 0)
})
</script>

<template>
  <div class="meal">
    <div class="mode-row">
      <div class="seg" role="group" aria-label="金額怎麼來">
        <button type="button" :aria-pressed="!auto" @click="auto && toManual()">自己設定</button>
        <button type="button" :aria-pressed="auto" @click="!auto && toAuto()">自動分配</button>
      </div>
      <template v-if="auto && cat.auto">
        <label class="mini">假日倍率 <input v-model.number="cat.auto.holidayMultiplier" class="input compact num" inputmode="decimal" @input="emit('touch')" /></label>
        <label class="mini">取整 <input v-model.number="cat.auto.roundingUnit" class="input compact num" inputmode="numeric" @input="emit('touch')" /></label>
        <button type="button" class="btn sm" :disabled="busy" @click="runAllocator">用分配器計算</button>
      </template>
    </div>

    <div class="st-row st-head muted">
      <span>時段</span><span>時間</span><span class="r">平日</span><span class="r">假日</span><span />
    </div>
    <template v-for="(s, si) in cat.slots" :key="si">
      <div class="st-row">
        <input v-model="s.name" class="input compact st-name" placeholder="例如 早餐" maxlength="40" aria-label="時段名稱" @input="emit('touch')" />
        <span class="st-time">
          <input v-model="s.start" type="time" class="input compact" step="1800" :disabled="si === 0" :aria-label="`${s.name || '時段'}開始時間`" @change="emit('touch')" />
          <span class="muted until num">到 {{ slotEnd(si) }}</span>
        </span>
        <span class="st-w cell">
          <span class="m-lab">平日</span>
          <button v-if="auto" type="button" class="nudge" :disabled="busy" aria-label="平日減" @click="nudge(s, 'w', -1)">−</button>
          <input v-model.number="s.workdayAmount" class="input compact num" inputmode="numeric" :readonly="auto" aria-label="平日金額" @input="emit('touch')" />
          <button v-if="auto" type="button" class="nudge" :disabled="busy" aria-label="平日加" @click="nudge(s, 'w', 1)">＋</button>
        </span>
        <span class="st-h cell">
          <span class="m-lab">假日</span>
          <button v-if="auto" type="button" class="nudge" :disabled="busy" aria-label="假日減" @click="nudge(s, 'h', -1)">−</button>
          <input v-model.number="s.holidayAmount" class="input compact num" inputmode="numeric" :readonly="auto" aria-label="假日金額" @input="emit('touch')" />
          <button v-if="auto" type="button" class="nudge" :disabled="busy" aria-label="假日加" @click="nudge(s, 'h', 1)">＋</button>
        </span>
        <span class="st-del">
          <button v-if="auto" type="button" class="btn quiet sm" @click="expanded = expanded === si ? null : si">{{ expanded === si ? '收起' : '進階' }}</button>
          <button type="button" class="btn quiet sm danger" :disabled="cat.slots.length === 1" @click="cat.slots.splice(si, 1); emit('touch')">移除</button>
        </span>
      </div>
      <div v-if="auto && expanded === si" class="adv">
        <label class="mini">權重 <input v-model.number="s.weight" class="input compact num" inputmode="decimal" @input="emit('touch')" /></label>
        <label class="mini">假日權重 <input v-model.number="s.holidayWeight" class="input compact num" inputmode="decimal" placeholder="權重×倍率" @input="emit('touch')" /></label>
        <label class="mini">平日鎖定 <input v-model.number="s.workdayLock" class="input compact num" inputmode="numeric" placeholder="不鎖" @input="emit('touch')" /></label>
        <label class="mini">假日鎖定 <input v-model.number="s.holidayLock" class="input compact num" inputmode="numeric" placeholder="不鎖" @input="emit('touch')" /></label>
        <label class="mini">平日底線 <input v-model.number="s.workdayFloor" class="input compact num" inputmode="numeric" placeholder="無" @input="emit('touch')" /></label>
        <label class="mini">假日底線 <input v-model.number="s.holidayFloor" class="input compact num" inputmode="numeric" placeholder="無" @input="emit('touch')" /></label>
      </div>
    </template>
    <button type="button" class="btn sm add" @click="addSlot">新增時段</button>

    <div class="totals num" :class="{ over: remaining < 0 }">
      <div>平日（{{ money(wdSum) }}）× {{ weekdays }} 天 = {{ money(wdTotal) }}</div>
      <div>假日（{{ money(hdSum) }}）× {{ holidays }} 天 = {{ money(hdTotal) }}</div>
      <div class="sum">
        合計 {{ money(total) }} / 額度 {{ money(budget) }}
        <b>{{ remaining >= 0 ? `剩 ${money(remaining)}（進待分配池）` : `超出 ${money(-remaining)}` }}</b>
      </div>
      <div v-if="auto" class="muted hint">
        平日 +{{ unit }} 需挪 {{ money(unit * weekdays) }}・假日 +{{ unit }} 需挪 {{ money(unit * holidays) }}
        <template v-if="leftover !== null">・分配零頭 {{ money(leftover) }}</template>
      </div>
    </div>

    <div v-if="tweak" class="tweak">
      <b>{{ tweak.slot.name }}・{{ tweak.day === 'w' ? '平日' : '假日' }} {{ signed(tweak.delta) }}：其他格的實際變化</b>
      <ul>
        <li v-for="c in tweakChanges" :key="c.label" class="num">{{ c.label }} <span :class="c.diff < 0 ? 'bad' : 'good'">{{ signed(c.diff) }}</span></li>
      </ul>
      <div class="tweak-actions">
        <button type="button" class="btn sm" @click="tweak = null">取消</button>
        <button type="button" class="btn sm primary" @click="confirmTweak">確認</button>
      </div>
    </div>

    <p v-if="message" class="warn-line">{{ message }}</p>
    <p class="muted small">第一個時段固定從 {{ dayStart }} 開始，每個時段到下一個開始為止，24 小時剛好切滿。</p>
  </div>
</template>

<style scoped>
.meal {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.mode-row {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
  margin-bottom: 4px;
}
.mini {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 12px;
  color: var(--ink-2);
}
.mini .input {
  width: 72px;
}
.st-row {
  display: grid;
  /* 最後一欄固定寬度：標題列那格是空的，用 auto 會讓標題和內容對不齊 */
  grid-template-columns: minmax(0, 1fr) 210px 120px 120px 96px;
  gap: 8px;
  align-items: center;
}
.st-head {
  font-size: 12px;
}
.r {
  text-align: right;
}
.st-time {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}
.st-time .input {
  /* 中文介面會顯示「上午 10:30」，太窄只看得到「上午」 */
  width: 130px;
  flex: none;
}
.until {
  font-size: 12px;
  white-space: nowrap;
}
.cell {
  display: flex;
  gap: 2px;
  align-items: center;
  min-width: 0;
}
.cell .input {
  flex: 1;
  min-width: 0;
}
.m-lab {
  display: none;
  font-size: 12px;
  color: var(--muted);
  margin-right: 4px;
  white-space: nowrap;
}
.cell .input[readonly] {
  background: var(--sunk);
}
.nudge {
  width: 26px;
  min-height: 30px;
  border: 1px solid var(--line-2);
  background: var(--surface);
  border-radius: 4px;
  cursor: pointer;
  font-size: 14px;
  padding: 0;
}
.st-del {
  display: flex;
  gap: 2px;
  justify-content: flex-end;
}
.adv {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 12px;
  padding: 6px 0 8px 8px;
  border-left: 2px solid var(--line-2);
}
.add {
  align-self: flex-start;
  margin-top: 4px;
}
.totals {
  margin-top: 6px;
  padding: 8px 10px;
  background: var(--sunk);
  border-left: 3px solid var(--ink-2);
  font-size: 13px;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.totals .sum {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
  margin-top: 2px;
}
.totals.over {
  border-left-color: var(--st-crit);
}
.totals.over .sum b {
  color: var(--bad-text);
}
.hint {
  font-size: 12px;
}
.tweak {
  border: 1px solid var(--line-2);
  border-radius: 4px;
  padding: 8px 10px;
  font-size: 13px;
}
.tweak ul {
  margin: 4px 0;
  padding-left: 18px;
}
.tweak-actions {
  display: flex;
  justify-content: flex-end;
  gap: 6px;
}
.warn-line {
  font-size: 13px;
  color: var(--warn-text);
}
.small {
  font-size: 12px;
}
@media (max-width: 640px) {
  .st-row {
    grid-template-columns: 1fr 1fr;
    grid-template-areas:
      'name name'
      'time time'
      'w h'
      '. del';
    padding-bottom: 8px;
    border-bottom: 1px solid var(--line);
  }
  .st-row.st-head {
    display: none;
  }
  .st-name {
    grid-area: name;
  }
  .st-time {
    grid-area: time;
    flex-wrap: wrap;
  }
  .m-lab {
    display: inline;
  }
  .st-time .input {
    flex: 1;
    width: auto;
  }
  .st-w {
    grid-area: w;
  }
  .st-h {
    grid-area: h;
  }
  .st-del {
    grid-area: del;
  }
}
</style>
