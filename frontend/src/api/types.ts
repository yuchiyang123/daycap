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
  /** 走過新手引導（§20.9）；舊使用者視為已完成 */
  onboarded: boolean
  /** 看過的一次性提示 */
  seenTips: string[]
  /** 帳戶只記儲蓄：對帳只更新餘額，不算差額 */
  savingsOnlyAccounts?: boolean
}

// ---- onboarding（§20）----
export interface TemplateBlockDto {
  key: string
  name: string
  percent: number
  defaultChecked: boolean
  hint: string | null
}
export interface TemplateDto {
  code: string
  version: number
  name: string
  blocks: TemplateBlockDto[]
  fixedItemNames: string[]
}
export interface OnboardingState {
  needed: boolean
  appliedTemplate: string | null
  templates: TemplateDto[]
}
export interface BlockChoice {
  key: string
  checked: boolean
  amount: number | null
}
export interface ApplyTemplateRequest {
  templateCode: string
  income: number
  paydayDay: number
  blocks: BlockChoice[]
  savingsMinPercent?: number | null
}
export interface TemplateBlockPreview {
  key: string
  name: string
  checked: boolean
  locked: boolean
  templatePercent: number
  baseline: number
  amount: number
  delta: number
  actualPercent: number
  hint: string | null
}
export interface TemplatePreview {
  code: string
  version: number
  name: string
  income: number
  allocatorReady: boolean
  blocks: TemplateBlockPreview[]
  unallocated: number
  meals: {
    periodStart: string
    periodEnd: string
    weekdays: number
    holidays: number
    budget: number
    perDayAverage: number
    slots: { name: string; workday: number; holiday: number }[] | null
    problem: string | null
  } | null
  errors: string[]
  warnings: string[]
}

// ---- settings ----
export interface SlotDto {
  id: number
  name: string
  /** 開始時間 HH:mm；結束＝下一個時段的開始（§3.2 邊界銜接） */
  start: string
  workdayAmount: number
  holidayAmount: number
  /** §8.2 自動分配：時段權重、假日格權重（空 = 權重 × 假日倍率）、各格鎖定單價、各格底線 */
  weight?: number | null
  holidayWeight?: number | null
  workdayLock?: number | null
  holidayLock?: number | null
  workdayFloor?: number | null
  holidayFloor?: number | null
}

export interface MealAuto {
  enabled: boolean
  holidayMultiplier: number
  roundingUnit: number
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
  /** 自動執行：從這個帳戶扣款，轉到 toAccountId，或定期定額買 holdingId（兩者擇一） */
  fromAccountId?: number | null
  toAccountId?: number | null
  holdingId?: number | null
  /** 定期定額：錢先從外部（沒登記的帳戶）存進扣款帳戶再扣 */
  fundedExternally?: boolean | null
}

export interface CategoryDto {
  id: number
  name: string
  group: CategoryGroup
  mode: BudgetMode
  percent: number
  slots: SlotDto[]
  fixedItems: FixedItemDto[]
  /** §7：沒勾「用 % 計算」= 固定金額 amount */
  usePercent?: boolean
  amount?: number | null
  floor?: number | null
  auto?: MealAuto | null
  /** 類別細項（§13） */
  subItems?: SubItemDoc[] | null
}

export type HolidayShift = 'None' | 'Before' | 'After'
export type PercentBase = 'Income' | 'AfterFixed'
export type IncomeKind = 'Fixed' | 'Variable'

export interface CategoryEstimate {
  index: number
  budget: number
  weekdayTotal: number
  holidayTotal: number
  scheduled: number
  over: number
}

/** 設定頁即時合計（§7、§8.2），以新設定生效那一期計算 */
export interface SettingsEstimate {
  periodStart: string
  periodEnd: string
  weekdays: number
  holidays: number
  income: number
  fixedTotal: number
  percentBaseAmount: number
  percentTotal: number
  unallocated: number
  categories: CategoryEstimate[]
  errors: string[]
}

export interface AllocationCell {
  key: string
  days: number
  weight: number
  lockedUnitAmount: number | null
  floor: number | null
  tier: number
}

export interface AllocationResult {
  unitAmount: Record<string, number>
  leftover: number
  errors: string[]
  warnings: string[]
}

export interface SettingsDto {
  /** 邏輯日起點 HH:mm（§3.1） */
  logicalDayStart: string
  monthlyIncome: number
  payday: { day: number; shift: HolidayShift }
  startDate: string | null
  categories: CategoryDto[]
  percentBase: PercentBase
  incomeKind: IncomeKind
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
  /** 從待分配池分配進來的額度（budget 已包含） */
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
  /** 時段開始時間 HH:mm（邏輯日內） */
  start?: string | null
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
  /** 旅遊中（§17）：這天的每日時段暫停 */
  trip?: string | null
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
  /** 攤到幾天、平均每天少多少（§10.1） */
  spreadDays: number
  spreadPerDay: number
  /** 從罐子付的部分（§11.2），不算進預算 */
  jarCovered?: number
  jarId?: number | null
  subItem?: string | null
  currency?: string | null
  foreignAmount?: number | null
}

export type ShortfallChoice = 'Pool' | 'NextPeriod' | 'Split' | 'Savings'

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
  jarId?: number | null
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
  /** 給使用者看的提醒（例如自動分配沒執行） */
  warnings: string[]
  /** 月結（§12.2）：這期已月結；上一期還沒月結時是它的 Id */
  closed: boolean
  previousPeriodNeedsClosing: number | null
  /** 類別細項彙總（§13） */
  subItems?: SubItemView[] | null
}

export interface MonthEndReport {
  periodId: number
  startDate: string
  endDate: string
  closed: boolean
  canClose: boolean
  cannotCloseReason: string | null
  needsReconciliation: boolean
  result: number
  previousResult: number | null
  topOverspends: { label: string; days: number; total: number }[]
  unexplained: number
  slots: {
    categoryId: number
    categoryName: string
    slotId: number
    slotName: string
    workdayAmount: number
    holidayAmount: number
    reportedDays: number
    avgActual: number
    avgPlanned: number
  }[]
  goals: { name: string; current: number; target: number; progress: number; targetDate: string; estimatedDate: string | null }[]
  summary: { closedAt: string; result: number; decision: ShortfallChoice; carryAmount: number; message: string } | null
  subItemOvers?: SubItemView[] | null
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
  /** 超過護欄下限、攤不完的部分怎麼處理（§10.2），預設待分配池 */
  guardrail?: ShortfallChoice | null
  guardrailAccountId?: number | null
  /** 從罐子付（§11.2）：只有額外花費可以 */
  jarId?: number | null
  /** 類別細項（§13） */
  subItem?: string | null
  /** 分帳（§14） */
  split?: SplitRequest | null
  /** 外幣（§17）：有填就用「原金額 × 匯率」換成台幣 */
  currency?: string | null
  foreignAmount?: number | null
  fxRate?: number | null
  /** 發生當地的時區，例如 Asia/Tokyo */
  timeZoneId?: string | null
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
  /** 定期定額說明（設定在固定支出），例如「定期定額：每月 6 號 9,000，從 玉山 扣款」 */
  dca?: string | null
  lastPurchase?: string | null
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

// ---- 罐子（§11.2）----
export type JarKind = 'Reservation' | 'Annual' | 'Goal'
export interface JarView {
  id: number
  kind: JarKind
  name: string
  targetAmount: number
  balance: number
  need: number
  dueDate: string | null
  daysLeft: number | null
  monthlyAmount: number | null
  autoSurplusPercent: number | null
  fixedItemId: number | null
  closed: boolean
}
export interface SaveJarRequest {
  kind: JarKind
  name: string
  targetAmount: number
  dueDate: string | null
  monthlyAmount?: number | null
  autoSurplusPercent?: number | null
  fixedItemId?: number | null
}

// ---- 類別細項（§13）----
export interface SubItemDoc {
  name: string
  cap: number | null
}
export interface SubItemView {
  categoryId: number
  name: string
  cap: number | null
  spent: number
  over: number
}

// ---- 應收應付（§14）----
export type SplitKind = 'IPaid' | 'TheyPaid'
export interface SplitRequest {
  kind: SplitKind
  counterparty: string
  total: number | null
}
export type DebtKind = 'Receivable' | 'Payable'
export interface DebtView {
  id: number
  kind: DebtKind
  counterparty: string
  amount: number
  settled: number
  outstanding: number
  date: string
  note: string | null
  accountId: number | null
  sourceEntryId: number | null
}
export interface DebtsView {
  debts: DebtView[]
  receivableTotal: number
  payableTotal: number
  people: { counterparty: string; receivable: number; payable: number; net: number }[]
}

// ---- 分期（§15）----
export type InstallmentMode = 'Simple' | 'Detailed'
export type PrepayMode = 'ReduceAmount' | 'ReduceTerm'
export interface CreateInstallmentRequest {
  name: string
  categoryId: number
  mode: InstallmentMode
  periods: number
  firstDueDate: string
  monthlyAmount?: number | null
  principal?: number | null
  annualRatePercent?: number | null
  fee?: number | null
  note?: string | null
}
export interface InstallmentView {
  id: number
  name: string
  categoryId: number
  mode: InstallmentMode
  monthlyAmount: number | null
  periods: number
  principal: number | null
  annualRatePercent: number | null
  fee: number | null
  firstDueDate: string
  note: string | null
  totalPayments: number
  paidPayments: number
  remaining: number
  principalRemaining: number
  nextDueDate: string | null
  nextPayment: number | null
  costTotal: number
  schedule: { index: number; dueDate: string; payment: number; principal: number; interest: number; fee: number; balanceAfter: number }[]
  prepayments: { id: number; date: string; amount: number; mode: PrepayMode; accountId: number | null }[]
}

// ---- 旅遊（§17）與收入中斷（§18）----
export interface TripView {
  id: number
  name: string
  startDate: string
  endDate: string
  budget: number
  currency: string | null
  fxRate: number | null
  jarId: number
  remaining: number
  jarClosed: boolean
  status: 'upcoming' | 'active' | 'ended'
}
export interface CreateTripRequest {
  name: string
  startDate: string
  endDate: string
  budget: number
  currency: string | null
  fxRate: number | null
}
export interface RunwayView {
  usableAssets: number
  avgDailySpend: number
  basisDays: number
  from: string
  to: string
  runwayDays: number | null
}
