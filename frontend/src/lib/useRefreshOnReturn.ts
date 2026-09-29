import { onActivated } from 'vue'

/**
 * 主要分頁留在記憶體（KeepAlive）：切回來時先顯示上次的資料，再背景抓一次。
 * 第一次顯示時 onMounted 已經抓過，所以跳過。
 */
export function useRefreshOnReturn(load: () => unknown) {
  let first = true
  onActivated(() => {
    if (first) {
      first = false
      return
    }
    void Promise.resolve(load()).catch(() => undefined)
  })
}
