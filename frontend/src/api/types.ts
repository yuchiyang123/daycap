export type CategoryGroup = 'Food' | 'Clothing' | 'Housing' | 'Transport' | 'Education' | 'Leisure' | 'Savings' | 'Other'
export type BudgetMode = 'Fixed' | 'Daily' | 'Envelope'
export type BillingCycle = 'Monthly' | 'Quarterly' | 'Yearly'
export type EntryInputMode = 'Actual' | 'Overage'
export type GoalScope = 'All' | 'Cash' | 'Investments'
export type IncomeAdjustmentKind =
  | 'SickLeave'
  | 'PersonalLeave'
  | 'MenstrualLeave'
  | 'Overtime'
  | 'Bonus'
  | 'OtherDeduction'
  | 'OtherAddition'

export interface Me {
  userId: string
  userName: string | null
}

// ---- settings ----
export interface SlotDto {
  id: number
  name: string
  /** 開始時間 HH:mm；結束＝下一個時段的開始（§3.2 邊界銜接） */
  start: string
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

export type HolidayShift = 'None' | 'Before' | 'After'

export interface SettingsDto {
  /** 邏輯日起點 HH:mm（§3.1） */
  logicalDayStart: string
  monthlyIncome: number
  payday: { day: number; shift: HolidayShift }
  startDate: string | null
  categories: CategoryDto[]
}

export interface SettingsVersionSummary {
  id: number
  effectiveFrom: string
  createdAt: string
  isCorrection: boolean
  note: string | null
}

/** 設定頁看到的是最新一份（可能明天才生效）；today 是邏輯日 */
export interface SettingsView {
  settings: SettingsDto
  effectiveFrom: string
  today: string
  versions: SettingsVersionSummary[]
}

export interface CalendarDayDto {
  date: string
  isHoliday: boolean
  name: string | null
}

// ---- period ----
export interface NotStartedDto {
  startDate: string
  firstPeriodStart: string
  firstPeriodEnd: string
  daysUntilStart: number
}

export interface PeriodSummary {
  id: number
  startDate: string
  endDate: string
  income: number
}

export interface PoolLine {
  date: string
  amount: number
  kind: 'Opening' | 'Surplus' | 'Cover' | 'Unabsorbed' | 'EnvelopeOver' | 'Transfer' | 'Income' | 'Allocate' | 'Settings' | 'Lower'
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
  /** 從待定區分配進來的額度（budget 已包含） */
  allocated: number
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
  fixedItemId: number | null
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
  categoryId: number | null
}

export interface IncomeAdjustmentView {
  id: number
  kind: IncomeAdjustmentKind
  days: number | null
  hours: number | null
  amount: number
  note: string | null
  label: string
}

export interface PeriodView {
  id: number
  startDate: string
  endDate: string
  /** 邏輯日（§3.1） */
  today: string
  logicalDayStart: string
  weekdayCount: number
  holidayCount: number
  /** 實領 = 期間第一天有效的月收入 + 本期薪資調整 */
  income: number
  baseIncome: number
  incomeAdjustments: IncomeAdjustmentView[]
  incomeConfirmed: boolean
  settledAt: string | null
  pool: { opening: number; balance: number; lines: PoolLine[] }
  categories: CategoryView[]
  days: DayView[]
  entries: EntryView[]
  fixedCharges: FixedChargeView[]
  transfers: PoolTransferView[]
  reconciliations: ReconciliationView[]
}

export interface ReconciliationView {
  id: number
  date: string
  expected: number
  actual: number
  diff: number
  fromPool: number
  spread: number
  unabsorbed: number
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
  /** 用哪個帳戶付的（選填）：信用卡 = 欠款增加 */
  accountId?: number | null
}

export interface EntryPreview {
  entry: EntryView
  poolBefore: number
  poolAfter: number
  categoryRemainingBefore: number
  categoryRemainingAfter: number
}

// ---- accounts (§5, §12.1) ----
export type AccountType = 'Bank' | 'Cash' | 'EWallet' | 'CreditCard'

export interface AccountView {
  id: number
  name: string
  type: AccountType
  /** 推算值；信用卡是欠款（正數） */
  balance: number
  reconciledOn: string | null
  cardSpendThisPeriod: number
  isArchived: boolean
}

export interface AccountsView {
  accounts: AccountView[]
  netLiquid: number
  lastFullReconciliation: string | null
}

export interface AccountEdit {
  id: number
  name: string
  type: AccountType
  openingBalance: number | null
}

export type TransferKind = 'Transfer' | 'CardPayment'

export interface TransferView {
  id: number
  date: string
  kind: TransferKind
  fromAccountId: number
  fromName: string
  toAccountId: number
  toName: string
  amount: number
  note: string | null
  createdAt: string
}

export interface ReconciliationResult {
  id: number | null
  date: string
  hasBaseline: boolean
  baselineDate: string | null
  expected: number
  actual: number
  diff: number
  fromPool: number
  spread: number
  unabsorbed: number
  largeDiff: boolean
  poolBefore: number
  poolAfter: number
}

export interface ReconciliationSummary {
  id: number
  date: string
  isFull: boolean
  net: number
  note: string | null
  createdAt: string
  lines: { accountId: number; balance: number }[]
}

// ---- ledger / notifications ----
export interface AssetAdjustmentView {
  id: number
  cashAccountId: number
  accountName: string
  date: string
  amount: number
  note: string
  source: 'manual' | 'settlement'
  periodId: number | null
  createdAt: string
}

export interface NotificationDto {
  id: number
  kind: 'reminder' | 'settlement' | string
  title: string
  lines: string[]
  tone: 'warn' | 'ok'
  createdAt: string
  read: boolean
  showPopup: boolean
}

export interface NotificationsView {
  unread: number
  items: NotificationDto[]
}

// ---- assets ----
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
  cashAccounts: AccountView[]
  holdings: HoldingView[]
  goals: GoalView[]
  history: { date: string; cash: number; investments: number }[]
  quotesFetchedAt: string | null
}
