<script setup lang="ts">
import { computed, ref } from 'vue'
import { RouterLink } from 'vue-router'
import DayPanel from '../components/DayPanel.vue'
import ReportSheet from '../components/ReportSheet.vue'
import ExtraSheet from '../components/ExtraSheet.vue'
import CategorySummary from '../components/CategorySummary.vue'
import IncomeSheet from '../components/IncomeSheet.vue'
import AllocateSheet from '../components/AllocateSheet.vue'
import { confirmIncome } from '../api/endpoints'
import { setPeriod } from '../lib/store'
import type { SlotView } from '../api/types'
import { store } from '../lib/store'
import { dayLabel, money, signed } from '../lib/format'

const period = computed(() => store.period!)
const today = computed(() => period.value.days.find((d) => d.status === 'today') ?? period.value.days[0])
const dayIndex = computed(() => period.value.days.findIndex((d) => d.date === today.value.date) + 1)

const variableCategories = computed(() => period.value.categories.filter((c) => c.mode !== 'Fixed'))

/** 今天還能花：還沒回報的時段額度加總（回報過的就不算）。 */
const leftToday = computed(() => today.value.slots.filter((s) => s.actual === null).reduce((a, s) => a + s.planned, 0))

const reporting = ref<{ slot: SlotView; name: string } | null>(null)
const addingExtra = ref(false)
const showIncome = ref(false)
const showAllocate = ref(false)

// ---- 月結（§12.2）----
/** 發薪日前 5 天（本期最後 5 天）起可以月結 */
const MONTH_END_OPEN_DAYS = 5
const monthEndOpen = computed(() => {
  if (period.value.closed) return false
  const end = new Date(period.value.endDate + 'T00:00:00Z')
  end.setUTCDate(end.getUTCDate() + 1 - MONTH_END_OPEN_DAYS)
  const openFrom = end.toISOString().slice(0, 10)
  return period.value.today >= openFrom
})
/** 上期還沒月結：先擋住，可以選擇晚點再說（只在這次開著時有效） */
const gateDismissed = ref(false)
const gated = computed(() => period.value.previousPeriodNeedsClosing !== null && !gateDismissed.value)

async function confirmNoChange() {
  setPeriod(await confirmIncome(period.value.id))
  if (period.value.pool.balance > 0) showAllocate.value = true
}
</script>

<template>
  <div class="page wide">
    <header class="page-head">
      <div>
        <h1>今天</h1>
        <p class="sub">
          {{ dayLabel(today.date) }}・{{ today.isHoliday ? today.holidayName ?? '假日' : '上班日' }}・第 {{ dayIndex }} / {{ period.days.length }} 天
        </p>
      </div>
    </header>

    <section v-if="period.previousPeriodNeedsClosing !== null" class="panel gate">
      <div>
        <b>上一期還沒月結</b>
        <p class="muted">先對帳、看結果、決定結餘或超支怎麼處理，這期的期初才會是對的。</p>
      </div>
      <div class="gate-actions">
        <button v-if="gated" type="button" class="btn" @click="gateDismissed = true">晚點再說</button>
        <RouterLink :to="`/month-end/${period.previousPeriodNeedsClosing}`" class="btn primary">去月結</RouterLink>
      </div>
    </section>

    <template v-if="!gated">
    <section v-if="monthEndOpen" class="panel gate soft">
      <div>
        <b>可以月結了</b>
        <p class="muted">這期剩最後幾天。月結前記得先完整對帳一次。</p>
      </div>
      <RouterLink :to="`/month-end/${period.id}`" class="btn">月結</RouterLink>
    </section>

    <p v-for="w in period.warnings" :key="w" class="notice-line">{{ w }}</p>

    <section class="panel hero-box">
      <div class="hero-main">
        <span class="label">今日額度</span>
        <span class="hero"><span class="hero-unit">NT$</span>{{ money(today.planned) }}</span>
        <span class="muted small num">
          還沒回報的時段合計 {{ money(leftToday) }}
          <template v-if="today.planned !== today.basePlanned">・原本 {{ money(today.basePlanned) }}</template>
        </span>
      </div>
      <div class="hero-side">
        <div>
          <span class="label">今日差額</span>
          <span class="side-v num" :class="today.net > 0 ? 'good' : today.net < 0 ? 'bad' : ''">{{ signed(today.net) }}</span>
        </div>
        <div>
          <RouterLink to="/overview" class="pool-link">
            <span class="label">待分配池</span>
            <span class="side-v num" :class="period.pool.balance < 0 ? 'bad' : ''">{{ money(period.pool.balance) }}</span>
          </RouterLink>
          <button v-if="period.pool.balance > 0" type="button" class="btn quiet sm alloc" @click="showAllocate = true">分配剩餘</button>
        </div>
      </div>
    </section>

    <section v-if="!period.incomeConfirmed" class="panel payday">
      <div>
        <b>確認本期薪資</b>
        <p class="muted num">
          設定的月收入 {{ money(period.baseIncome) }} 只是預設值。這期有請假（病假、事假）、加班或獎金嗎？調整後待分配池會跟著變。
        </p>
      </div>
      <div class="payday-actions">
        <button type="button" class="btn" @click="showIncome = true">調整薪資</button>
        <button type="button" class="btn primary" @click="confirmNoChange">沒有變動</button>
      </div>
    </section>

    <div class="cols">
      <section class="section">
        <h2 class="section-title">檢核<span class="aside">沒回報 = 照預算</span></h2>
        <DayPanel :day="today" @report="(s, n) => (reporting = { slot: s, name: n })" @extra="addingExtra = true" />
      </section>

      <section class="section">
        <h2 class="section-title">本期分類<span class="aside">預計 / 額度</span></h2>
        <div class="panel">
          <template v-for="(c, i) in variableCategories" :key="c.categoryId">
            <hr v-if="i > 0" class="rule" />
            <CategorySummary :category="c" :period="period" />
          </template>
        </div>
      </section>
    </div>

    </template>

    <ReportSheet
      v-if="reporting"
      :date="today.date"
      :slot="reporting.slot"
      :category-name="reporting.name"
      @close="reporting = null"
    />
    <ExtraSheet v-if="addingExtra" :date="today.date" @close="addingExtra = false" />
    <IncomeSheet v-if="showIncome" @close="showIncome = false" @allocate="showIncome = false; showAllocate = true" />
    <AllocateSheet v-if="showAllocate" @close="showAllocate = false" />
  </div>
</template>

<style scoped>
.hero-box {
  display: flex;
  justify-content: space-between;
  align-items: stretch;
  gap: 16px;
  padding: 18px 20px;
  flex-wrap: wrap;
}
.hero-main {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.label {
  font-size: 12px;
  color: var(--muted);
  letter-spacing: 0.06em;
  display: block;
}
.small {
  font-size: 12px;
}
.hero-side {
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  gap: 10px;
  padding-left: 20px;
  border-left: 1px solid var(--line);
  min-width: 130px;
}
.side-v {
  font-size: 22px;
  font-weight: 600;
}
.pool-link {
  color: inherit;
  text-decoration: none;
}
.alloc {
  margin-left: -8px;
  color: var(--accent);
}
.payday {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 14px;
  flex-wrap: wrap;
  padding: 14px 16px;
  border-left: 3px solid var(--accent);
}
.gate {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 14px;
  flex-wrap: wrap;
  padding: 14px 16px;
  border-left: 3px solid var(--warn-text);
}
.gate.soft {
  border-left-color: var(--accent);
}
.gate p {
  font-size: 13px;
  margin-top: 2px;
}
.gate-actions {
  display: flex;
  gap: 8px;
}
.payday p {
  font-size: 13px;
  margin-top: 2px;
}
.payday-actions {
  display: flex;
  gap: 8px;
}
.pool-link:hover .side-v {
  text-decoration: underline;
  text-underline-offset: 3px;
}
.cols {
  display: grid;
  gap: 24px;
}
@media (min-width: 900px) {
  .cols {
    grid-template-columns: 1.2fr 1fr;
    align-items: start;
  }
}
@media (max-width: 480px) {
  .hero-side {
    flex-direction: row;
    border-left: 0;
    border-top: 1px solid var(--line);
    padding: 12px 0 0;
    width: 100%;
  }
  .hero-side > * {
    flex: 1;
  }
}
</style>
