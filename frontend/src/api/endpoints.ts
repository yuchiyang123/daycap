import { api, ensureCsrf, readCookie } from './http'
import type {
  AssetsView,
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
export const getPeriod = (id: number) => api<PeriodView>(`/api/periods/${id}`)
export const rebuildPeriod = (id: number, fromDate: string | null) =>
  api<PeriodView>(`/api/periods/${id}/rebuild`, { method: 'POST', body: { fromDate } })

export const createEntry = (periodId: number, req: CreateEntryRequest) =>
  api<PeriodView>(`/api/periods/${periodId}/entries`, { method: 'POST', body: req })
export const previewEntry = (periodId: number, req: CreateEntryRequest) =>
  api<EntryPreview>(`/api/periods/${periodId}/entries/preview`, { method: 'POST', body: req })
export const deleteEntry = (periodId: number, entryId: number) =>
  api<PeriodView>(`/api/periods/${periodId}/entries/${entryId}`, { method: 'DELETE' })

export const addTransfer = (periodId: number, date: string, amount: number, note: string) =>
  api<PeriodView>(`/api/periods/${periodId}/transfers`, { method: 'POST', body: { date, amount, note } })
export const deleteTransfer = (periodId: number, transferId: number) =>
  api<PeriodView>(`/api/periods/${periodId}/transfers/${transferId}`, { method: 'DELETE' })

export const getAssets = (refresh = false) => api<AssetsView>(`/api/assets${refresh ? '?refresh=true' : ''}`)
export const saveAssets = (body: { cashAccounts: CashAccountDto[]; holdings: HoldingDto[]; goals: GoalDto[] }) =>
  api<AssetsView>('/api/assets', { method: 'PUT', body })
