export type ThemeChoice = 'system' | 'light' | 'dark'

const KEY = 'daycap:theme'

/** 外觀偏好只存在這台裝置（localStorage），讀寫失敗就當「跟隨系統」。 */
export function currentTheme(): ThemeChoice {
  try {
    const v = localStorage.getItem(KEY)
    return v === 'light' || v === 'dark' ? v : 'system'
  } catch {
    return 'system'
  }
}

export function applyTheme(t: ThemeChoice = currentTheme()) {
  const root = document.documentElement
  if (t === 'system') root.removeAttribute('data-theme')
  else root.setAttribute('data-theme', t)
  try {
    if (t === 'system') localStorage.removeItem(KEY)
    else localStorage.setItem(KEY, t)
  } catch {
    /* 私密瀏覽等情況 */
  }
  const dark = t === 'dark' || (t === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches)
  document.querySelector('meta[name="theme-color"]')?.setAttribute('content', dark ? '#15181d' : '#f3f4f6')
}
