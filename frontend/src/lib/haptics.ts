/**
 * 觸覺回饋（滑卡用）。
 * - Android：Vibration API。
 * - iPhone：Safari 沒有震動 API；借系統「開關」元件（<input type="checkbox" switch>）被切換時的觸覺回饋。
 *   只在使用者手勢（touchend、click）裡觸發才有效。寫法有好幾種，哪一種在哪一版 iOS 有效不一定，
 *   所以留了幾種方式，設定頁可以逐一測試；測到有效的那種記在這台裝置，之後滑卡就用它。
 */
export type IosMethod = 'label-hidden' | 'label-offscreen' | 'input-click'

const KEY = 'daycap.haptic-method'
let lastAt = 0

function preferredIosMethod(): IosMethod {
  try {
    const v = localStorage.getItem(KEY)
    if (v === 'label-hidden' || v === 'label-offscreen' || v === 'input-click') return v
  } catch {
    /* 讀不到就用預設 */
  }
  return 'label-offscreen'
}

export function rememberIosMethod(m: IosMethod) {
  try {
    localStorage.setItem(KEY, m)
  } catch {
    /* 存不了就算了 */
  }
}

/** 回傳用了哪一種方式。同一個動作可能從兩個事件觸發，Android 150ms 內只震一次。 */
export function haptic(kind: 'tick' | 'confirm' = 'tick'): 'vibrate' | IosMethod | 'skipped' {
  try {
    if (typeof navigator.vibrate === 'function') {
      // 只有真的會震的 Android 才擋重複；iPhone 在非手勢事件裡呼叫本來就沒效果，不能佔掉 touchend 那次
      const now = performance.now()
      if (now - lastAt < 150) return 'skipped'
      lastAt = now
      navigator.vibrate(kind === 'tick' ? 10 : [12, 50, 18])
      return 'vibrate'
    }
  } catch {
    /* 有些瀏覽器在沒有使用者手勢時會丟錯 */
  }
  const m = preferredIosMethod()
  iosTick(m)
  return m
}

export function iosTick(method: IosMethod) {
  try {
    const input = document.createElement('input')
    input.type = 'checkbox'
    input.setAttribute('switch', '')
    input.tabIndex = -1
    if (method === 'input-click') {
      // 放在畫面外、可見的開關，直接切換它
      input.style.cssText = 'position:fixed;left:-100px;top:0;opacity:0.01;pointer-events:none'
      document.body.appendChild(input)
      input.click()
      setTimeout(() => input.remove(), 0)
      return
    }
    const label = document.createElement('label')
    label.ariaHidden = 'true'
    label.appendChild(input)
    if (method === 'label-hidden') {
      // 最早流傳的寫法：display:none 放在 head
      label.style.display = 'none'
      document.head.appendChild(label)
      label.click()
      label.remove()
      return
    }
    // 放在 body、畫面外但不是 display:none（有些版本對隱藏元素不給回饋）
    label.style.cssText = 'position:fixed;left:-100px;top:0;opacity:0.01;pointer-events:none'
    document.body.appendChild(label)
    label.click()
    setTimeout(() => label.remove(), 0)
  } catch {
    /* 不支援就算了 */
  }
}
