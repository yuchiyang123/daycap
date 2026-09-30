/**
 * 觸覺回饋（滑卡用）。
 * - Android：Vibration API。
 * - iPhone：Safari 不支援 Vibration API；iOS 18 起觸發系統「開關」元件（<input type="checkbox" switch>）
 *   會有系統觸覺回饋，借它來震一下。更舊的 iOS 沒有震動，不影響功能。
 * tick＝拖過門檻的輕震；confirm＝確認送出，震兩下。
 */
let lastAt = 0

/** 回傳用了哪一種方式（設定頁的「測試震動」顯示用）。同一個動作可能從兩個事件觸發，150ms 內只震一次。 */
export function haptic(kind: 'tick' | 'confirm' = 'tick'): 'vibrate' | 'ios-switch' | 'skipped' {
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
  // iPhone：只有在使用者手勢（touchend、click）裡觸發才有效，所以不能等 setTimeout 再震第二下
  iosTick()
  return 'ios-switch'
}

function iosTick() {
  try {
    const label = document.createElement('label')
    label.ariaHidden = 'true'
    label.style.display = 'none'
    const input = document.createElement('input')
    input.type = 'checkbox'
    input.setAttribute('switch', '')
    label.appendChild(input)
    document.head.appendChild(label)
    label.click()
    document.head.removeChild(label)
  } catch {
    /* 不支援就算了 */
  }
}
