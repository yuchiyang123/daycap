export class ApiError extends Error {
  status: number
  /** ProblemDetails 原文（例如 409 還沒開始時帶的 notStarted）。 */
  body: Record<string, unknown> | null
  constructor(status: number, message: string, body: Record<string, unknown> | null = null) {
    super(message)
    this.status = status
    this.body = body
  }
}

function readCookie(name: string): string | null {
  const match = document.cookie.match(new RegExp(`(?:^|; )${name}=([^;]*)`))
  return match ? decodeURIComponent(match[1]) : null
}

let csrfPromise: Promise<void> | null = null

/** Mini-SSO 的雙提交 CSRF：改資料前確定手上有 XSRF-TOKEN cookie。 */
async function ensureCsrf(force = false): Promise<void> {
  if (!force && readCookie('XSRF-TOKEN')) return
  csrfPromise ??= fetch('/api/auth/csrf', { credentials: 'include' })
    .then(() => undefined)
    .catch(() => undefined) // 本機 DevAuth 沒有 Mini-SSO，拿不到也沒關係
    .finally(() => {
      csrfPromise = null
    })
  await csrfPromise
}

let refreshPromise: Promise<boolean> | null = null

/** access token 60 分鐘過期；401 時用 refresh token 換一次再重打。 */
async function refreshSession(): Promise<boolean> {
  refreshPromise ??= (async () => {
    await ensureCsrf()
    const res = await fetch('/api/auth/refresh', {
      method: 'POST',
      credentials: 'include',
      headers: { 'X-CSRF-TOKEN': readCookie('XSRF-TOKEN') ?? '' },
    }).catch(() => null)
    return !!res?.ok
  })().finally(() => {
    refreshPromise = null
  })
  return refreshPromise
}

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE'
  body?: unknown
  /** 401 時不要導去登入頁（例如進站時的登入檢查）。 */
  allowAnonymous?: boolean
}

export async function api<T>(url: string, opts: RequestOptions = {}): Promise<T> {
  const method = opts.method ?? 'GET'
  const mutating = method !== 'GET'

  const send = async () => {
    if (mutating) await ensureCsrf()
    const headers: Record<string, string> = {}
    if (opts.body !== undefined) headers['Content-Type'] = 'application/json'
    if (mutating) headers['X-CSRF-TOKEN'] = readCookie('XSRF-TOKEN') ?? ''
    return fetch(url, {
      method,
      credentials: 'include',
      headers,
      body: opts.body === undefined ? undefined : JSON.stringify(opts.body),
    })
  }

  let res = await send()
  if (res.status === 401 && (await refreshSession())) res = await send()
  if (res.status === 403 && mutating) {
    await ensureCsrf(true)
    res = await send()
  }

  if (res.status === 401 && !opts.allowAnonymous) {
    const here = window.location.pathname + window.location.search
    if (!window.location.pathname.startsWith('/login')) {
      window.location.href = `/login?redirect=${encodeURIComponent(here)}`
    }
  }

  if (!res.ok) {
    let message = `發生錯誤（${res.status}）`
    let body: Record<string, unknown> | null = null
    try {
      body = await res.json()
      message = (body?.detail as string) || (body?.title as string) || message
    } catch {
      /* 不是 JSON */
    }
    throw new ApiError(res.status, message, body)
  }

  if (res.status === 204) return undefined as T
  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}

export { ensureCsrf, readCookie }
