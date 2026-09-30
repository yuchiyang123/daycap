export type ThemeChoice = 'system' | 'light' | 'dark'
/** 風格：original＝原本的外觀（預設）；其他在 style.css 最後的「可切換的風格」。 */
export type DesignChoice = 'original' | 'mono' | 'swiss'

const KEY = 'daycap:theme'
const DESIGN_KEY = 'daycap:design'

export const designs: { v: DesignChoice; label: string }[] = [
  { v: 'original', label: '原本' },
  { v: 'mono', label: '終端機' },
  { v: 'swiss', label: '瑞士平面' },
]

/** 各風格的頁面底色：瀏覽器 / 手機狀態列的顏色要跟著換 */
const PAGE_COLOR: Record<DesignChoice, { light: string; dark: string }> = {
  original: { light: '#f4f3ef', dark: '#0d0d0d' },
  mono: { light: '#f3f6f1', dark: '#0a0d0b' },
  swiss: { light: '#ffffff', dark: '#000000' },
}

/** 外觀偏好只存在這台裝置（localStorage），讀寫失敗就當「跟隨系統」。 */
export function currentTheme(): ThemeChoice {
  try {
    const v = localStorage.getItem(KEY)
    return v === 'light' || v === 'dark' ? v : 'system'
  } catch {
    return 'system'
  }
}

export function currentDesign(): DesignChoice {
  try {
    const v = localStorage.getItem(DESIGN_KEY)
    return v === 'mono' || v === 'swiss' ? v : 'original'
  } catch {
    return 'original'
  }
}

function syncThemeColor() {
  const t = currentTheme()
  const dark = t === 'dark' || (t === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches)
  const color = PAGE_COLOR[currentDesign()][dark ? 'dark' : 'light']
  document.querySelector('meta[name="theme-color"]')?.setAttribute('content', color)
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
  syncThemeColor()
}

export function applyDesign(d: DesignChoice = currentDesign()) {
  const root = document.documentElement
  if (d === 'original') root.removeAttribute('data-design')
  else root.setAttribute('data-design', d)
  try {
    if (d === 'original') localStorage.removeItem(DESIGN_KEY)
    else localStorage.setItem(DESIGN_KEY, d)
  } catch {
    /* 私密瀏覽等情況 */
  }
  syncThemeColor()
}
