<script setup lang="ts">
import { computed, ref } from 'vue'
import { RouterLink } from 'vue-router'
import DayPanel from '../components/DayPanel.vue'
import SwipeStack from '../components/SwipeStack.vue'
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

/**
 * 今天頁：一打開就是一個大數字「今天還能花」。
 * 常用的動作都在一步之內——時段旁邊直接按「照預算」、底部固定「記一筆」；
 * 次要的資訊（本期分類）收合起來，其他功能用下面的捷徑進去。
 */
const period = computed(() => store.period!)
const today = computed(() => period.value.days.find((d) => d.status === 'today') ?? period.value.days[0])
const dayIndex = computed(() => period.value.days.findIndex((d) => d.date === today.value.date) + 1)

const variableCategories = computed(() => period.value.categories.filter((c) => c.mode !== 'Fixed'))
const overCount = computed(() => variableCategories.value.filter((c) => (c.mode === 'Daily' ? c.projected : c.spent) > c.budget).length)

/** 今天還能花：還沒回報的時段額度加總（回報過的就不算）。 */
const leftToday = computed(() => today.value.slots.filter((s) => s.actual === null).reduce((a, s) => a + s.planned, 0))
/** 今天已經花掉的：回報過的時段 + 今天的額外花費 */
const spentToday = computed(() => {
  const extras = new Set(today.value.extraEntryIds)
  return (
    today.value.slots.reduce((a, s) => a + (s.actual ?? 0), 0) +
    period.value.entries.filter((e) => extras.has(e.id)).reduce((a, e) => a + e.actual, 0)
  )
})
const spentPct = computed(() => {
  const total = spentToday.value + leftToday.value
  return total > 0 ? Math.min(100, Math.round((spentToday.value / total) * 100)) : 0
})

const reporting = ref<{ slot: SlotView; name: string; date: string } | null>(null)
const addingExtra = ref(false)
const showIncome = ref(false)
const showAllocate = ref(false)

// 本期分類：預設收起來，記住上次的選擇
const FOLD_KEY = 'daycap.today-cats-open'
const catsOpen = ref(
  (() => {
    try {
      return localStorage.getItem(FOLD_KEY) === '1'
    } catch {
      return false
    }
  })(),
)
function toggleCats() {
  catsOpen.value = !catsOpen.value
  try {
    localStorage.setItem(FOLD_KEY, catsOpen.value ? '1' : '0')
  } catch {
    /* 記不住就算了 */
  }
}

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
  <div class="page today">
    <header class="page-head">
      <div>
        <h1>今天</h1>
        <p class="sub">
          {{ dayLabel(today.date) }}・{{ today.isHoliday ? today.holidayName ?? '假日' : '上班日' }}・第 {{ dayIndex }} / {{ period.days.length }} 天
        </p>
      </div>
      <button type="button" class="btn primary head-add" @click="addingExtra = true">記一筆</button>
    </header>

    <section v-if="period.previousPeriodNeedsClosing !== null" class="note strong">
      <div>
        <b>上一期還沒月結</b>
        <p class="muted">先對帳、看結果、決定結餘或超支怎麼處理，這期的期初才會是對的。</p>
      </div>
      <div class="note-actions">
        <button v-if="gated" type="button" class="btn sm" @click="gateDismissed = true">晚點再說</button>
        <RouterLink :to="`/month-end/${period.previousPeriodNeedsClosing}`" class="btn sm primary">去月結</RouterLink>
      </div>
    </section>

    <template v-if="!gated">
      <!-- 大數字：今天還能花 -->
      <section class="today-hero">
        <span class="h-label">今天還能花</span>
        <span class="h-num num"><span class="h-unit">NT$</span>{{ money(leftToday) }}</span>
        <div class="h-bar" role="img" :aria-label="`今天已花 ${spentPct}%`"><span :style="{ width: spentPct + '%' }" /></div>
        <div class="h-meta num">
          <span>已花 {{ money(spentToday) }}</span>
          <span>
            今日額度 {{ money(today.planned) }}<template v-if="today.planned !== today.basePlanned">（原本 {{ money(today.basePlanned) }}）</template>
          </span>
        </div>
      </section>

      <div class="stats">
        <div class="stat">
          <span class="s-label">今日差額</span>
          <span class="s-val num" :class="today.net > 0 ? 'good' : today.net < 0 ? 'bad' : ''">{{ signed(today.net) }}</span>
        </div>
        <div class="stat">
          <RouterLink to="/overview" class="pool-link">
            <span class="s-label">待分配池</span>
            <span class="s-val num" :class="period.pool.balance < 0 ? 'bad' : ''">{{ money(period.pool.balance) }}</span>
          </RouterLink>
          <button v-if="period.pool.balance > 0" type="button" class="btn sm alloc" @click="showAllocate = true">分配</button>
        </div>
      </div>

      <section v-if="!period.incomeConfirmed" class="note">
        <div>
          <b>這期薪水有變動嗎？</b>
          <p class="muted num">預設 {{ money(period.baseIncome) }}。有請假、加班或獎金就調整，待分配池會跟著變。</p>
        </div>
        <div class="note-actions">
          <button type="button" class="btn sm" @click="showIncome = true">調整</button>
          <button type="button" class="btn sm primary" @click="confirmNoChange">沒有變動</button>
        </div>
      </section>

      <section v-if="monthEndOpen" class="note">
        <div>
          <b>可以月結了</b>
          <p class="muted">這期剩最後幾天。月結前先完整對帳一次。</p>
        </div>
        <RouterLink :to="`/month-end/${period.id}`" class="btn sm">月結</RouterLink>
      </section>

      <p v-for="w in period.warnings" :key="w" class="notice-line">{{ w }}</p>

      <SwipeStack @correct="(s, n, d) => (reporting = { slot: s, name: n, date: d })" />

      <section class="section">
        <h2 class="section-title">今天的時段<span class="aside">沒回報 = 照預算</span></h2>
        <DayPanel :day="today" @report="(s, n) => (reporting = { slot: s, name: n, date: today.date })" @extra="addingExtra = true" />
      </section>

      <section class="section">
        <button type="button" class="fold" :aria-expanded="catsOpen" @click="toggleCats">
          <span class="section-title">本期分類</span>
          <span class="fold-side">
            <span v-if="overCount" class="bad">{{ overCount }} 個會超過</span>
            <span v-else class="muted">都在額度內</span>
            <span class="fold-mark">{{ catsOpen ? '收起' : '展開' }}</span>
          </span>
        </button>
        <div v-if="catsOpen" class="panel">
          <template v-for="(c, i) in variableCategories" :key="c.categoryId">
            <hr v-if="i > 0" class="rule" />
            <CategorySummary :category="c" :period="period" />
          </template>
        </div>
      </section>

      <!-- 其他功能的捷徑：不用記得它們在哪一頁 -->
      <nav class="shortcuts" aria-label="其他功能">
        <RouterLink to="/assets" class="shortcut">對帳</RouterLink>
        <RouterLink to="/overview#jars" class="shortcut">罐子</RouterLink>
        <RouterLink to="/assets#debts" class="shortcut">分帳</RouterLink>
        <RouterLink to="/overview#trips" class="shortcut">旅遊</RouterLink>
        <RouterLink to="/month" class="shortcut">補登其他天</RouterLink>
      </nav>
    </template>

    <!-- 手機：最常用的動作固定在底部 -->
    <div v-if="!gated" class="add-bar">
      <button type="button" class="btn primary block" @click="addingExtra = true">記一筆花費</button>
    </div>

    <ReportSheet
      v-if="reporting"
      :date="reporting.date"
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
/* 外觀由 style.css 的設計層（--hero-* 等變數）決定，這裡只管結構 */
.today-hero {
  display: flex;
  flex-direction: column;
  gap: 6px;
  background: var(--hero-bg, transparent);
  color: var(--hero-ink, var(--ink));
  padding: var(--hero-pad, 4px 0);
  border-radius: var(--hero-radius, 0);
  border-top: var(--hero-rule-top, 0);
  border-bottom: var(--hero-rule-bottom, 0);
}
.h-label {
  font-size: 14px;
  font-weight: 600;
  color: var(--hero-muted, var(--muted));
}
.h-num {
  font-family: var(--num-font, inherit);
  font-size: var(--hero-size, 72px);
  line-height: 1;
  font-weight: 700;
  letter-spacing: -0.02em;
}
.h-unit {
  font-family: var(--sans);
  font-size: 16px;
  font-weight: 600;
  letter-spacing: 0.04em;
  margin-right: 8px;
  color: var(--hero-muted, var(--muted));
}
.h-bar {
  height: 8px;
  border-radius: 4px;
  background: var(--hero-track, var(--sunk));
  overflow: hidden;
  margin-top: 10px;
}
.h-bar span {
  display: block;
  height: 100%;
  background: var(--hero-fill, var(--accent));
  border-radius: 4px;
}
.h-meta {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
  font-size: 14px;
  color: var(--hero-muted, var(--muted));
}
.stats {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--stat-gap, 10px);
}
.stat {
  background: var(--panel-bg, var(--surface));
  border: var(--panel-border, 0);
  border-radius: var(--radius);
  padding: 14px 16px;
  display: flex;
  justify-content: space-between;
  align-items: flex-end;
  gap: 8px;
  min-width: 0;
}
.s-label {
  display: block;
  font-size: 13px;
  color: var(--muted);
}
.s-val {
  display: block;
  font-family: var(--num-font, inherit);
  font-size: 26px;
  font-weight: 700;
  line-height: 1.2;
}
.pool-link {
  color: inherit;
  text-decoration: none;
  min-width: 0;
}
.note {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
  padding: 14px 16px;
  border-radius: var(--radius);
  background: var(--accent-soft);
}
.note.strong {
  background: var(--sunk);
}
.note p {
  font-size: 13px;
  margin-top: 2px;
}
.note-actions {
  display: flex;
  gap: 8px;
}
.fold {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 8px;
  width: 100%;
  background: none;
  border: 0;
  padding: 4px 0;
  cursor: pointer;
  text-align: left;
}
.fold-side {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 13px;
}
.fold-mark {
  color: var(--accent);
  font-weight: 600;
}
.shortcuts {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
.shortcut {
  padding: 8px 14px;
  border-radius: var(--btn-radius, 10px);
  background: var(--panel-bg, var(--surface));
  border: var(--panel-border, 0);
  color: var(--ink-2);
  text-decoration: none;
  font-size: 14px;
  font-weight: 500;
}
.add-bar {
  display: none;
}
@media (max-width: 719px) {
  .head-add {
    display: none;
  }
  .today {
    padding-bottom: calc(var(--nav-h) + 96px + env(safe-area-inset-bottom, 0px));
  }
  .add-bar {
    display: block;
    position: fixed;
    left: var(--gutter);
    right: var(--gutter);
    bottom: calc(var(--nav-h) + 10px + env(safe-area-inset-bottom, 0px));
    z-index: 25;
  }
  .add-bar .btn {
    min-height: 50px;
    font-size: 16px;
  }
}
@media (max-width: 380px) {
  .h-num {
    font-size: min(var(--hero-size, 72px), 60px);
  }
}
</style>
