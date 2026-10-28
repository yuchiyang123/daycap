<script setup lang="ts">
import { computed, ref } from 'vue'
import { money, pct } from '../lib/format'

/**
 * 部分對整體的圓餅（甜甜圈）：扇區之間留 2px 底色間隔，中間放總額。
 * 旁邊的對照表同時是圖例和「表格檢視」，顏色不是唯一的辨識方式。
 * 顏色由呼叫端決定（跟著分類的設定順序固定），篩選或排序不會重新上色。
 */
export interface Slice {
  key: string | number
  label: string
  value: number
  color: string
  note?: string
}

const props = withDefaults(defineProps<{ slices: Slice[]; centerLabel?: string; centerValue?: string; caption?: string; size?: number }>(), {
  size: 168,
})

const visible = computed(() => props.slices.filter((s) => s.value > 0))
const total = computed(() => visible.value.reduce((a, s) => a + s.value, 0))
const r = computed(() => props.size / 2 - 2)
const inner = computed(() => r.value * 0.62)
const c = computed(() => props.size / 2)
const hover = ref<string | number | null>(null)

function point(radius: number, angle: number): string {
  return `${(c.value + radius * Math.sin(angle)).toFixed(2)},${(c.value - radius * Math.cos(angle)).toFixed(2)}`
}

const arcs = computed(() => {
  let a0 = 0
  return visible.value.map((s) => {
    const sweep = total.value > 0 ? (s.value / total.value) * Math.PI * 2 : 0
    const a1 = a0 + sweep
    const large = sweep > Math.PI ? 1 : 0
    // 只有一塊時畫成完整圓環（弧線起訖點重合畫不出來）
    const d =
      visible.value.length === 1
        ? `M${c.value},${c.value - r.value}a${r.value},${r.value} 0 1,1 0,${2 * r.value}a${r.value},${r.value} 0 1,1 0,${-2 * r.value}Z` +
          `M${c.value},${c.value - inner.value}a${inner.value},${inner.value} 0 1,0 0,${2 * inner.value}a${inner.value},${inner.value} 0 1,0 0,${-2 * inner.value}Z`
        : `M${point(r.value, a0)}A${r.value},${r.value} 0 ${large},1 ${point(r.value, a1)}` +
          `L${point(inner.value, a1)}A${inner.value},${inner.value} 0 ${large},0 ${point(inner.value, a0)}Z`
    a0 = a1
    return { s, d }
  })
})
const hovered = computed(() => visible.value.find((s) => s.key === hover.value) ?? null)
</script>

<template>
  <div class="donut-wrap">
  <div class="donut">
    <svg :width="size" :height="size" :viewBox="`0 0 ${size} ${size}`" role="img" :aria-label="caption ?? '比例'">
      <path
        v-for="a in arcs"
        :key="a.s.key"
        :d="a.d"
        :fill="a.s.color"
        fill-rule="evenodd"
        class="slice"
        :opacity="hover === null || hover === a.s.key ? 1 : 0.4"
        @pointerenter="hover = a.s.key"
        @pointerdown="hover = a.s.key"
        @pointerleave="hover = null"
      />
      <text :x="c" :y="c - 6" text-anchor="middle" class="c-label">{{ hovered ? hovered.label : centerLabel }}</text>
      <text :x="c" :y="c + 14" text-anchor="middle" class="c-value">
        {{ hovered ? `${money(hovered.value)}・${pct(hovered.value / total)}` : centerValue ?? money(total) }}
      </text>
    </svg>
    <table class="tbl legend">
      <tbody>
        <tr
          v-for="s in slices"
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
          <td class="r num">{{ money(s.value) }}</td>
          <td class="r muted pc num">{{ total > 0 ? pct(Math.max(0, s.value) / total, 1) : '—' }}</td>
        </tr>
      </tbody>
    </table>
  </div>
  </div>
</template>

<style scoped>
.donut {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr);
  gap: 16px;
  align-items: center;
}
/* 依容器寬度（不是螢幕寬度）決定圖例放旁邊還是下面：雙欄並排時容器很窄 */
.donut-wrap {
  container-type: inline-size;
}
@container (max-width: 440px) {
  .donut {
    grid-template-columns: 1fr;
    justify-items: center;
  }
  .legend {
    width: 100%;
  }
}
.legend td:nth-child(2) {
  white-space: nowrap;
}
svg {
  display: block;
  overflow: visible;
}
.slice {
  stroke: var(--surface);
  stroke-width: 2;
  stroke-linejoin: round;
  cursor: default;
}
.c-label {
  font-size: 11px;
  fill: var(--muted);
}
.c-value {
  font-size: 14px;
  font-weight: 600;
  fill: var(--ink);
  font-variant-numeric: tabular-nums;
}
.legend td {
  padding-top: 5px;
  padding-bottom: 5px;
}
.legend tr.active td {
  background: var(--page);
}
.sw-cell {
  width: 14px;
}
.sw {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 2px;
  vertical-align: 1px;
}
.note {
  font-size: 11px;
  margin-left: 4px;
}
.pc {
  width: 52px;
}
</style>
