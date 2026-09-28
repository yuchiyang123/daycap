import { onBeforeUnmount, onMounted, ref, type Ref } from 'vue'

/** 量容器寬度，讓 SVG 圖表用實際像素畫（線寬、字級不會跟著縮放變形）。 */
export function useWidth(el: Ref<HTMLElement | null>, fallback = 320) {
  const width = ref(fallback)
  let ro: ResizeObserver | null = null
  onMounted(() => {
    if (!el.value) return
    width.value = el.value.clientWidth || fallback
    ro = new ResizeObserver((entries) => {
      const w = entries[0]?.contentRect.width
      if (w) width.value = Math.floor(w)
    })
    ro.observe(el.value)
  })
  onBeforeUnmount(() => ro?.disconnect())
  return width
}
