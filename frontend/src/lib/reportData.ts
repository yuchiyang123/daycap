import type { CategoryView, PeriodView } from '../api/types'
import type { Slice } from '../charts/Donut.vue'
import type { Column } from '../charts/ColumnChart.vue'
import { parseDate, shortDate } from './format'

/** 圖表與報告共用的資料整理（總覽頁、報告頁都用這裡，數字才會一致）。 */

// 顏色跟著分類的設定順序固定，不會因為篩選或排序重新上色。
const SLOTS = ['--series-1', '--series-2', '--series-3', '--series-4', '--series-5', '--series-6', '--series-7', '--series-8']
export const seriesColor = (i: number) => (i < SLOTS.length ? `var(${SLOTS[i]})` : 'var(--line-2)')

/** 實際額度：設定額度扣掉已轉進待分配池的少花（超支由池補則加回）。 */
export const effectiveBudget = (c: CategoryView) => c.budget - (c.toPool ?? 0)

/** 到今天為止，錢花到哪些分類（固定支出、儲蓄也算）。超過 8 類的併成「其他」。 */
export function spendByCategory(p: PeriodView): Slice[] {
  const cats = p.categories
  const slices: Slice[] = cats.slice(0, 7).map((c, i) => ({ key: c.categoryId, label: c.name, value: Math.max(0, c.spent), color: seriesColor(i) }))
  const tail = cats.slice(7)
  if (tail.length === 1) slices.push({ key: tail[0].categoryId, label: tail[0].name, value: Math.max(0, tail[0].spent), color: seriesColor(7) })
  else if (tail.length > 1)
    slices.push({ key: 'other', label: '其他分類', value: tail.reduce((a, c) => a + Math.max(0, c.spent), 0), color: seriesColor(7), note: tail.map((c) => c.name).join('、') })
  return slices
}

/** 每日額度類（例如餐費、交通）到今天為止，各時段花了多少；沒回報的照預算算。 */
export function spendBySlot(p: PeriodView): Slice[] {
  const daily = new Set(p.categories.filter((c) => c.mode === 'Daily').map((c) => c.categoryId))
  const totals = new Map<string, number>()
  const order: string[] = []
  const add = (k: string, v: number) => {
    if (!totals.has(k)) order.push(k)
    totals.set(k, (totals.get(k) ?? 0) + v)
  }
  for (const d of p.days) {
    if (d.status === 'future') continue
    for (const s of d.slots) if (daily.has(s.categoryId)) add(s.name, s.actual ?? s.planned)
    for (const id of d.extraEntryIds) {
      const e = p.entries.find((x) => x.id === id)
      if (e && daily.has(e.categoryId)) add('額外花費', e.actual)
    }
  }
  return order.map((k, i) => ({ key: k, label: k, value: Math.max(0, totals.get(k) ?? 0), color: seriesColor(i) }))
}

/** 以本期第一天起算、每 7 天一週：每日額度類的實際花費對照原排程。 */
export function weeklyDaily(p: PeriodView): Column[] {
  const daily = new Set(p.categories.filter((c) => c.mode === 'Daily').map((c) => c.categoryId))
  const cols: Column[] = []
  for (let i = 0; i < p.days.length; i += 7) {
    const week = p.days.slice(i, i + 7)
    const plan = week.reduce((a, d) => a + d.slots.filter((s) => daily.has(s.categoryId)).reduce((x, s) => x + s.basePlanned, 0), 0)
    const started = week.some((d) => d.status !== 'future')
    let value = 0
    for (const d of week) {
      if (d.status === 'future') {
        value += d.slots.filter((s) => daily.has(s.categoryId)).reduce((x, s) => x + s.planned, 0)
        continue
      }
      value += d.slots.filter((s) => daily.has(s.categoryId)).reduce((x, s) => x + (s.actual ?? s.planned), 0)
      value += d.extraEntryIds
        .map((id) => p.entries.find((e) => e.id === id))
        .filter((e) => e && daily.has(e.categoryId))
        .reduce((x, e) => x + e!.actual, 0)
    }
    const first = week[0].date
    const last = week[week.length - 1].date
    cols.push({
      key: first,
      label: `第${cols.length + 1}週`,
      // 整週都還沒到就不畫實際值；進行中的這週＝已花＋之後的排程
      value: started ? value : null,
      plan,
      note: `${shortDate(first)}–${shortDate(last)}${week.some((d) => d.status === 'today') ? '（本週，含之後排程）' : ''}`,
    })
  }
  return cols
}

/** 本期到今天為止的整體數字。 */
export function summary(p: PeriodView) {
  const spent = p.categories.reduce((a, c) => a + c.spent, 0)
  const projected = p.categories.reduce((a, c) => a + c.projected, 0)
  const savings = p.categories.filter((c) => c.group === 'Savings').reduce((a, c) => a + c.spent, 0)
  const start = parseDate(p.startDate).getTime()
  const end = parseDate(p.endDate).getTime()
  const now = parseDate(p.today).getTime()
  const elapsed = Math.min(1, Math.max(0, (now - start) / 86_400_000 + 1) / ((end - start) / 86_400_000 + 1))
  return {
    income: p.income,
    spent,
    projected,
    savings,
    pool: p.pool.balance,
    // 照目前的預計，期末還會剩多少（待分配池 + 各分類實際額度沒用完的部分）
    leftover: p.income - projected,
    elapsed,
  }
}
