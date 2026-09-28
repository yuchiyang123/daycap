<script setup lang="ts">
import { computed, ref } from 'vue'
import { money, pct } from '../lib/format'

/**
 * 部分對整體：一條 100% 橫條 + 下方對照表。
 * 段與段之間用 2px 底色間隔分開（不畫框線）；對照表同時是色塊的圖例與「表格檢視」，
 * 淺色模式下幾個對比不足 3:1 的顏色也因此不會只靠顏色辨識。
 */
export interface Segment {
  key: string | number
  label: string
  value: number
  color: string
  note?: string
}

const props = defineProps<{ segments: Segment[]; total?: number; caption?: string }>()

const sum = computed(() => props.total ?? props.segments.reduce((s, x) => s + Math.max(0, x.value), 0))
const visible = computed(() => props.segments.filter((s) => s.value > 0))
const hover = ref<string | number | null>(null)
</script>

<template>
  <div class="alloc">
    <div class="bar" role="img" :aria-label="caption ?? '配置比例'">
      <div
        v-for="s in visible"
        :key="s.key"
        class="seg"
        :class="{ dim: hover !== null && hover !== s.key }"
        :style="{ flexGrow: s.value, background: s.color }"
        @pointerenter="hover = s.key"
        @pointerleave="hover = null"
      />
    </div>
    <table class="tbl legend">
      <tbody>
        <tr
          v-for="s in segments"
          :key="s.key"
          :class="{ active: hover === s.key }"
          @pointerenter="hover = s.key"
          @pointerleave="hover = null"
        >
          <td class="sw-cell"><span class="sw" :style="{ background: s.color }" /></td>
          <td>
            {{ s.label }}
            <span v-if="s.note" class="muted note">{{ s.note }}</span>
          </td>
          <td class="r">{{ money(s.value) }}</td>
          <td class="r muted pc">{{ sum > 0 ? pct(s.value / sum, 1) : '—' }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
.alloc {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.bar {
  display: flex;
  gap: 2px;
  height: 20px;
  border-radius: 4px;
  overflow: hidden;
  background: var(--surface);
}
.seg {
  flex-basis: 0;
  min-width: 3px;
  transition: opacity 0.15s;
}
.seg.dim {
  opacity: 0.35;
}
.legend td {
  padding: 6px 8px;
}
.legend tr.active td {
  background: var(--sunk);
}
.sw-cell {
  width: 20px;
  padding-right: 0 !important;
}
.sw {
  display: block;
  width: 10px;
  height: 10px;
  border-radius: 2px;
}
.note {
  font-size: 12px;
  margin-left: 6px;
}
.pc {
  width: 64px;
}
</style>
