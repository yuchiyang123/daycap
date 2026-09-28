export type CategoryGroup = 'Food' | 'Clothing' | 'Housing' | 'Transport' | 'Education' | 'Leisure' | 'Savings' | 'Other'
export type BudgetMode = 'Fixed' | 'Daily' | 'Envelope'
export type BillingCycle = 'Monthly' | 'Yearly'
export type EntryInputMode = 'Actual' | 'Overage'
export type GoalScope = 'All' | 'Cash' | 'Investments'

export interface Me {
  userId: string
  userName: string | null
}

// ---- settings ----
export interface SlotDto {
  id: number
  name: string
  workdayAmount: number
  holidayAmount: number
}

export interface FixedItemDto {
  id: number
  name: string
  amount: number
  dueDay: number | null
  isSubscription: boolean
  cycle: BillingCycle
  billingMonth: number | null
  isActive: boolean
  activeFrom: string | null
}

export interface CategoryDto {
  id: number
  name: string
  group: CategoryGroup
  mode: BudgetMode
  percent: number
  slots: SlotDto[]
  fixedItems: FixedItemDto[]
}

export interface SettingsDto {
  monthlyIncome: number
  cycleStartDay: number
  categories: CategoryDto[]
}

export interface CalendarDayDto {
  date: string
  isHoliday: boolean
  name: string | null
}

// ---- period ----
export interface PeriodSummary {
  id: number
  startDate: string
  endDate: string
  income: number
}

export interface PoolLine {
  date: string
  amount: number
  kind: 'Opening' | 'Surplus' | 'Cover' | 'Unabsorbed' | 'EnvelopeOver' | 'Transfer'
  label: string
  entryId: number | null
  transferId: number | null
}

export interface CategoryView {
  categoryId: number
  name: string
  group: CategoryGroup
  mode: BudgetMode
  budget: number
  scheduled: number
  spent: number
  plannedRemaining: number
  projected: number
}

export interface SlotView {
  categoryId: number
  slotId: number
  name: string
  basePlanned: number
  planned: number
  actual: number | null
  entryId: number | null
}

export interface DayView {
  date: string
  isHoliday: boolean
  holidayName: string | null
  status: 'past' | 'today' | 'future'
  basePlanned: number
  planned: number
  net: number
  slots: SlotView[]
  extraEntryIds: number[]
}

export interface EntryView {
  id: number
  date: string
  categoryId: number
  categoryName: string
  slotId: number | null
  slotName: string | null
  inputMode: EntryInputMode
  inputAmount: number
  actual: number
  plannedAtEntry: number
  diff: number
  usePool: boolean
  fromPool: number
  spread: number
  spreadSlots: number
  unabsorbed: number
  envelopeOver: number
  note: string | null
  isSubscription: boolean
  createdAt: string
}

export interface FixedChargeView {
  id: number
  categoryId: number
  name: string
  amount: number
  dueDate: string | null
  isSubscription: boolean
}

export interface PoolTransferView {
  id: number
  date: string
  amount: number
  note: string
}

export interface PeriodView {
  id: number
  startDate: string
  endDate: string
  today: string
  income: number
  pool: { opening: number; balance: number; lines: PoolLine[] }
  categories: CategoryView[]
  days: DayView[]
  entries: EntryView[]
  fixedCharges: FixedChargeView[]
  transfers: PoolTransferView[]
}

export interface CreateEntryRequest {
  date: string
  categoryId: number
  slotId: number | null
  inputMode: EntryInputMode
  amount: number
  usePool: boolean
  note: string | null
  subscription: { name: string; targetCategoryId: number; cycle: BillingCycle; dueDay: number | null } | null
}

export interface EntryPreview {
  entry: EntryView
  poolBefore: number
  poolAfter: number
  categoryRemainingBefore: number
  categoryRemainingAfter: number
}

// ---- assets ----
export interface CashAccountDto {
  id: number
  name: string
  balance: number
}

export interface HoldingDto {
  id: number
  symbol: string
  name: string
  shares: number
  avgCost: number
  manualPrice: number | null
}

export interface HoldingView extends HoldingDto {
  price: number | null
  priceSource: 'manual' | 'market' | 'none'
  priceDate: string | null
  marketValue: number
  cost: number
  pnl: number
}

export interface GoalDto {
  id: number
  name: string
  targetAmount: number
  targetDate: string
  scope: GoalScope
}

export interface GoalView extends GoalDto {
  current: number
  progress: number
  monthsLeft: number
  monthlyNeeded: number
}

export interface AssetsView {
  cashTotal: number
  investmentTotal: number
  costTotal: number
  cashAccounts: CashAccountDto[]
  holdings: HoldingView[]
  goals: GoalView[]
  history: { date: string; cash: number; investments: number }[]
  quotesFetchedAt: string | null
}
