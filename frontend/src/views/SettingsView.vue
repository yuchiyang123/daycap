<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import AllocationBar, { type Segment } from '../charts/AllocationBar.vue'
import { deleteBeforeStart, getCalendar, getSettings, rebuildPeriod, saveSettings, setDayOverride, logout } from '../api/endpoints'
import type { BudgetMode, CalendarDayDto, CategoryDto, CategoryGroup, SettingsDto } from '../api/types'
import { store, setPeriod, loadCurrentPeriod } from '../lib/store'
import { dayLabel, groupLabel, money, parseDate, pct, shortDate } from '../lib/format'
import { applyTheme, currentTheme, type ThemeChoice } from '../lib/theme'

const draft = ref<SettingsDto | null>(null)
const error = ref<string | null>(null)
const saved = ref(false)
const dirty = ref(false)
const saving = ref(false)
const needsRebuild = ref(false)
const rebuilding = ref(false)

onMounted(async () => {
  try {
    draft.value = await getSettings()
    savedStart.value = draft.value.startDate
    await loadCalendar()
  } catch (e) {
    error.value = (e as Error).message
  }
})

function touch() {
  dirty.value = true
  saved.value = false
}

// ---- 金額換算 ----
const income = computed(() => Number(draft.value?.monthlyIncome) || 0)
function fixedMonthly(c: CategoryDto): number {
  // 年繳項目換算成每月平均，只用來看比例；實際扣款在年繳月份那期。
  const perMonth = { Monthly: 1, Quarterly: 3, Yearly: 12 } as const
  return c.fixedItems.filter((f) => f.isActive).reduce((a, f) => a + Math.round((Number(f.amount) || 0) / perMonth[f.cycle]), 0)
}
function amountOf(c: CategoryDto): number {
  return c.mode === 'Fixed' ? fixedMonthly(c) : Math.round((income.value * (Number(c.percent) || 0)) / 100)
}
const allocated = computed(() => draft.value?.categories.reduce((a, c) => a + amountOf(c), 0) ?? 0)
const unallocated = computed(() => income.value - allocated.value)

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
  if (unallocated.value > 0) segs.push({ key: 'u', label: '未分配（進待定區）', value: unallocated.value, color: 'var(--line-2)' })
  return segs
})

// ---- 每日額度估算：用本期（或還沒開始時的第一期）實際的上班日 / 假日天數 ----
const dayCounts = computed(() => {
  const hol = calendar.value.filter((d) => d.isHoliday).length
  return { work: calendar.value.length - hol, hol }
})
const periodWord = computed(() => (store.period ? '本期' : '第一期'))
function dailyEstimate(c: CategoryDto): number {
  const w = c.slots.reduce((a, s) => a + (Number(s.workdayAmount) || 0), 0)
  const h = c.slots.reduce((a, s) => a + (Number(s.holidayAmount) || 0), 0)
  return w * dayCounts.value.work + h * dayCounts.value.hol
}

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
  if (m === 'Daily' && c.slots.length === 0) c.slots.push({ id: 0, name: '', workdayAmount: 0, holidayAmount: 0 })
  touch()
}

async function save() {
  if (!draft.value) return
  saving.value = true
  error.value = null
  try {
    const startChanged = (draft.value.startDate || null) !== (savedStart.value || null)
    const clean: SettingsDto = {
      monthlyIncome: Math.round(Number(draft.value.monthlyIncome) || 0),
      cycleStartDay: Math.round(Number(draft.value.cycleStartDay) || 1),
      startDate: draft.value.startDate || null,
      categories: draft.value.categories.map((c) => ({
        ...c,
        percent: Number(c.percent) || 0,
        slots: c.slots.map((s) => ({ ...s, workdayAmount: Math.round(Number(s.workdayAmount) || 0), holidayAmount: Math.round(Number(s.holidayAmount) || 0) })),
        fixedItems: c.fixedItems.map((f) => ({
          ...f,
          amount: Math.round(Number(f.amount) || 0),
          dueDay: f.dueDay === null || (f.dueDay as unknown) === '' ? null : Math.round(Number(f.dueDay)),
          billingMonth: f.cycle === 'Monthly' ? null : Math.round(Number(f.billingMonth) || 1),
        })),
      })),
    }
    draft.value = await saveSettings(clean)
    savedStart.value = draft.value.startDate
    dirty.value = false
    saved.value = true
    if (startChanged) {
      // 開始日期變了：重新判斷現在是「還沒開始」還是要開第一期（新的一期直接用新設定，不用重建）
      await loadCurrentPeriod(true)
      await loadCalendar()
    } else {
      needsRebuild.value = !!store.period
    }
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    saving.value = false
  }
}

async function rebuild() {
  if (!store.period) return
  rebuilding.value = true
  try {
    setPeriod(await rebuildPeriod(store.period.id, null))
    needsRebuild.value = false
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    rebuilding.value = false
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
  const custom = d.name === '自訂假日' ? 'hol' : d.name === '自訂上班日' ? 'work' : null
  const next = custom === null ? true : custom === 'hol' ? false : null
  await setDayOverride(d.date, next)
  await loadCalendar()
  needsRebuild.value = !!store.period
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
        <p class="sub">改完按儲存，再決定要不要套用到本期</p>
      </div>
    </header>

    <p v-if="error" class="error-box">{{ error }}</p>

    <div v-if="needsRebuild" class="notice">
      <div>
        <b>要把新設定套用到本期嗎？</b>
        <p class="muted">只會重排今天起的每日額度；已經過去的日子和所有回報都不動。</p>
      </div>
      <div class="notice-actions">
        <button class="btn sm" @click="needsRebuild = false">下期再生效</button>
        <button class="btn sm primary" :disabled="rebuilding" @click="rebuild">{{ rebuilding ? '套用中' : '套用到本期' }}</button>
      </div>
    </div>

    <template v-if="draft">
      <section class="section">
        <h2 class="section-title">收入</h2>
        <div class="panel panel-pad two">
          <label class="field">
            每月收入（實拿）
            <input v-model.number="draft.monthlyIncome" class="input num" inputmode="numeric" @input="touch" />
          </label>
          <label class="field">
            週期起始日（發薪日）
            <select v-model.number="draft.cycleStartDay" class="select" @change="touch">
              <option v-for="d in 28" :key="d" :value="d">每月 {{ d }} 號</option>
            </select>
          </label>
          <div class="field start-field">
            <label for="start-date">開始日期（選填）</label>
            <div class="start-row">
              <input id="start-date" v-model="draft.startDate" type="date" class="input" @input="touch" />
              <button v-if="draft.startDate" type="button" class="btn quiet sm" @click="draft.startDate = null; touch()">清除</button>
            </div>
          </div>
          <p class="hint-line muted">
            <template v-if="draft.startDate">
              {{ dayLabel(draft.startDate) }} 之前完全不排額度、不計算；第一期從那天到下一個 {{ draft.cycleStartDay }} 號前一天。
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
          <span class="aside num" :class="unallocated < 0 ? 'bad' : ''">
            已分配 {{ money(allocated) }}（{{ pct(allocated / Math.max(1, income)) }}）・{{ unallocated >= 0 ? `未分配 ${money(unallocated)}` : `超出收入 ${money(-unallocated)}` }}
          </span>
        </h2>
        <div class="panel panel-pad">
          <AllocationBar :segments="segments" :total="income" caption="收入分配預覽" />
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
            <label class="field pct-field">
              佔收入 %
              <input v-model.number="c.percent" class="input compact num" inputmode="decimal" @input="touch" />
            </label>
            <span class="num eq">= {{ money(amountOf(c)) }} / 月</span>
            <span v-if="c.mode === 'Daily'" class="num muted est">
              {{ periodWord }}日程 {{ money(dailyEstimate(c)) }}（上班 {{ dayCounts.work }} 天・假日 {{ dayCounts.hol }} 天）
              <b :class="amountOf(c) - dailyEstimate(c) < 0 ? 'bad' : ''">
                {{ amountOf(c) - dailyEstimate(c) >= 0 ? `多 ${money(amountOf(c) - dailyEstimate(c))} 進待定區` : `排超過 ${money(dailyEstimate(c) - amountOf(c))}` }}
              </b>
            </span>
            <span v-else class="muted est">整月一個額度，花了就扣</span>
          </div>

          <!-- 每日時段 -->
          <div v-if="c.mode === 'Daily'" class="sub-table">
            <div class="st-row st-head muted">
              <span>時段</span><span class="r">上班日</span><span class="r">假日</span><span />
            </div>
            <div v-for="(s, si) in c.slots" :key="si" class="st-row">
              <input v-model="s.name" class="input compact" placeholder="例如 早餐" maxlength="40" aria-label="時段名稱" @input="touch" />
              <input v-model.number="s.workdayAmount" class="input compact num" inputmode="numeric" aria-label="上班日金額" @input="touch" />
              <input v-model.number="s.holidayAmount" class="input compact num" inputmode="numeric" aria-label="假日金額" @input="touch" />
              <button type="button" class="btn quiet sm danger" @click="c.slots.splice(si, 1); touch()">移除</button>
            </div>
            <button type="button" class="btn sm add" @click="c.slots.push({ id: 0, name: '', workdayAmount: 0, holidayAmount: 0 }); touch()">新增時段</button>
          </div>

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
              <input v-model.number="f.amount" class="input compact num fx-amt" inputmode="numeric" placeholder="金額" aria-label="金額" @input="touch" />
              <input v-model.number="f.dueDay" class="input compact num fx-due" inputmode="numeric" placeholder="扣款日" aria-label="扣款日" @input="touch" />
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

      <div class="save-bar">
        <span class="muted">{{ saved ? '已儲存' : dirty ? '有未儲存的變更' : '' }}</span>
        <button class="btn primary" :disabled="saving || !dirty" @click="save">{{ saving ? '儲存中' : '儲存設定' }}</button>
      </div>

      <section class="section">
        <h2 class="section-title">{{ periodWord }}假日<span class="aside">點一下切換：照行事曆 → 假日 → 上班日</span></h2>
        <div class="panel cal">
          <div v-for="w in ['日', '一', '二', '三', '四', '五', '六']" :key="w" class="wd">{{ w }}</div>
          <template v-for="(d, i) in calCells" :key="i">
            <div v-if="!d" class="cell blank" />
            <button
              v-else
              type="button"
              class="cell"
              :class="{ hol: d.isHoliday, custom: d.name?.startsWith('自訂') }"
              :title="d.name ?? (d.isHoliday ? '假日' : '上班日')"
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
  </div>
</template>

<style scoped>
.two {
  display: grid;
  grid-template-columns: 1fr 1fr;
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
.st-row {
  display: grid;
  grid-template-columns: 1fr 90px 90px 56px;
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
    grid-template-columns: 1fr 70px 70px 48px;
  }
  .two {
    grid-template-columns: 1fr;
  }
}
</style>
