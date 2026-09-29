import type { BudgetMode, CategoryGroup } from '../api/types'

const nf = new Intl.NumberFormat('en-US', { maximumFractionDigits: 0 })

/** 12,345；負數用真正的減號（−），跟連字號區分。 */
export function money(n: number): string {
  const s = nf.format(Math.abs(Math.round(n)))
  return n < 0 ? `−${s}` : s
}

/** 帶正負號：+40 / −30 / 0 */
export function signed(n: number): string {
  if (n === 0) return '0'
  return n > 0 ? `+${money(n)}` : money(n)
}

/** 大數字精簡：12.9K / 1.2M，用在圖表軸 */
export function compact(n: number): string {
  const a = Math.abs(n)
  const sign = n < 0 ? '−' : ''
  if (a >= 1_000_000) return `${sign}${trim(a / 1_000_000)}M`
  if (a >= 10_000) return `${sign}${trim(a / 1_000)}K`
  return money(n)
}

function trim(x: number): string {
  return x.toFixed(1).replace(/\.0$/, '')
}

export function pct(ratio: number, digits = 0): string {
  return `${(ratio * 100).toFixed(digits)}%`
}

const WEEK = ['日', '一', '二', '三', '四', '五', '六']

export function parseDate(iso: string): Date {
  const [y, m, d] = iso.split('-').map(Number)
  return new Date(y, m - 1, d)
}

/** 現在的邏輯日：時間往前推「邏輯日起點」再取日期（§3.1）。 */
export function logicalToday(dayStart = '04:00'): string {
  const [h, m] = dayStart.split(':').map(Number)
  return toIso(new Date(Date.now() - (h * 60 + m) * 60_000))
}

export function toIso(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}

export function dayLabel(iso: string): string {
  const d = parseDate(iso)
  return `${d.getMonth() + 1}月${d.getDate()}日 星期${WEEK[d.getDay()]}`
}

export function shortDate(iso: string): string {
  const d = parseDate(iso)
  return `${d.getMonth() + 1}/${d.getDate()}`
}

export function weekday(iso: string): string {
  return WEEK[parseDate(iso).getDay()]
}

export const groupLabel: Record<CategoryGroup, string> = {
  Food: '食',
  Clothing: '衣',
  Housing: '住',
  Transport: '行',
  Education: '育',
  Leisure: '樂',
  Savings: '儲蓄',
  Other: '其他',
}

export const modeLabel: Record<BudgetMode, string> = {
  Fixed: '固定（鎖定）',
  Daily: '每日額度',
  Envelope: '月額度',
}
