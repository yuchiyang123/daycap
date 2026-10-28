<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink } from 'vue-router'
import Donut from '../charts/Donut.vue'
import ColumnChart from '../charts/ColumnChart.vue'
import LineChart, { type Series } from '../charts/LineChart.vue'
import { store } from '../lib/store'
import { effectiveBudget, seriesColor, spendByCategory, spendBySlot, summary, weeklyDaily } from '../lib/reportData'
import { modeLabel, money, pct, shortDate, signed, toIso } from '../lib/format'

/**
 * 本期報告：一頁看完這期的錢，按「匯出 PDF」用瀏覽器的列印存成 PDF
 * （iPhone：分享 → 列印 → 兩指放大預覽 → 分享 → 儲存到檔案）。
 * 另外可以下載所有記錄的 CSV，自己丟進試算表。
 */
const period = computed(() => store.period!)
const s = computed(() => summary(period.value))
const byCategory = computed(() => spendByCategory(period.value))
const bySlot = computed(() => spendBySlot(period.value))
const weekly = computed(() => weeklyDaily(period.value))
const generated = toIso(new Date())

// 每日額度類累計：原排程 vs 實際（跟總覽頁同一種算法）
const cumulative = computed(() => {
  const daily = new Set(period.value.categories.filter((c) => c.mode === 'Daily').map((c) => c.categoryId))
  let planned = 0
  let actual = 0
  const p: number[] = []
  const a: (number | null)[] = []
  for (const d of period.value.days) {
    planned += d.slots.filter((x) => daily.has(x.categoryId)).reduce((t, x) => t + x.basePlanned, 0)
    p.push(planned)
    if (d.status === 'future') {
      a.push(null)
      continue
    }
    actual += d.slots.filter((x) => daily.has(x.categoryId)).reduce((t, x) => t + (x.actual ?? x.planned), 0)
    actual += d.extraEntryIds
      .map((id) => period.value.entries.find((e) => e.id === id))
      .filter((e) => e && daily.has(e.categoryId))
      .reduce((t, e) => t + e!.actual, 0)
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

const entries = computed(() =>
  [...period.value.entries].sort((a, b) => (a.date === b.date ? a.id - b.id : a.date < b.date ? -1 : 1)),
)
const poolLines = computed(() => period.value.pool.lines.filter((l) => l.amount !== 0))

function exportPdf() {
  window.print()
}

function downloadCsv() {
  const esc = (v: string | number | null | undefined) => {
    const t = v === null || v === undefined ? '' : String(v)
    return /[",\n]/.test(t) ? `"${t.replace(/"/g, '""')}"` : t
  }
  const rows = [
    ['日期', '分類', '時段', '細項', '金額', '當時額度', '差額', '備註', '逐筆明細'],
    ...entries.value.map((e) => [
      e.date,
      e.categoryName,
      e.slotName ?? '',
      e.subItem ?? '',
      e.actual,
      e.slotId ? e.plannedAtEntry : '',
      e.slotId ? e.plannedAtEntry - e.actual : '',
      e.note ?? '',
      e.parts?.length ? e.parts.join('+') : '',
    ]),
    [],
    ['固定支出', '分類', '', '', '金額', '扣款日'],
    ...period.value.fixedCharges.map((f) => [
      f.name,
      period.value.categories.find((c) => c.categoryId === f.categoryId)?.name ?? '',
      '',
      '',
      f.amount,
      f.dueDate ?? '',
    ]),
  ]
  // 加 BOM，Excel 打開中文才不會亂碼
  const blob = new Blob(['﻿' + rows.map((r) => r.map(esc).join(',')).join('\r\n')], { type: 'text/csv;charset=utf-8' })
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = `daycap-${period.value.startDate}-${period.value.endDate}.csv`
  a.click()
  setTimeout(() => URL.revokeObjectURL(a.href), 1000)
}
</script>

<template>
  <div class="page wide report">
    <header class="page-head">
      <div>
        <h1>本期報告</h1>
        <p class="sub num">
          {{ period.startDate }} – {{ period.endDate }}・已過 {{ pct(s.elapsed) }}・產生於 {{ generated }}
        </p>
      </div>
      <div class="actions no-print">
        <RouterLink to="/overview" class="btn sm">回總覽</RouterLink>
        <button type="button" class="btn sm" @click="downloadCsv">下載 CSV</button>
        <button type="button" class="btn sm primary" @click="exportPdf">匯出 PDF</button>
      </div>
    </header>
    <p class="muted small no-print hint">
      「匯出 PDF」會開啟列印，目的地選「儲存為 PDF」。iPhone 上是在列印預覽用兩指放大，再按分享存到檔案。
    </p>

    <div class="tiles">
      <div class="tile">
        <span class="label">實領</span>
        <span class="value">{{ money(s.income) }}</span>
      </div>
      <div class="tile">
        <span class="label">到今天已花</span>
        <span class="value">{{ money(s.spent) }}</span>
        <span class="note">{{ pct(s.spent / Math.max(1, s.income)) }} 的收入，含固定與儲蓄</span>
      </div>
      <div class="tile">
        <span class="label">儲蓄</span>
        <span class="value">{{ money(s.savings) }}</span>
        <span class="note">{{ pct(s.savings / Math.max(1, s.income)) }}</span>
      </div>
      <div class="tile">
        <span class="label">預計期末剩</span>
        <span class="value" :class="s.leftover < 0 ? 'bad' : ''">{{ money(s.leftover) }}</span>
        <span class="note">待分配池 {{ money(s.pool) }}</span>
      </div>
    </div>

    <div class="grid-2 avoid-break">
      <section class="section">
        <h2 class="section-title">錢花到哪</h2>
        <div class="panel panel-pad">
          <Donut :slices="byCategory" center-label="已花" caption="各分類花費比例" />
        </div>
      </section>
      <section class="section">
        <h2 class="section-title">每日時段</h2>
        <div class="panel panel-pad">
          <Donut :slices="bySlot" center-label="每日額度類" caption="各時段花費比例" />
        </div>
      </section>
    </div>

    <section class="section avoid-break">
      <h2 class="section-title">每週花費<span class="aside">每日額度類；橫線 = 原排程</span></h2>
      <div class="panel panel-pad">
        <ColumnChart :columns="weekly" caption="每週花費" plan-label="原排程" />
      </div>
    </section>

    <section class="section avoid-break">
      <h2 class="section-title">每日額度類累計<span class="aside">虛線 = 原本排程</span></h2>
      <div class="panel panel-pad">
        <LineChart :series="lineSeries" :labels="lineLabels" :marker-index="todayIndex" :height="200" />
      </div>
    </section>

    <section class="section avoid-break">
      <h2 class="section-title">各分類</h2>
      <div class="panel table-wrap">
        <table class="tbl">
          <thead>
            <tr>
              <th>分類</th>
              <th class="r">設定額度</th>
              <th class="r">轉進池</th>
              <th class="r">實際額度</th>
              <th class="r">已花</th>
              <th class="r">預計</th>
              <th class="r">剩</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(c, i) in period.categories" :key="c.categoryId">
              <td>
                <span class="sw" :style="{ background: seriesColor(i) }" />{{ c.name }}
                <span class="tag">{{ modeLabel[c.mode] }}</span>
              </td>
              <td class="r num">{{ money(c.budget) }}</td>
              <td class="r num muted">{{ c.toPool ? signed(c.toPool) : '—' }}</td>
              <td class="r num">{{ money(effectiveBudget(c)) }}</td>
              <td class="r num">{{ money(c.spent) }}</td>
              <td class="r num">{{ money(c.projected) }}</td>
              <td class="r num" :class="effectiveBudget(c) - c.projected < 0 ? 'bad' : ''">{{ money(effectiveBudget(c) - c.projected) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>

    <div class="grid-2">
      <section class="section">
        <h2 class="section-title">固定支出</h2>
        <div class="panel">
          <ul class="list rows">
            <li v-for="(f, i) in period.fixedCharges" :key="i">
              <span class="d num muted">{{ f.dueDate ? shortDate(f.dueDate) : '—' }}</span>
              <span>{{ f.name }}</span>
              <span class="num">{{ money(f.amount) }}</span>
            </li>
          </ul>
        </div>
      </section>
      <section class="section">
        <h2 class="section-title">待分配池<span class="aside num">餘額 {{ money(period.pool.balance) }}</span></h2>
        <div class="panel">
          <ul class="list rows">
            <li v-for="(l, i) in poolLines" :key="i">
              <span class="d num muted">{{ shortDate(l.date) }}</span>
              <span>{{ l.label }}</span>
              <span class="num" :class="l.amount > 0 ? 'good' : 'bad'">{{ signed(l.amount) }}</span>
            </li>
          </ul>
        </div>
      </section>
    </div>

    <section class="section">
      <h2 class="section-title">所有記錄<span class="aside">{{ entries.length }} 筆</span></h2>
      <div class="panel table-wrap">
        <table class="tbl">
          <thead>
            <tr>
              <th>日期</th>
              <th>分類</th>
              <th>時段・備註</th>
              <th class="r">金額</th>
              <th class="r">差額</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="e in entries" :key="e.id">
              <td class="num">{{ shortDate(e.date) }}</td>
              <td>{{ e.categoryName }}</td>
              <td>
                {{ e.slotName ?? '' }}<template v-if="e.slotName && e.note">・</template>{{ e.note ?? '' }}
                <span v-if="e.parts && e.parts.length > 1" class="muted small">（{{ e.parts.map((p) => money(p)).join(' + ') }}）</span>
              </td>
              <td class="r num">{{ money(e.actual) }}</td>
              <td class="r num" :class="e.slotId ? (e.plannedAtEntry - e.actual >= 0 ? 'good' : 'bad') : 'muted'">
                {{ e.slotId ? signed(e.plannedAtEntry - e.actual) : '—' }}
              </td>
            </tr>
          </tbody>
        </table>
        <p v-if="entries.length === 0" class="empty">這期還沒有記錄。</p>
      </div>
    </section>
  </div>
</template>

<style scoped>
.actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}
.hint {
  margin: -6px 0 12px;
}
.small {
  font-size: 12px;
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
.rows li {
  display: grid;
  grid-template-columns: 44px 1fr auto;
  gap: 10px;
  align-items: center;
  padding: 8px 16px;
  font-size: 13px;
}
.d {
  font-size: 12px;
}
</style>

<style>
/* 列印 / 存成 PDF：只留報告本身，白底黑字，圖表不要被切成兩頁 */
@media print {
  @page {
    size: A4;
    margin: 12mm;
  }
  .topbar,
  .tabbar,
  .offline-bar,
  .no-print {
    display: none !important;
  }
  /* 深色模式或其他風格下也印成淺色原版配色，紙上才看得清楚 */
  :root {
    --page: #ffffff !important;
    --surface: #ffffff !important;
    --sunk: #f2f2ef !important;
    --ink: #141412 !important;
    --ink-2: #52514e !important;
    --muted: #6b6964 !important;
    --line: #e1e0d9 !important;
    --line-2: #c3c2b7 !important;
    --accent: #256abf !important;
    --good-text: #006300 !important;
    --bad-text: #b42f2f !important;
    --series-1: #2a78d6 !important;
    --series-2: #eb6834 !important;
    --series-3: #1baf7a !important;
    --series-4: #eda100 !important;
    --series-5: #e87ba4 !important;
    --series-6: #008300 !important;
    --series-7: #4a3aa7 !important;
    --series-8: #e34948 !important;
    --pos: #2a78d6 !important;
    --neg: #e34948 !important;
    --grid: #e1e0d9 !important;
    --axis: #c3c2b7 !important;
    --track: #e8e7e1 !important;
    color-scheme: light !important;
  }
  html,
  body {
    background: #fff !important;
    color: #141412 !important;
  }
  .report {
    max-width: none !important;
    padding: 0 !important;
  }
  .report .panel {
    break-inside: avoid;
  }
  .report .avoid-break {
    break-inside: avoid;
  }
  .report .table-wrap {
    overflow: visible !important;
  }
  * {
    -webkit-print-color-adjust: exact;
    print-color-adjust: exact;
  }
}
</style>
