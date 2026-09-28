import { ref, watch, type Ref } from 'vue'
import { previewEntry } from '../api/endpoints'
import type { CreateEntryRequest, EntryPreview } from '../api/types'

/** 輸入變動 250ms 後向後端試算一次；請求有先後時只採用最新那次的結果。 */
export function usePreview(periodId: () => number, request: Ref<CreateEntryRequest | null>) {
  const preview = ref<EntryPreview | null>(null)
  const loading = ref(false)
  let timer: ReturnType<typeof setTimeout> | undefined
  let seq = 0

  watch(
    request,
    (req) => {
      clearTimeout(timer)
      if (!req) {
        preview.value = null
        return
      }
      timer = setTimeout(async () => {
        const mine = ++seq
        loading.value = true
        try {
          const res = await previewEntry(periodId(), req)
          if (mine === seq) preview.value = res
        } catch {
          if (mine === seq) preview.value = null
        } finally {
          if (mine === seq) loading.value = false
        }
      }, 250)
    },
    { deep: true, immediate: true },
  )

  return { preview, loading }
}
