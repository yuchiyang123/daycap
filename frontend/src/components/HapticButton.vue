<script setup lang="ts">
import { ref } from 'vue'

/**
 * 會震的按鈕（iPhone）：iOS 只在「手指真的點到系統開關」時給觸覺回饋，程式自己切換開關沒有用。
 * 所以按鈕本身是一個 label，裡面藏一個看不見的 <input type="checkbox" switch>：
 * 手指點按鈕＝瀏覽器替你切換那個開關＝有觸覺回饋。用開關的 change 事件觸發，一次點擊只會觸發一次。
 * Android 另外用 Vibration API（呼叫端自己震）。
 */
const props = defineProps<{ disabled?: boolean }>()
const emit = defineEmits<{ press: [] }>()
const input = ref<HTMLInputElement | null>(null)

function onKey(e: KeyboardEvent) {
  if (props.disabled) return
  if (e.key === 'Enter' || e.key === ' ') {
    e.preventDefault()
    input.value?.click()
  }
}
</script>

<template>
  <label class="btn hbtn" role="button" :tabindex="disabled ? -1 : 0" :aria-disabled="disabled || undefined" @keydown="onKey">
    <input ref="input" type="checkbox" switch class="hidden-switch" tabindex="-1" aria-hidden="true" :disabled="disabled" @change="emit('press')" />
    <slot />
  </label>
</template>

<style scoped>
.hbtn {
  position: relative;
  user-select: none;
  -webkit-user-select: none;
}
.hbtn[aria-disabled] {
  opacity: 0.45;
  cursor: default;
  pointer-events: none;
}
.hidden-switch {
  position: absolute;
  width: 1px;
  height: 1px;
  opacity: 0;
  pointer-events: none;
  margin: 0;
}
</style>
