import { reactive } from 'vue'
import * as ep from '../api/endpoints'
import { ApiError } from '../api/http'
import type { Me, NotStartedDto, PeriodView } from '../api/types'

/**
 * 全站共用狀態。資料量很小（單人、一個月），不需要 Pinia：
 * 每個會改資料的 API 都直接回傳整個重算好的 PeriodView，覆蓋掉就好。
 */
export const store = reactive({
  me: null as Me | null,
  authChecked: false,
  period: null as PeriodView | null,
  /** 設了開始日期而且還沒到：不計算，顯示「還沒開始」。 */
  notStarted: null as NotStartedDto | null,
  periodError: null as string | null,
  loading: false,
})

export async function checkAuth(): Promise<boolean> {
  if (store.authChecked) return store.me !== null
  try {
    store.me = await ep.getMe()
  } catch {
    store.me = null
  } finally {
    store.authChecked = true
  }
  return store.me !== null
}

export async function loadCurrentPeriod(force = false): Promise<void> {
  if ((store.period || store.notStarted) && !force) return
  store.loading = true
  store.periodError = null
  try {
    store.period = await ep.getCurrentPeriod()
    store.notStarted = null
  } catch (e) {
    if (e instanceof ApiError && e.status === 409 && e.body?.notStarted) {
      store.period = null
      store.notStarted = e.body.notStarted as NotStartedDto
    } else {
      store.periodError = (e as Error).message
    }
  } finally {
    store.loading = false
  }
}

export function setPeriod(p: PeriodView) {
  store.period = p
}
