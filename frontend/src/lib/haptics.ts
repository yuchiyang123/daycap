/**
 * 觸覺回饋（滑卡用）。
 * - Android：Vibration API，這裡處理。
 * - iPhone：Safari 沒有震動 API；只有「手指真的點到系統開關」才有觸覺回饋（程式自己切換開關沒用，2026-09 實機測過），
 *   所以 iPhone 的震動做在按鈕上（HapticButton：按鈕裡藏一個看不見的開關）。滑動本身在 iPhone 上不會震。
 */
let lastAt = 0

/** 同一個動作可能從兩個事件觸發，150ms 內只震一次。回傳有沒有呼叫到震動。 */
export function haptic(kind: 'tick' | 'confirm' = 'tick'): 'vibrate' | 'unsupported' | 'skipped' {
  try {
    if (typeof navigator.vibrate !== 'function') return 'unsupported'
    const now = performance.now()
    if (now - lastAt < 150) return 'skipped'
    lastAt = now
    navigator.vibrate(kind === 'tick' ? 10 : [12, 50, 18])
    return 'vibrate'
  } catch {
    return 'unsupported' // 有些瀏覽器在沒有使用者手勢時會丟錯
  }
}
