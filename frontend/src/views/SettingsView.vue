<script setup lang="ts">
import Skeleton from '../components/Skeleton.vue'
import { computed, onActivated, onMounted, ref } from 'vue'
import AllocationBar, { type Segment } from '../charts/AllocationBar.vue'
import MealTable from '../components/MealTable.vue'
import PushSettings from '../components/PushSettings.vue'
import SubItemsEditor from '../components/SubItemsEditor.vue'
import TrustSettings from '../components/TrustSettings.vue'
import { watch } from 'vue'
import { estimateSettings } from '../api/endpoints'
import type { PercentBase, SettingsEstimate } from '../api/types'
import { deleteBeforeStart, getCalendar, getSettings, saveSettings, setDayOverride, logout } from '../api/endpoints'
import type { BudgetMode, CalendarDayDto, CategoryDto, CategoryGroup, HolidayShift, SettingsDto, SettingsVersionSummary } from '../api/types'
import { store, loadCurrentPeriod } from '../lib/store'
import { dayLabel, groupLabel, money, parseDate, pct, shortDate } from '../lib/format'
import { applyTheme, currentTheme, type ThemeChoice } from '../lib/theme'

/**
 * 設定版本化（§4.1）：每次儲存都新增一個版本，一般從明天起生效；
 * 今天還沒回報、而且調低的時段今天就生效（取較低值）。要改過去必須走「更正」並寫原因。
 */
const draft = ref<SettingsDto | null>(null)
const today = ref('')
const latestFrom = ref('')
const versions = ref<SettingsVersionSummary[]>([])
const error = ref<string | null>(null)
const savedMsg = ref<string | null>(null)
const dirty = ref(false)
const saving = ref(false)
const correcting = ref(false)
const correctionFrom = ref('')
const correctionNote = ref('')

function applyView(v: { settings: SettingsDto; effectiveFrom: string; today: string; versions: SettingsVersionSummary[] }) {
  draft.value = v.settings
  today.value = v.today
  latestFrom.value = v.effectiveFrom
  versions.value = v.versions
  savedStart.value = v.settings.startDate
}

// 設定和行事曆同時抓（以前一個接一個，要等兩趟）
onMounted(async () => {
  try {
    const [view] = await Promise.all([getSettings(), loadCalendar()])
    applyView(view)
  } catch (e) {
    error.value = (e as Error).message
  }
})
// 從別頁切回來：沒有在編輯的話背景更新；正在改的草稿保留
let firstActivation = true
onActivated(async () => {
  if (firstActivation) {
    firstActivation = false
    return
  }
  if (dirty.value) return
  try {
    applyView(await getSettings())
  } catch {
    /* 背景更新失敗就先顯示原本的 */
  }
})

function touch() {
  dirty.value = true
  savedMsg.value = null
}

// ---- 金額換算 ----
const income = computed(() => Number(draft.value?.monthlyIncome) || 0)
function fixedMonthly(c: CategoryDto): number {
  // 年繳項目換算成每月平均，只用來看比例；實際扣款在年繳月份那期。
  const perMonth = { Monthly: 1, Quarterly: 3, Yearly: 12 } as const
  return c.fixedItems.filter((f) => f.isActive).reduce((a, f) => a + Math.round((Number(f.amount) || 0) / perMonth[f.cycle]), 0)
}
// ---- 即時合計（§7、§8.2）：用草稿向後端試算新設定生效那一期 ----
const estimate = ref<SettingsEstimate | null>(null)
let estTimer: ReturnType<typeof setTimeout> | undefined
watch(
  draft,
  (d) => {
    clearTimeout(estTimer)
    if (!d) return
    estTimer = setTimeout(async () => {
      try {
        estimate.value = await estimateSettings(cleanDraft())
      } catch {
        /* 試算失敗不影響編輯 */
      }
    }, 350)
  },
  { deep: true },
)
const estOf = (i: number) => estimate.value?.categories.find((c) => c.index === i)
const overCats = computed(() => (estimate.value?.categories ?? []).filter((c) => c.over > 0))

/** % 基準切換：換算每個分類的 %，讓金額不變（§7） */
function switchBase(next: PercentBase) {
  const d = draft.value!
  if (d.percentBase === next || !estimate.value) {
    d.percentBase = next
    touch()
    return
  }
  const incomeAmt = Number(d.monthlyIncome) || 0
  const afterFixed = Math.max(1, incomeAmt - estimate.value.fixedTotal)
  const factor = next === 'AfterFixed' ? incomeAmt / afterFixed : afterFixed / Math.max(1, incomeAmt)
  for (const c of d.categories) if (c.mode !== 'Fixed' && c.usePercent !== false) c.percent = Math.round((Number(c.percent) || 0) * factor * 100) / 100
  d.percentBase = next
  touch()
}

function amountOf(c: CategoryDto): number {
  const i = draft.value?.categories.indexOf(c) ?? -1
  const e = i >= 0 ? estOf(i) : undefined
  if (e) return e.budget
  return c.mode === 'Fixed' ? fixedMonthly(c) : Math.round((income.value * (Number(c.percent) || 0)) / 100)
}
const allocated = computed(() => draft.value?.categories.reduce((a, c) => a + amountOf(c), 0) ?? 0)
const unallocated = computed(() => income.value - allocated.value)

// ---- 未分配的 % 一鍵分出去 ----
const variableCats = computed(() => draft.value?.categories.filter((c) => c.mode !== 'Fixed') ?? [])
const giveTarget = ref<number>(-1)
const unallocatedPct = computed(() => (income.value > 0 ? Math.floor((unallocated.value / income.value) * 10000) / 100 : 0))
const round2 = (x: number) => Math.round(x * 100) / 100

/** 把未分配的 % 全部加到一個分類上 */
function giveAll() {
  const c = variableCats.value[giveTarget.value] ?? variableCats.value[0]
  if (!c || unallocatedPct.value <= 0) return
  c.percent = round2((Number(c.percent) || 0) + unallocatedPct.value)
  touch()
}

/** 依各分類目前的 % 比例，把未分配的 % 分到所有每日 / 月額度分類 */
function spreadAll() {
  const cats = variableCats.value
  const total = cats.reduce((a, c) => a + (Number(c.percent) || 0), 0)
  if (!cats.length || unallocatedPct.value <= 0) return
  let left = unallocatedPct.value
  cats.forEach((c, i) => {
    const share = i === cats.length - 1 ? left : round2(total > 0 ? (unallocatedPct.value * (Number(c.percent) || 0)) / total : unallocatedPct.value / cats.length)
    c.percent = round2((Number(c.percent) || 0) + share)
    left = round2(left - share)
  })
  touch()
}

const SLOTS = ['--series-1', '--series-2', '--series-3', '--series-4', '--series-5', '--series-6', '--series-7', '--series-8']
const segments = computed<Segment[]>(() => {
  if (!draft.value) return []
  const segs: Segment[] = draft.value.categories.slice(0, 8).map((c, i) => ({
    key: i,
    label: c.name || '（未命名）',
    value: amountOf(c),
    color: `var(${SLOTS[i]})`,
    note: groupLabel[c.group],
  }))
  if (unallocated.value > 0) segs.push({ key: 'u', label: '未分配（進待分配池）', value: unallocated.value, color: 'var(--line-2)' })
  return segs
})

// ---- 每日額度估算：用本期（或還沒開始時的第一期）實際的上班日 / 假日天數（§3.5）----
const dayCounts = computed(() => {
  if (store.period) return { work: store.period.weekdayCount, hol: store.period.holidayCount }
  const hol = calendar.value.filter((d) => d.isHoliday).length
  return { work: calendar.value.length - hol, hol }
})
const periodWord = computed(() => (store.period ? '本期' : '第一期'))
const nextDay = computed(() => {
  if (!today.value) return ''
  const d = parseDate(today.value)
  d.setDate(d.getDate() + 1)
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
})
// ---- 分類操作 ----
const groups: CategoryGroup[] = ['Food', 'Clothing', 'Housing', 'Transport', 'Education', 'Leisure', 'Savings', 'Other']
const modes: { v: BudgetMode; label: string }[] = [
  { v: 'Daily', label: '每日' },
  { v: 'Envelope', label: '月額度' },
  { v: 'Fixed', label: '固定' },
]

function addCategory() {
  draft.value!.categories.push({ id: 0, name: '', group: 'Other', mode: 'Envelope', percent: 0, slots: [], fixedItems: [] })
  touch()
}
function move(i: number, d: -1 | 1) {
  const list = draft.value!.categories
  const j = i + d
  if (j < 0 || j >= list.length) return
  ;[list[i], list[j]] = [list[j], list[i]]
  touch()
}
const confirmRemove = ref<number | null>(null)
function removeCategory(i: number) {
  if (confirmRemove.value !== i) {
    confirmRemove.value = i
    return
  }
  draft.value!.categories.splice(i, 1)
  confirmRemove.value = null
  touch()
}
function setMode(c: CategoryDto, m: BudgetMode) {
  c.mode = m
  if (m === 'Daily' && c.slots.length === 0) c.slots.push({ id: 0, name: '', start: draft.value!.logicalDayStart, workdayAmount: 0, holidayAmount: 0 })
  touch()
}

// ---- 時段時間（§3.2 邊界銜接）：第一個固定從邏輯日起點開始 ----
/** 改邏輯日起點時，每個分類的第一個時段跟著移 */
function onDayStartChange() {
  for (const c of draft.value!.categories) if (c.slots[0]) c.slots[0].start = draft.value!.logicalDayStart
  touch()
}
const shiftLabel: Record<HolidayShift, string> = { None: '不調整', Before: '往前推到最近的工作日', After: '往後推到最近的工作日' }

/** 送出前整理數字（儲存和即時試算共用） */
function cleanDraft(): SettingsDto {
  const d = draft.value!
  const num = (x: unknown) => (x === null || x === '' || x === undefined ? null : Number(x))
  return {
    logicalDayStart: d.logicalDayStart,
    monthlyIncome: Math.round(Number(d.monthlyIncome) || 0),
    payday: { day: Math.round(Number(d.payday.day) || 1), shift: d.payday.shift },
    startDate: d.startDate || null,
    percentBase: d.percentBase,
    incomeKind: d.incomeKind,
    categories: d.categories.map((c) => ({
      ...c,
      percent: Number(c.percent) || 0,
      usePercent: c.usePercent !== false,
      amount: c.usePercent === false ? Math.round(Number(c.amount) || 0) : null,
      floor: num(c.floor),
      slots: c.slots.map((s) => ({
        ...s,
        workdayAmount: Math.round(Number(s.workdayAmount) || 0),
        holidayAmount: Math.round(Number(s.holidayAmount) || 0),
        weight: num(s.weight),
        holidayWeight: num(s.holidayWeight),
        workdayLock: num(s.workdayLock),
        holidayLock: num(s.holidayLock),
        workdayFloor: num(s.workdayFloor),
        holidayFloor: num(s.holidayFloor),
      })),
      fixedItems: c.fixedItems.map((f) => ({
        ...f,
        amount: Math.round(Number(f.amount) || 0),
        dueDay: f.dueDay === null || (f.dueDay as unknown) === '' ? null : Math.round(Number(f.dueDay)),
        billingMonth: f.cycle === 'Monthly' ? null : Math.round(Number(f.billingMonth) || 1),
      })),
    })),
  }
}

async function save() {
  if (!draft.value) return
  if (correcting.value && (!correctionFrom.value || correctionNote.value.trim().length < 2)) {
    error.value = '更正過去要選生效日並寫原因。'
    return
  }
  saving.value = true
  error.value = null
  try {
    const startChanged = (draft.value.startDate || null) !== (savedStart.value || null)
    const v = await saveSettings(cleanDraft(), correcting.value ? correctionFrom.value : null, correcting.value ? correctionNote.value.trim() : null)
    applyView(v)
    dirty.value = false
    savedMsg.value = correcting.value
      ? `已存成更正版本，從 ${shortDate(v.effectiveFrom)} 起重算`
      : `已存成新版本，${shortDate(v.effectiveFrom)} 起生效；今天還沒回報、而且調低的時段今天就生效`
    correcting.value = false
    correctionNote.value = ''
    // 版本變了，本期的每日額度由後端重播重新算
    await loadCurrentPeriod(true)
    if (startChanged) await loadCalendar()
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    saving.value = false
  }
}

// ---- 假日覆寫 ----
const calendar = ref<CalendarDayDto[]>([])
async function loadCalendar() {
  const range = store.period
    ? [store.period.startDate, store.period.endDate]
    : store.notStarted
      ? [store.notStarted.firstPeriodStart, store.notStarted.firstPeriodEnd]
      : null
  calendar.value = range ? await getCalendar(range[0], range[1]) : []
}

// ---- 開始日期 ----
const savedStart = ref<string | null>(null)
const clearing = ref(false)
const clearMsg = ref<string | null>(null)
async function clearTrial() {
  if (!clearing.value) {
    clearing.value = true
    setTimeout(() => (clearing.value = false), 4000)
    return
  }
  const { deleted } = await deleteBeforeStart()
  clearing.value = false
  clearMsg.value = deleted ? `已清除 ${deleted} 期試用資料` : '沒有開始日期之前的資料'
}

/** 季繳：選第一個扣款月份，顯示成「1・4・7・10 月」。 */
function quarterLabel(m: number): string {
  return [0, 3, 6, 9].map((k) => ((m - 1 + k) % 12) + 1).join('・') + ' 月'
}
const calCells = computed(() => {
  if (!calendar.value.length) return []
  const first = parseDate(calendar.value[0].date).getDay()
  return [...Array.from({ length: first }, () => null), ...calendar.value]
})
/** 點一下循環：照行事曆 → 強制假日 → 強制上班日 → 照行事曆。 */
async function toggleDay(d: CalendarDayDto) {
  if (d.date < today.value) return // 過去的日子不能改（§2.3）
  const custom = d.name === '自訂假日' ? 'hol' : d.name === '自訂上班日' ? 'work' : null
  const next = custom === null ? true : custom === 'hol' ? false : null
  try {
    await setDayOverride(d.date, next)
    await loadCalendar()
    await loadCurrentPeriod(true)
  } catch (e) {
    error.value = (e as Error).message
  }
}

// ---- 外觀 / 帳號 ----
const theme = ref<ThemeChoice>(currentTheme())
function pickTheme(t: ThemeChoice) {
  theme.value = t
  applyTheme(t)
}
async function signOut() {
  await logout()
  window.location.href = '/login'
}
</script>

<template>
  <div class="page">
    <header class="page-head">
      <div>
        <h1>設定</h1>
        <p class="sub">每次儲存都是一個新版本，從明天起生效；過去的日子不會被改</p>
      </div>
    </header>

    <p v-if="error" class="error-box">{{ error }}</p>

    <p v-if="latestFrom > today" class="notice">
      你看到的是 <b>{{ shortDate(latestFrom) }}</b> 起生效的版本；在那之前照舊的設定算。
    </p>

    <template v-if="draft">
      <section class="section">
        <h2 class="section-title">收入</h2>
        <div class="panel panel-pad two">
          <label class="field">
            每月收入（實拿）
            <input v-model.number="draft.monthlyIncome" class="input num" inputmode="numeric" @input="touch" />
          </label>
          <label class="field">
            收入類型
            <select v-model="draft.incomeKind" class="select" @change="touch">
              <option value="Fixed">固定月薪</option>
              <option value="Variable">非固定（接案、抽成）</option>
            </select>
          </label>
          <label class="field">
            % 算在哪個基準上
            <select :value="draft.percentBase" class="select" @change="switchBase(($event.target as HTMLSelectElement).value as PercentBase)">
              <option value="AfterFixed">月收入扣掉固定支出後（建議）</option>
              <option value="Income">整個月收入</option>
            </select>
          </label>
          <label class="field">
            發薪日
            <select v-model.number="draft.payday.day" class="select" @change="touch">
              <option v-for="d in 31" :key="d" :value="d">每月 {{ d }} 號{{ d > 28 ? '（小月是月底）' : '' }}</option>
            </select>
          </label>
          <label class="field">
            發薪日遇到假日
            <select v-model="draft.payday.shift" class="select" @change="touch">
              <option v-for="(label, k) in shiftLabel" :key="k" :value="k">{{ label }}</option>
            </select>
          </label>
          <label class="field">
            一天從幾點開始算
            <input v-model="draft.logicalDayStart" type="time" class="input" step="1800" @change="onDayStartChange" />
          </label>
          <div class="field start-field">
            <label for="start-date">開始日期（選填）</label>
            <div class="start-row">
              <input id="start-date" v-model="draft.startDate" type="date" class="input" @input="touch" />
              <button v-if="draft.startDate" type="button" class="btn quiet sm" @click="draft.startDate = null; touch()">清除</button>
            </div>
          </div>
          <p class="hint-line muted">
            <template v-if="draft.incomeKind === 'Variable'">非固定收入：每期的 % 以上期實際收入（含薪資調整）當基準。</template>
            <template v-if="estimate && draft.percentBase === 'AfterFixed'">
              這期固定支出 {{ money(estimate.fixedTotal) }}，% 算在剩下的 {{ money(estimate.percentBaseAmount) }} 上；切換基準時會自動換算 %，金額不變。
            </template>
          </p>
          <p class="hint-line muted">
            一天從 {{ draft.logicalDayStart }} 開始：凌晨 {{ draft.logicalDayStart }} 以前的消費算前一天（週六 01:00 的宵夜算週五）。
          </p>
          <p class="hint-line muted">
            <template v-if="draft.startDate">
              {{ dayLabel(draft.startDate) }} 之前完全不排額度、不計算；第一期從那天到下一個發薪日前一天。
            </template>
            <template v-else>還沒拿到薪水的話，填第一次發薪那天，在那之前 app 只顯示「還沒開始」。</template>
          </p>
          <div v-if="savedStart" class="trial">
            <button type="button" class="btn sm danger" @click="clearTrial">
              {{ clearing ? '再按一次確認清除' : '清除開始日期之前的試用資料' }}
            </button>
            <span v-if="clearMsg" class="muted small">{{ clearMsg }}</span>
          </div>
        </div>
      </section>

      <section class="section">
        <h2 class="section-title">
          分配
          <span class="aside num" :class="unallocated < 0 || (estimate?.percentTotal ?? 0) > 100 ? 'bad' : ''">
            <template v-if="estimate">已分配 {{ estimate.percentTotal }}%，剩 {{ Math.max(0, 100 - estimate.percentTotal).toFixed(2).replace(/\.?0+$/, '') }}% 進待分配池・</template>
            {{ unallocated >= 0 ? `未分配 ${money(unallocated)}` : `超出收入 ${money(-unallocated)}` }}
          </span>
        </h2>
        <div class="panel panel-pad">
          <AllocationBar :segments="segments" :total="income" caption="收入分配預覽" />
          <div v-if="unallocated > 0 && variableCats.length" class="give">
            <span class="num">還有 <b>{{ money(unallocated) }}</b>（{{ unallocatedPct }}%）沒分配</span>
            <div class="give-row">
              <select v-model.number="giveTarget" class="select compact" aria-label="分給哪個分類">
                <option :value="-1" disabled>選一個分類</option>
                <option v-for="(c, i) in variableCats" :key="i" :value="i">{{ c.name || '（未命名）' }}</option>
              </select>
              <button type="button" class="btn sm" :disabled="giveTarget < 0" @click="giveAll">全部給它</button>
              <button type="button" class="btn sm" @click="spreadAll">依比例分到全部</button>
            </div>
            <p class="muted small">不分也沒關係：沒分配的錢每期會進待分配池，拿來補超支。</p>
          </div>
        </div>
      </section>

      <section class="section">
        <h2 class="section-title">分類<span class="aside">食衣住行育樂，自己定</span></h2>

        <div v-for="(c, i) in draft.categories" :key="i" class="panel cat">
          <div class="cat-head">
            <input v-model="c.name" class="input compact name" placeholder="分類名稱" maxlength="40" aria-label="分類名稱" @input="touch" />
            <select v-model="c.group" class="select compact grp" aria-label="類別" @change="touch">
              <option v-for="g in groups" :key="g" :value="g">{{ groupLabel[g] }}</option>
            </select>
            <div class="seg" role="group" aria-label="模式">
              <button v-for="m in modes" :key="m.v" type="button" :aria-pressed="c.mode === m.v" @click="setMode(c, m.v)">{{ m.label }}</button>
            </div>
            <div class="order">
              <button type="button" class="btn quiet sm" :disabled="i === 0" aria-label="上移" @click="move(i, -1)">上移</button>
              <button type="button" class="btn quiet sm" :disabled="i === draft.categories.length - 1" aria-label="下移" @click="move(i, 1)">下移</button>
              <button type="button" class="btn quiet sm danger" @click="removeCategory(i)">{{ confirmRemove === i ? '確認刪除' : '刪除' }}</button>
            </div>
          </div>

          <div v-if="c.mode !== 'Fixed'" class="pct-row">
            <label class="check pct-toggle">
              <input :checked="c.usePercent !== false" type="checkbox" @change="c.usePercent = ($event.target as HTMLInputElement).checked; touch()" />
              <span>用 % 計算</span>
            </label>
            <label v-if="c.usePercent !== false" class="field pct-field">
              比例 %
              <input v-model.number="c.percent" class="input compact num" inputmode="decimal" @input="touch" />
              <span class="hint base-hint">{{ draft.percentBase === 'AfterFixed' ? '佔扣掉固定支出後' : '佔收入' }}</span>
            </label>
            <label v-else class="field pct-field">
              固定金額
              <input v-model.number="c.amount" class="input compact num" inputmode="numeric" @input="touch" />
            </label>
            <span class="num eq">= {{ money(amountOf(c)) }} / 期</span>
            <label class="field floor-field">
              底線（選填）
              <input v-model.number="c.floor" class="input compact num" inputmode="numeric" placeholder="無" @input="touch" />
            </label>
            <span v-if="c.mode === 'Envelope'" class="muted est">整期一個額度，花了就扣</span>
          </div>

          <!-- 每日時段 -->
          <div v-if="c.mode === 'Daily'" class="sub-table">
            <MealTable
              :cat="c"
              :est="estOf(i)"
              :weekdays="estimate?.weekdays ?? dayCounts.work"
              :holidays="estimate?.holidays ?? dayCounts.hol"
              :day-start="draft.logicalDayStart"
              @touch="touch"
            />
          </div>

          <SubItemsEditor v-if="c.mode !== 'Fixed'" :cat="c" @touch="touch" />

          <!-- 固定項目 -->
          <div v-if="c.mode === 'Fixed'" class="sub-table">
            <p class="muted fixed-sum num">
              鎖定：每月 {{ money(fixedMonthly(c)) }}（{{ pct(fixedMonthly(c) / Math.max(1, income), 1) }}），不能回報、不參與每日計算
            </p>
            <div class="fx-row st-head muted">
              <span>項目</span><span class="r">金額</span><span class="r">扣款日</span><span>週期</span><span>訂閱</span><span />
            </div>
            <div v-for="(f, fi) in c.fixedItems" :key="fi" class="fx-row">
              <input v-model="f.name" class="input compact fx-name" placeholder="例如 房租" maxlength="60" aria-label="項目名稱" @input="touch" />
              <span class="fx-amt m-field">
                <span class="m-lab">金額</span>
                <input v-model.number="f.amount" class="input compact num" inputmode="numeric" placeholder="金額" aria-label="金額" @input="touch" />
              </span>
              <span class="fx-due m-field">
                <span class="m-lab">每月幾號扣</span>
                <input v-model.number="f.dueDay" class="input compact num" inputmode="numeric" placeholder="扣款日" aria-label="扣款日" @input="touch" />
              </span>
              <div class="cycle fx-cycle">
                <select v-model="f.cycle" class="select compact" aria-label="週期" @change="f.billingMonth ??= 1; touch()">
                  <option value="Monthly">月繳</option>
                  <option value="Quarterly">季繳</option>
                  <option value="Yearly">年繳</option>
                </select>
                <select v-if="f.cycle === 'Yearly'" v-model.number="f.billingMonth" class="select compact" aria-label="扣款月份" @change="touch">
                  <option v-for="m in 12" :key="m" :value="m">{{ m }} 月</option>
                </select>
                <select v-else-if="f.cycle === 'Quarterly'" v-model.number="f.billingMonth" class="select compact" aria-label="扣款月份" @change="touch">
                  <option v-for="m in 3" :key="m" :value="m">{{ quarterLabel(m) }}</option>
                  <option v-if="f.billingMonth && f.billingMonth > 3" :value="f.billingMonth">{{ quarterLabel(f.billingMonth) }}</option>
                </select>
              </div>
              <label class="check fx-sub"><input v-model="f.isSubscription" type="checkbox" @change="touch" /><span class="sub-label">訂閱</span></label>
              <button type="button" class="btn quiet sm danger fx-del" @click="c.fixedItems.splice(fi, 1); touch()">移除</button>
            </div>
            <p v-for="f in c.fixedItems.filter((x) => x.activeFrom)" :key="`af-${f.id}`" class="muted small">
              「{{ f.name }}」從 {{ shortDate(f.activeFrom!) }} 那期開始算
            </p>
            <button
              type="button"
              class="btn sm add"
              @click="c.fixedItems.push({ id: 0, name: '', amount: 0, dueDay: null, isSubscription: false, cycle: 'Monthly', billingMonth: null, isActive: true, activeFrom: null }); touch()"
            >
              新增固定項目
            </button>
          </div>
        </div>

        <button type="button" class="btn add-cat" @click="addCategory">新增分類</button>
      </section>

      <div v-if="dirty || savedMsg || correcting" class="save-bar">
        <div class="save-info">
          <span v-if="savedMsg" class="good">{{ savedMsg }}</span>
          <span v-else-if="dirty" class="muted">
            {{ correcting ? '更正：從指定日期起重算（過去的結果會改變）' : `儲存後從 ${shortDate(nextDay)} 起生效；調低的時段今天就生效` }}
          </span>
          <label class="check corr">
            <input v-model="correcting" type="checkbox" />
            <span>更正過去</span>
          </label>
          <div v-if="correcting" class="corr-fields">
            <input v-model="correctionFrom" type="date" class="input compact" :max="today" aria-label="更正生效日" />
            <input v-model="correctionNote" class="input compact" maxlength="200" placeholder="原因（必填），例如 薪資單記錯" aria-label="更正原因" />
          </div>
        </div>
        <p v-if="overCats.length" class="bad small over-msg">
          {{ overCats.map((c) => `「${draft!.categories[c.index]?.name}」超出 ${money(c.over)}`).join('、') }}，調整後才能儲存（§8.2）
        </p>
        <button class="btn primary" :disabled="saving || !dirty || overCats.length > 0" @click="save">{{ saving ? '儲存中' : correcting ? '存成更正' : '儲存設定' }}</button>
      </div>

      <section class="section">
        <h2 class="section-title">設定版本<span class="aside">每次儲存都留一份，可以追溯</span></h2>
        <div class="panel">
          <ul class="list versions">
            <li v-for="v in versions" :key="v.id">
              <span class="num">{{ v.effectiveFrom <= '0001-01-01' ? '最早' : shortDate(v.effectiveFrom) }} 起</span>
              <span>
                <span v-if="v.isCorrection" class="tag">更正</span>
                {{ v.note ?? '一般儲存' }}
              </span>
              <span class="muted num">{{ new Date(v.createdAt).toLocaleString('zh-TW', { hour12: false }) }}</span>
            </li>
          </ul>
        </div>
      </section>

      <section class="section">
        <h2 class="section-title">{{ periodWord }}假日<span class="aside">點一下切換：照行事曆 → 假日 → 上班日（過去的日子不能改）</span></h2>
        <div class="panel cal">
          <div v-for="w in ['日', '一', '二', '三', '四', '五', '六']" :key="w" class="wd">{{ w }}</div>
          <template v-for="(d, i) in calCells" :key="i">
            <div v-if="!d" class="cell blank" />
            <button
              v-else
              type="button"
              class="cell"
              :class="{ hol: d.isHoliday, custom: d.name?.startsWith('自訂'), past: d.date < today }"
              :title="d.name ?? (d.isHoliday ? '假日' : '上班日')"
              :disabled="d.date < today"
              @click="toggleDay(d)"
            >
              <span class="dn">{{ parseDate(d.date).getDate() }}</span>
              <span class="hn">{{ d.name ?? '' }}</span>
            </button>
          </template>
        </div>
        <p class="muted small">國定假日、補假、補班來自行政院人事行政總處的辦公日曆表。請假或颱風假可以在這裡自己改。</p>
      </section>

      <section class="section">
        <h2 class="section-title">每晚通知</h2>
        <PushSettings />
      </section>

      <section class="section">
        <h2 class="section-title">資料與安全</h2>
        <TrustSettings />
      </section>

      <section class="section">
        <h2 class="section-title">外觀與帳號</h2>
        <div class="panel panel-pad acct">
          <div class="seg" role="group" aria-label="外觀">
            <button type="button" :aria-pressed="theme === 'system'" @click="pickTheme('system')">跟隨系統</button>
            <button type="button" :aria-pressed="theme === 'light'" @click="pickTheme('light')">淺色</button>
            <button type="button" :aria-pressed="theme === 'dark'" @click="pickTheme('dark')">深色</button>
          </div>
          <div class="who">
            <span class="muted">{{ store.me?.userName ?? store.me?.userId }}</span>
            <button class="btn sm" @click="signOut">登出</button>
          </div>
        </div>
      </section>
    </template>
    <Skeleton v-else-if="!error" variant="page" :rows="4" :header="false" />
  </div>
</template>

<style scoped>
.two {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
}
.give {
  margin-top: 12px;
  padding-top: 12px;
  border-top: 1px solid var(--line);
  display: flex;
  flex-direction: column;
  gap: 8px;
  font-size: 14px;
}
.give-row {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}
.give-row .select {
  width: auto;
  min-width: 140px;
  flex: 1;
}
.settle {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.start-row {
  display: flex;
  gap: 6px;
  align-items: center;
}
.hint-line {
  grid-column: 1 / -1;
  font-size: 12px;
  margin-top: -4px;
}
.trial {
  grid-column: 1 / -1;
  display: flex;
  gap: 10px;
  align-items: center;
  flex-wrap: wrap;
}
.sub-label {
  display: none;
  font-size: 13px;
}
.notice {
  position: sticky;
  top: 8px;
  z-index: 5;
  display: flex;
  gap: 12px;
  justify-content: space-between;
  align-items: center;
  flex-wrap: wrap;
  padding: 12px 16px;
  background: var(--surface);
  border: 1px solid var(--ink);
  border-left-width: 3px;
  border-radius: 4px;
  font-size: 14px;
}
.notice p {
  font-size: 12px;
}
.notice-actions {
  display: flex;
  gap: 8px;
}
.cat {
  display: flex;
  flex-direction: column;
}
.cat-head {
  display: grid;
  grid-template-columns: 1fr 84px auto auto;
  gap: 8px;
  align-items: center;
  padding: 12px 16px;
  border-bottom: 1px solid var(--line);
}
.name {
  font-weight: 600;
}
.order {
  display: flex;
}
/* 手機才顯示的欄位標籤（桌機有表頭） */
.m-field {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}
.m-field .input {
  flex: 1;
  min-width: 0;
}
.m-lab {
  display: none;
  font-size: 12px;
  color: var(--muted);
  white-space: nowrap;
}
.base-hint {
  font-size: 11px;
  white-space: nowrap;
}
.pct-row {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 8px 14px;
  padding: 12px 16px 4px;
}
.pct-field {
  width: 100px;
}
.eq {
  font-weight: 600;
  padding-bottom: 8px;
}
.est {
  font-size: 12px;
  padding-bottom: 9px;
}
.sub-table {
  padding: 10px 16px 14px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.pct-toggle {
  align-self: center;
  padding-bottom: 6px;
}
.floor-field {
  width: 110px;
}
.over-msg {
  width: 100%;
  order: -1;
}
.st-time {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}
.st-time .input {
  width: 96px;
}
.until {
  font-size: 12px;
  white-space: nowrap;
}
.cell.past {
  opacity: 0.45;
  cursor: default;
}
.corr {
  white-space: nowrap;
}
.save-info {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px 12px;
  flex: 1;
  min-width: 0;
}
.corr {
  font-size: 13px;
}
.corr-fields {
  display: grid;
  grid-template-columns: 150px 1fr;
  gap: 6px;
  width: 100%;
}
.versions li {
  display: grid;
  grid-template-columns: 70px 1fr auto;
  gap: 10px;
  padding: 8px 16px;
  font-size: 13px;
  align-items: center;
}
.st-row {
  display: grid;
  grid-template-columns: 1fr 170px 80px 80px 56px;
  gap: 8px;
  align-items: center;
}
.fx-row {
  display: grid;
  grid-template-columns: 1fr 96px 64px 160px 40px 56px;
  gap: 8px;
  align-items: center;
}
.st-head {
  font-size: 12px;
}
.r {
  text-align: right;
}
.cycle {
  display: flex;
  gap: 4px;
}
.cycle .select {
  flex: 1;
  min-width: 0;
}
.sr {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}
.fixed-sum {
  font-size: 13px;
  margin-bottom: 4px;
}
.small {
  font-size: 12px;
}
.add {
  align-self: flex-start;
  margin-top: 4px;
}
.add-cat {
  align-self: flex-start;
}
.save-bar {
  position: sticky;
  bottom: 12px;
  z-index: 5;
  display: flex;
  flex-wrap: wrap; /* 超出額度的提示佔一整行，其他的換到下一行，不要被擠成直的 */
  justify-content: flex-end;
  align-items: center;
  gap: 12px;
  padding: 10px 16px;
  background: var(--surface);
  border: 1px solid var(--line-2);
  border-radius: 6px;
  font-size: 13px;
}
.cal {
  display: grid;
  grid-template-columns: repeat(7, minmax(0, 1fr));
  gap: 1px;
  background: var(--line);
  overflow: hidden;
}
.wd {
  background: var(--surface);
  text-align: center;
  font-size: 12px;
  color: var(--muted);
  padding: 6px 0;
}
.cell {
  background: var(--surface);
  border: 0;
  min-height: 52px;
  padding: 5px 6px;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  text-align: left;
  cursor: pointer;
  min-width: 0;
}
.cell.blank {
  cursor: default;
}
.cell.hol {
  background: var(--sunk);
}
.cell.custom {
  outline: 1px solid var(--ink-2);
  outline-offset: -1px;
}
.dn {
  font-size: 13px;
  font-weight: 600;
}
.hn {
  font-size: 10px;
  color: var(--muted);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 100%;
}
.acct {
  display: flex;
  justify-content: space-between;
  align-items: center;
  flex-wrap: wrap;
  gap: 12px;
}
.who {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 13px;
}
@media (max-width: 719px) {
  .save-bar {
    bottom: calc(var(--nav-h) + 12px + env(safe-area-inset-bottom, 0px));
  }
}
@media (max-width: 640px) {
  /* 手機：用 % 計算一行；比例、金額一行；底線、說明一行 */
  .m-lab {
    display: inline;
  }
  .pct-row {
    display: grid;
    grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
    align-items: end;
    gap: 8px 12px;
  }
  .pct-toggle {
    grid-column: 1 / -1;
    padding-bottom: 0;
  }
  .pct-field,
  .floor-field {
    width: auto;
  }
  .eq {
    padding-bottom: 24px;
  }
  .over-msg {
    font-size: 12px;
  }
  .save-bar {
    padding: 8px 12px;
    gap: 8px;
  }
  .save-info > .muted {
    display: none;
  }
  .cat-head {
    grid-template-columns: 1fr 84px;
  }
  .cat-head .seg,
  .cat-head .order {
    grid-column: span 2;
  }
  /* 手機：每個固定項目排成一張小卡，週期的兩個下拉各佔半寬，不再擠在同一欄 */
  .fx-row {
    grid-template-columns: 1fr 1fr;
    grid-template-areas:
      'name name'
      'amt due'
      'cycle cycle'
      'sub del';
    padding-bottom: 10px;
    border-bottom: 1px solid var(--line);
  }
  .fx-name {
    grid-area: name;
  }
  .fx-amt {
    grid-area: amt;
  }
  .fx-due {
    grid-area: due;
  }
  .fx-cycle {
    grid-area: cycle;
  }
  .fx-sub {
    grid-area: sub;
    align-items: center;
  }
  .fx-del {
    grid-area: del;
    justify-self: end;
  }
  .sub-label {
    display: inline;
  }
  .fx-row.st-head {
    display: none;
  }
  .st-row {
    grid-template-columns: 1fr 1fr;
    grid-template-areas:
      'name time'
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
  }
  .st-w {
    grid-area: w;
  }
  .st-h {
    grid-area: h;
  }
  .st-del {
    grid-area: del;
    justify-self: end;
  }
  .corr-fields {
    grid-template-columns: 1fr;
  }
  .versions li {
    grid-template-columns: 60px 1fr;
  }
  .versions li > :last-child {
    grid-column: 2;
    font-size: 11px;
  }
  .two {
    grid-template-columns: 1fr;
  }
}
</style>
