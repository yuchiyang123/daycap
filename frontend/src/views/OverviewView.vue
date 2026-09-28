<script setup lang="ts">
import { computed, ref } from 'vue'
import AllocationBar, { type Segment } from '../charts/AllocationBar.vue'
import LineChart, { type Series } from '../charts/LineChart.vue'
import Meter from '../charts/Meter.vue'
import { addTransfer, deleteTransfer } from '../api/endpoints'
import IncomeSheet from '../components/IncomeSheet.vue'
import AllocateSheet from '../components/AllocateSheet.vue'
import { store, setPeriod } from '../lib/store'
import { groupLabel, modeLabel, money, pct, shortDate, signed } from '../lib/format'

const period = computed(() => store.period!)
const cats = computed(() => period.value.categories)

// 顏色跟著分類的設定順序固定，不會因為篩選或排序重新上色。
const SLOTS = ['--series-1', '--series-2', '--series-3', '--series-4', '--series-5', '--series-6', '--series-7', '--series-8']
const colorOf = (i: number) => `var(${SLOTS[i % SLOTS.length]})`

const fixedTotal = computed(() => cats.value.filter((c) => c.mode === 'Fixed' && c.group !== 'Savings').reduce((a, c) => a + c.budget, 0))
const savingsTotal = computed(() => cats.value.filter((c) => c.group === 'Savings').reduce((a, c) => a + c.budget, 0))
const variableTotal = computed(() => cats.value.filter((c) => c.mode !== 'Fixed').reduce((a, c) => a + c.budget, 0))
const unallocated = computed(() => period.value.income - cats.value.reduce((a, c) => a + c.budget, 0))

const segments = computed<Segment[]>(() => {
  const head: Segment[] = cats.value.slice(0, 7).map((c, i) => ({
    key: c.categoryId,
    label: c.name,
    value: c.budget,
    color: colorOf(i),
    note: `${groupLabel[c.group]}・${modeLabel[c.mode]}`,
  }))
  const tail = cats.value.slice(7)
  if (tail.length === 1) head.push({ key: tail[0].categoryId, label: tail[0].name, value: tail[0].budget, color: colorOf(7), note: `${groupLabel[tail[0].group]}・${modeLabel[tail[0].mode]}` })
  else if (tail.length > 1) head.push({ key: 'other', label: '其他分類', value: tail.reduce((a, c) => a + c.budget, 0), color: colorOf(7), note: tail.map((c) => c.name).join('、') })
  if (unallocated.value > 0) head.push({ key: 'pool', label: '未分配（進待定區）', value: unallocated.value, color: 'var(--line-2)', note: '' })
  return head
})

// ---- 每日額度類的累計：原排程 vs 實際 ----
const dailyIds = computed(() => new Set(cats.value.filter((c) => c.mode === 'Daily').map((c) => c.categoryId)))
const cumulative = computed(() => {
  let planned = 0
  let actual = 0
  const p: number[] = []
  const a: (number | null)[] = []
  for (const d of period.value.days) {
    planned += d.slots.filter((s) => dailyIds.value.has(s.categoryId)).reduce((x, s) => x + s.basePlanned, 0)
    p.push(planned)
    if (d.status === 'future') {
      a.push(null)
      continue
    }
    const slotSpend = d.slots
      .filter((s) => dailyIds.value.has(s.categoryId))
      .reduce((x, s) => x + (s.actual ?? s.planned), 0)
    const extras = d.extraEntryIds
      .map((id) => period.value.entries.find((e) => e.id === id))
      .filter((e) => e && dailyIds.value.has(e.categoryId))
      .reduce((x, e) => x + e!.actual, 0)
    actual += slotSpend + extras
    a.push(actual)
  }
  return { p, a }
})
const lineSeries = computed<Series[]>(() => [
  { key: 'plan', label: '原排程累計', color: 'var(--ink-2)', values: cumulative.value.p, dashed: true },
  { key: 'actual', label: '實際累計', color: 'var(--series-1)', values: cumulative.value.a },
])
const lineLabels = computed(() => period.value.days.map((d) => shortDate(d.date)))
const todayIndex = computed(() => period.value.days.findIndex((d) => d.status === 'today'))

// ---- 待定區 ----
const transferAmount = ref('')
const transferNote = ref('')
const transferDir = ref<'out' | 'in'>('out')
const busy = ref(false)
const error = ref<string | null>(null)

async function submitTransfer() {
  const n = Math.round(Number(transferAmount.value))
  if (!n || n <= 0) return
  busy.value = true
  error.value = null
  try {
    const note = transferNote.value.trim() || (transferDir.value === 'out' ? '轉出' : '補進')
    setPeriod(await addTransfer(period.value.id, period.value.today, transferDir.value === 'out' ? -n : n, note))
    transferAmount.value = ''
    transferNote.value = ''
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}

async function removeTransfer(id: number) {
  setPeriod(await deleteTransfer(period.value.id, id))
}

const ledger = computed(() => [...period.value.pool.lines].reverse())
const showIncome = ref(false)
const showAllocate = ref(false)
</script>

<template>
  <div class="page wide">
    <header class="page-head">
      <div>
        <h1>總覽</h1>
        <p class="sub">
          {{ shortDate(period.startDate) }} – {{ shortDate(period.endDate) }}・實領 {{ money(period.income) }}
          <template v-if="period.income !== period.baseIncome">（預設 {{ money(period.baseIncome) }}）</template>
        </p>
      </div>
      <div class="head-actions">
        <button class="btn sm" @click="showIncome = true">本期薪資</button>
        <button class="btn sm primary" :disabled="period.pool.balance <= 0" @click="showAllocate = true">分配剩餘</button>
      </div>
    </header>

    <div class="tiles">
      <div class="tile">
        <span class="label">固定支出</span>
        <span class="value">{{ money(fixedTotal) }}</span>
        <span class="note">{{ pct(fixedTotal / Math.max(1, period.income)) }} 的收入</span>
      </div>
      <div class="tile">
        <span class="label">儲蓄</span>
        <span class="value">{{ money(savingsTotal) }}</span>
        <span class="note">{{ pct(savingsTotal / Math.max(1, period.income)) }}</span>
      </div>
      <div class="tile">
        <span class="label">變動額度</span>
        <span class="value">{{ money(variableTotal) }}</span>
        <span class="note">每日 + 月額度</span>
      </div>
      <div class="tile">
        <span class="label">待定區</span>
        <span class="value" :class="period.pool.balance < 0 ? 'bad' : ''">{{ money(period.pool.balance) }}</span>
        <span class="note">期初 {{ money(period.pool.opening) }}</span>
      </div>
    </div>

    <section class="section">
      <h2 class="section-title">收入怎麼分<span class="aside">本期快照</span></h2>
      <div class="panel panel-pad">
        <AllocationBar :segments="segments" :total="period.income" caption="收入分配" />
        <p v-if="period.incomeAdjustments.length" class="muted small adj-note">
          薪資調整：{{ period.incomeAdjustments.map((a) => `${a.label} ${signed(a.amount)}`).join('、') }}
        </p>
      </div>
    </section>

    <section class="section">
      <h2 class="section-title">每日額度類累計花費<span class="aside">虛線 = 原本排程</span></h2>
      <div class="panel panel-pad">
        <LineChart :series="lineSeries" :labels="lineLabels" :marker-index="todayIndex" :height="220" />
      </div>
    </section>

    <section class="section">
      <h2 class="section-title">各分類</h2>
      <div class="panel table-wrap">
        <table class="tbl">
          <thead>
            <tr>
              <th>分類</th>
              <th class="r">額度</th>
              <th class="r">預計</th>
              <th class="r">剩餘</th>
              <th class="meter-col">用量</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(c, i) in cats" :key="c.categoryId">
              <td>
                <span class="sw" :style="{ background: i < 8 ? colorOf(i) : 'var(--line-2)' }" />
                {{ c.name }}
                <span class="tag">{{ modeLabel[c.mode] }}</span>
              </td>
              <td class="r">{{ money(c.budget) }}</td>
              <td class="r">{{ money(c.projected) }}</td>
              <td class="r" :class="c.budget - c.projected < 0 ? 'bad' : ''">{{ money(c.budget - c.projected) }}</td>
              <td class="meter-col">
                <Meter :value="c.projected" :max="c.budget" :level="c.projected > c.budget ? 'over' : c.mode === 'Envelope' && c.projected > c.budget * 0.85 ? 'near' : 'ok'" />
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>

    <div class="grid-2">
      <section class="section">
        <h2 class="section-title">待定區流水<span class="aside num">餘額 {{ money(period.pool.balance) }}</span></h2>
        <div class="panel">
          <form class="transfer" @submit.prevent="submitTransfer">
            <div class="seg" role="group" aria-label="方向">
              <button type="button" :aria-pressed="transferDir === 'out'" @click="transferDir = 'out'">轉出</button>
              <button type="button" :aria-pressed="transferDir === 'in'" @click="transferDir = 'in'">補進</button>
            </div>
            <input v-model="transferAmount" class="input compact num" inputmode="numeric" placeholder="金額" aria-label="金額" />
            <input v-model="transferNote" class="input compact" maxlength="120" placeholder="例如 轉進儲蓄" aria-label="備註" />
            <button class="btn sm primary" :disabled="busy || !transferAmount">記下</button>
          </form>
          <p v-if="error" class="error-box" style="margin: 0 16px 12px">{{ error }}</p>
          <ul class="list ledger">
            <li v-for="(l, i) in ledger" :key="i">
              <span class="d num muted">{{ shortDate(l.date) }}</span>
              <span class="t">{{ l.label }}</span>
              <span class="a num" :class="l.amount > 0 ? 'good' : l.amount < 0 ? 'bad' : ''">{{ signed(l.amount) }}</span>
              <button v-if="l.transferId" type="button" class="btn quiet sm" @click="removeTransfer(l.transferId)">刪除</button>
              <span v-else />
            </li>
          </ul>
        </div>
      </section>

      <section class="section">
        <h2 class="section-title">固定支出<span class="aside">鎖定，不用回報</span></h2>
        <div class="panel">
          <ul class="list">
            <li v-for="f in period.fixedCharges" :key="f.id" class="fx">
              <span class="d num muted">{{ f.dueDate ? shortDate(f.dueDate) : '—' }}</span>
              <span class="t">
                {{ f.name }}
                <span v-if="f.isSubscription" class="tag accent">訂閱</span>
                <span class="muted cat">{{ cats.find((c) => c.categoryId === f.categoryId)?.name }}</span>
              </span>
              <span class="a num">{{ money(f.amount) }}</span>
            </li>
          </ul>
          <p v-if="period.fixedCharges.length === 0" class="empty">沒有固定支出。</p>
        </div>
      </section>
    </div>

    <IncomeSheet v-if="showIncome" @close="showIncome = false" @allocate="showIncome = false; showAllocate = true" />
    <AllocateSheet v-if="showAllocate" @close="showAllocate = false" />
  </div>
</template>

<style scoped>
.head-actions {
  display: flex;
  gap: 8px;
}
.small {
  font-size: 12px;
}
.adj-note {
  margin-top: 10px;
}
.tile .label {
  font-size: 12px;
  color: var(--muted);
}
.sw {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 2px;
  margin-right: 6px;
  vertical-align: 1px;
}
.meter-col {
  width: 28%;
  min-width: 90px;
}
.transfer {
  display: grid;
  grid-template-columns: auto 90px 1fr auto;
  gap: 8px;
  padding: 12px 16px;
  border-bottom: 1px solid var(--line);
  align-items: center;
}
@media (max-width: 480px) {
  .transfer {
    grid-template-columns: auto 1fr;
  }
}
.ledger {
  max-height: 420px;
  overflow-y: auto;
}
.ledger li,
.fx {
  display: grid;
  grid-template-columns: 44px 1fr auto auto;
  gap: 10px;
  align-items: center;
  padding: 9px 16px;
  font-size: 14px;
}
.fx {
  grid-template-columns: 44px 1fr auto;
}
.d {
  font-size: 12px;
}
.a {
  font-weight: 600;
}
.cat {
  font-size: 12px;
  margin-left: 4px;
}
</style>
