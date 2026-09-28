<script setup lang="ts">
import { computed } from 'vue'

/**
 * 用量條：填色代表嚴重程度（正常 → 接近 → 超過），而且一定搭配文字標籤，不只靠顏色。
 * 底軌是同色系淺一階，整條都讀得出狀態。
 */
const props = withDefaults(
  defineProps<{ value: number; max: number; height?: number; level?: 'ok' | 'near' | 'over' }>(),
  { height: 8, level: undefined },
)

const ratio = computed(() => (props.max <= 0 ? (props.value > 0 ? 1.01 : 0) : props.value / props.max))
const level = computed(() => props.level ?? (ratio.value > 1 ? 'over' : ratio.value > 0.85 ? 'near' : 'ok'))
const width = computed(() => `${Math.min(100, Math.max(0, ratio.value * 100))}%`)
</script>

<template>
  <div
    class="meter"
    :class="level"
    :style="{ height: `${height}px` }"
    role="meter"
    :aria-valuenow="value"
    :aria-valuemin="0"
    :aria-valuemax="max"
  >
    <i class="anim-w" :style="{ width }" />
  </div>
</template>

<style scoped>
.meter {
  background: var(--track);
  border-radius: 4px;
  overflow: hidden;
  min-width: 60px;
}
.meter i {
  display: block;
  height: 100%;
  background: var(--series-1);
  border-radius: 0 4px 4px 0;
}
.meter.near i {
  background: var(--st-warn);
}
.meter.over i {
  background: var(--st-crit);
}
</style>
