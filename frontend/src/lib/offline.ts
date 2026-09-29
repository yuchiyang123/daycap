import { reactive } from 'vue'
import { createEntry } from '../api/endpoints'
import { ApiError } from '../api/http'
import type { CreateEntryRequest } from '../api/types'

/**
 * 離線使用（§21.4）：沒網路時滑卡確認先排隊（存在這台裝置），連線後依序送出。
 * 衝突規則（規格未定，先保守）：送到伺服器的順序就是生效順序；同一時段兩台都回報，後送到的取代先送到的（舊的仍留紀錄）。
 * 伺服器拒絕的（例如那期已經月結）不會默默丟掉，列在「沒送出」讓使用者看到。
 * 離線資料目前不加密（規格未定）；App 鎖只擋畫面。
 */
interface Queued {
  id: string
  periodId: number
  body: CreateEntryRequest
  label: string
  queuedAt: number
}
interface Failed extends Queued {
  message: string
}

const KEY = 'daycap.offline-queue'
const FAILED_KEY = 'daycap.offline-failed'

function read<T>(key: string): T[] {
  try {
    return JSON.parse(localStorage.getItem(key) ?? '[]') as T[]
  } catch {
    return []
  }
}
function write<T>(key: string, items: T[]) {
  try {
    localStorage.setItem(key, JSON.stringify(items))
  } catch {
    /* 存不了：只好當場失敗 */
  }
}

export const offlineState = reactive({
  online: navigator.onLine,
  pending: read<Queued>(KEY).length,
  failed: read<Failed>(FAILED_KEY),
  flushing: false,
})

/** fetch 本身失敗（沒網路、DNS、伺服器沒回應）才算離線；伺服器有回應的錯誤照常丟出去。 */
export const isNetworkError = (e: unknown) => !(e instanceof ApiError) && (e instanceof TypeError || !navigator.onLine)

export function enqueue(periodId: number, body: CreateEntryRequest, label: string) {
  const items = read<Queued>(KEY)
  items.push({ id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`, periodId, body, label, queuedAt: Date.now() })
  write(KEY, items)
  offlineState.pending = items.length
}

/** 依序送出；網路又斷了就停，下次再送。回傳送出成功的筆數。 */
export async function flushQueue(): Promise<number> {
  if (offlineState.flushing) return 0
  offlineState.flushing = true
  let sent = 0
  try {
    let items = read<Queued>(KEY)
    while (items.length) {
      const item = items[0]
      try {
        await createEntry(item.periodId, item.body)
        sent++
      } catch (e) {
        if (isNetworkError(e)) break
        const failed = [...read<Failed>(FAILED_KEY), { ...item, message: (e as Error).message }]
        write(FAILED_KEY, failed)
        offlineState.failed = failed
      }
      items = items.slice(1)
      write(KEY, items)
      offlineState.pending = items.length
    }
  } finally {
    offlineState.flushing = false
  }
  return sent
}

export function dismissFailed() {
  write(FAILED_KEY, [])
  offlineState.failed = []
}

export function initOffline(onFlushed: () => void) {
  const flush = async () => {
    offlineState.online = navigator.onLine
    if (navigator.onLine && (await flushQueue()) > 0) onFlushed()
  }
  window.addEventListener('online', flush)
  window.addEventListener('offline', () => (offlineState.online = false))
  void flush()
}
