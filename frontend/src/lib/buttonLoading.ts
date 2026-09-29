/**
 * 按鈕載入動畫（全站）：按下按鈕後 0.4 秒內開始的 API 請求，都算是那顆按鈕造成的——
 * 請求進行中按鈕加上 .is-loading（轉圈、不能再按，避免連點送兩次），全部結束才拿掉。
 * 動畫至少顯示 0.35 秒，太快回來也看得到有在動。不用每個元件自己處理。
 */
const CLICK_WINDOW_MS = 400
const MIN_VISIBLE_MS = 350

let lastButton: HTMLElement | null = null
let lastClickAt = 0

export function initButtonLoading() {
  document.addEventListener(
    'click',
    (e) => {
      const target = e.target as Element | null
      const button = target?.closest?.('button, .btn') as HTMLElement | null
      if (!button) return
      lastButton = button
      lastClickAt = performance.now()
    },
    true,
  )
}

export function trackRequest<T>(request: Promise<T>): Promise<T> {
  const button = lastButton && lastButton.isConnected && performance.now() - lastClickAt < CLICK_WINDOW_MS ? lastButton : null
  if (!button) return request

  const pending = Number(button.dataset.pending ?? 0) + 1
  button.dataset.pending = String(pending)
  if (pending === 1) {
    button.dataset.loadingSince = String(performance.now())
    button.classList.add('is-loading')
    button.setAttribute('aria-busy', 'true')
  }

  const done = () => {
    const left = Number(button.dataset.pending ?? 1) - 1
    if (left > 0) {
      button.dataset.pending = String(left)
      return
    }
    const shown = performance.now() - Number(button.dataset.loadingSince ?? 0)
    setTimeout(
      () => {
        if (Number(button.dataset.pending ?? 0) > 1) return // 等待期間又開始了新的請求
        delete button.dataset.pending
        delete button.dataset.loadingSince
        button.classList.remove('is-loading')
        button.removeAttribute('aria-busy')
      },
      Math.max(0, MIN_VISIBLE_MS - shown),
    )
  }
  request.then(done, done)
  return request
}
