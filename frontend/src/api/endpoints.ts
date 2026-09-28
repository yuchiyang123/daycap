import { api, ensureCsrf, readCookie } from './http'
import type {
  AssetAdjustmentView,
  AssetsView,
  IncomeAdjustmentKind,
  NotificationsView,
  CalendarDayDto,
  CashAccountDto,
  CreateEntryRequest,
  EntryPreview,
  GoalDto,
  HoldingDto,
  Me,
  PeriodSummary,
  PeriodView,
  SettingsDto,
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

export const getSettings = () => api<SettingsDto>('/api/settings')
export const saveSettings = (dto: SettingsDto) => api<SettingsDto>('/api/settings', { method: 'PUT', body: dto })

export const getCalendar = (from: string, to: string) =>
  api<CalendarDayDto[]>(`/api/calendar?from=${from}&to=${to}`)
export const setDayOverride = (date: string, isHoliday: boolean | null) =>
  api<void>(`/api/calendar/overrides/${date}`, { method: 'PUT', body: { isHoliday } })

export const listPeriods = () => api<PeriodSummary[]>('/api/periods')
export const getCurrentPeriod = () => api<PeriodView>('/api/periods/current')
export const deleteBeforeStart = () => api<{ deleted: number }>('/api/periods/before-start', { method: 'DELETE' })
export const getPeriod = (id: number) => api<PeriodView>(`/api/periods/${id}`)
export const rebuildPeriod = (id: number, fromDate: string | null) =>
  api<PeriodView>(`/api/periods/${id}/rebuild`, { method: 'POST', body: { fromDate } })

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
export const saveAssets = (body: { cashAccounts: CashAccountDto[]; holdings: HoldingDto[]; goals: GoalDto[] }) =>
  api<AssetsView>('/api/assets', { method: 'PUT', body })
