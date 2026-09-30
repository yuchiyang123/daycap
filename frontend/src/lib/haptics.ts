/**
 * 觸覺回饋（滑卡用）。
 * - Android：Vibration API。
 * - iPhone：Safari 不支援 Vibration API；iOS 18 起觸發系統「開關」元件（<input type="checkbox" switch>）
 *   會有系統觸覺回饋，借它來震一下。更舊的 iOS 沒有震動，不影響功能。
 * tick＝拖過門檻的輕震；confirm＝確認送出，震兩下。
 */
export function haptic(kind: 'tick' | 'confirm' = 'tick') {
  try {
    if (typeof navigator.vibrate === 'function') {
      navigator.vibrate(kind === 'tick' ? 10 : [12, 50, 18])
      return
    }
  } catch {
    /* 有些瀏覽器在沒有使用者手勢時會丟錯 */
  }
  iosTick()
  if (kind === 'confirm') setTimeout(iosTick, 80)
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
