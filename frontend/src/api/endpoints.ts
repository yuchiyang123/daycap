import { api, ensureCsrf, readCookie } from './http'
import type {
  AccountEdit,
  AccountsView,
  AssetAdjustmentView,
  ReconciliationResult,
  ReconciliationSummary,
  TransferKind,
  TransferView,
  AssetsView,
  IncomeAdjustmentKind,
  NotificationsView,
  CalendarDayDto,
  CreateEntryRequest,
  EntryPreview,
  GoalDto,
  HoldingDto,
  Me,
  PeriodSummary,
  PeriodView,
  SettingsDto,
  SettingsView,
  SettingsEstimate,
  MonthEndReport,
  JarView,
  SaveJarRequest,
  OnboardingState,
  ApplyTemplateRequest,
  TemplatePreview,
  ShortfallChoice,
  AllocationCell,
  AllocationResult,
} from './types'

export const getMe = () => api<Me>('/api/me', { allowAnonymous: true })

export async function login(userName: string, password: string): Promise<void> {
  await ensureCsrf(true)
  await api('/api/auth', { method: 'POST', body: { userName, password }, allowAnonymous: true })
}

export async function logout(): Promise<void> {
  await ensureCsrf()
  await fetch('/api/auth/logout', {
    method: 'POST',
    credentials: 'include',
    headers: { 'X-CSRF-TOKEN': readCookie('XSRF-TOKEN') ?? '' },
  }).catch(() => undefined)
}

export const externalLoginUrl = (provider: 'google' | 'github') => `/api/auth/external/${provider}/login`

export const getSettings = () => api<SettingsView>('/api/settings')
/** 新增一筆設定版本：一般從明天起生效；correctionFrom 有值是更正過去（必須附原因） */
export const saveSettings = (settings: SettingsDto, correctionFrom: string | null = null, correctionNote: string | null = null) =>
  api<SettingsView>('/api/settings', { method: 'PUT', body: { settings, correctionFrom, correctionNote } })

export const getCalendar = (from: string, to: string) =>
  api<CalendarDayDto[]>(`/api/calendar?from=${from}&to=${to}`)
export const setDayOverride = (date: string, isHoliday: boolean | null) =>
  api<void>(`/api/calendar/overrides/${date}`, { method: 'PUT', body: { isHoliday } })

export const listPeriods = () => api<PeriodSummary[]>('/api/periods')
export const getCurrentPeriod = () => api<PeriodView>('/api/periods/current')
export const deleteBeforeStart = () => api<{ deleted: number }>('/api/periods/before-start', { method: 'DELETE' })
export const getPeriod = (id: number) => api<PeriodView>(`/api/periods/${id}`)
/** 手動覆蓋本期結束後的實際入帳日（＝下一期第一天） */
export const setNextPayday = (id: number, date: string) =>
  api<PeriodView>(`/api/periods/${id}/next-payday`, { method: 'PUT', body: { date } })

export const createEntry = (periodId: number, req: CreateEntryRequest) =>
  api<PeriodView>(`/api/periods/${periodId}/entries`, { method: 'POST', body: req })
export const previewEntry = (periodId: number, req: CreateEntryRequest) =>
  api<EntryPreview>(`/api/periods/${periodId}/entries/preview`, { method: 'POST', body: req })
export const deleteEntry = (periodId: number, entryId: number) =>
  api<PeriodView>(`/api/periods/${periodId}/entries/${entryId}`, { method: 'DELETE' })

export const addIncomeAdjustment = (
  periodId: number,
  body: { kind: IncomeAdjustmentKind; days: number | null; hours: number | null; amount: number; note: string | null },
) => api<PeriodView>(`/api/periods/${periodId}/income-adjustments`, { method: 'POST', body })
export const deleteIncomeAdjustment = (periodId: number, id: number) =>
  api<PeriodView>(`/api/periods/${periodId}/income-adjustments/${id}`, { method: 'DELETE' })
export const confirmIncome = (periodId: number) =>
  api<PeriodView>(`/api/periods/${periodId}/income-confirm`, { method: 'POST' })
export const allocatePool = (periodId: number, body: { mode: 'single' | 'proportional'; categoryId: number | null; amount: number }) =>
  api<PeriodView>(`/api/periods/${periodId}/allocate`, { method: 'POST', body })

export const listAssetAdjustments = (from?: string, to?: string) =>
  api<AssetAdjustmentView[]>(`/api/assets/adjustments${from ? `?from=${from}&to=${to}` : ''}`)
export const addAssetAdjustment = (body: { cashAccountId: number; date: string; amount: number; note: string | null }) =>
  api<AssetAdjustmentView>('/api/assets/adjustments', { method: 'POST', body })
export const deleteAssetAdjustment = (id: number) => api<void>(`/api/assets/adjustments/${id}`, { method: 'DELETE' })

export const getNotifications = () => api<NotificationsView>('/api/notifications')
export const markPopupShown = (id: number) => api<void>(`/api/notifications/${id}/popup-shown`, { method: 'POST' })
export const readAllNotifications = () => api<void>('/api/notifications/read-all', { method: 'POST' })

export const addTransfer = (periodId: number, date: string, amount: number, note: string) =>
  api<PeriodView>(`/api/periods/${periodId}/transfers`, { method: 'POST', body: { date, amount, note } })
export const deleteTransfer = (periodId: number, transferId: number) =>
  api<PeriodView>(`/api/periods/${periodId}/transfers/${transferId}`, { method: 'DELETE' })

export const getAssets = (refresh = false) => api<AssetsView>(`/api/assets${refresh ? '?refresh=true' : ''}`)
export const saveAssets = (body: { cashAccounts: AccountEdit[]; holdings: HoldingDto[]; goals: GoalDto[] }) =>
  api<AssetsView>('/api/assets', { method: 'PUT', body })

// ---- 帳戶、轉帳、對帳（§5、§12.1）----
export const getAccounts = () => api<AccountsView>('/api/accounts')
export const listTransfers = (from?: string, to?: string) =>
  api<TransferView[]>(`/api/accounts/transfers${from ? `?from=${from}&to=${to}` : ''}`)
export const addTransfer2 = (body: { date: string; kind: TransferKind; fromAccountId: number; toAccountId: number; amount: number; note: string | null }) =>
  api<TransferView>('/api/accounts/transfers', { method: 'POST', body })
export const deleteTransfer2 = (id: number) => api<void>(`/api/accounts/transfers/${id}`, { method: 'DELETE' })

type ReconcileBody = { date: string; lines: { accountId: number; balance: number }[]; usePool: boolean; note: string | null }
export const listReconciliations = () => api<ReconciliationSummary[]>('/api/accounts/reconciliations')
export const previewReconciliation = (body: ReconcileBody) =>
  api<ReconciliationResult>('/api/accounts/reconciliations/preview', { method: 'POST', body })
export const createReconciliation = (body: ReconcileBody) =>
  api<ReconciliationResult>('/api/accounts/reconciliations', { method: 'POST', body })
export const deleteReconciliation = (id: number) => api<void>(`/api/accounts/reconciliations/${id}`, { method: 'DELETE' })

// ---- 設定即時合計、分配器預覽（§7、§8.2）----
export const estimateSettings = (draft: SettingsDto) => api<SettingsEstimate>('/api/settings/estimate', { method: 'POST', body: draft })
/** 分配器由你實作（§8.1）；還沒實作時後端回 501，這裡會丟出含「分配器尚未實作」訊息的錯誤 */
export const allocatePreview = (budget: number, cells: AllocationCell[], roundingUnit: number, releasedShareReceiverKey: string | null = null) =>
  api<AllocationResult>('/api/settings/allocate-preview', { method: 'POST', body: { budget, cells, roundingUnit, releasedShareReceiverKey } })

// ---- 全天例外（§9.3）、月結（§12.2）----
export const setDay = (periodId: number, date: string, mode: 'zero' | 'planned') =>
  api<PeriodView>(`/api/periods/${periodId}/days/${date}/${mode}`, { method: 'POST' })
export const getMonthEnd = (periodId: number) => api<MonthEndReport>(`/api/month-end/${periodId}`)
export const closeMonth = (
  periodId: number,
  body: { decision: ShortfallChoice; accountId: number | null; slotChanges: { categoryId: number; slotId: number; workdayAmount: number; holidayAmount: number }[] },
) => api<MonthEndReport>(`/api/month-end/${periodId}`, { method: 'POST', body })

// ---- onboarding（§20）----
export const getOnboarding = () => api<OnboardingState>('/api/onboarding')
export const previewTemplate = (body: ApplyTemplateRequest) => api<TemplatePreview>('/api/onboarding/preview', { method: 'POST', body })
export const applyTemplate = (body: ApplyTemplateRequest) => api<TemplatePreview>('/api/onboarding/apply', { method: 'POST', body })
export const skipOnboarding = () => api<void>('/api/onboarding/skip', { method: 'POST' })
export const markTipSeen = (key: string) => api<string[]>(`/api/me/tips/${key}`, { method: 'POST' })

// ---- 罐子（§11.2）----
export const getJars = () => api<JarView[]>('/api/jars')
export const createJar = (body: SaveJarRequest) => api<JarView[]>('/api/jars', { method: 'POST', body })
export const updateJar = (id: number, body: SaveJarRequest) => api<JarView[]>(`/api/jars/${id}`, { method: 'PUT', body })
/** amount > 0 從待分配池存進罐子；< 0 拿回池子 */
export const moveJar = (id: number, amount: number) => api<JarView[]>(`/api/jars/${id}/move`, { method: 'POST', body: { amount } })
export const closeJar = (id: number) => api<JarView[]>(`/api/jars/${id}/close`, { method: 'POST' })
