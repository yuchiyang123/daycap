<script setup lang="ts">
import { computed, ref } from 'vue'
import { useWidth } from '../lib/useWidth'
import { linear, niceDomain } from '../lib/scale'
import { compact, money } from '../lib/format'

/**
 * 折線圖：單一 y 軸、2px 線、端點 8px 圓點（外圈 2px 底色）、終點直接標數字。
 * 兩條以上一定有圖例；滑過（或手指按住拖）出現十字線 + 所有序列的數值。
 */
export interface Series {
  key: string
  label: string
  color: string
  values: (number | null)[]
  /** 虛線只用來表示「計畫」，不是格線。 */
  dashed?: boolean
}

const props = withDefaults(
  defineProps<{
    series: Series[]
    labels: string[]
    height?: number
    markerIndex?: number | null
    markerLabel?: string
    zeroBased?: boolean
  }>(),
  { height: 200, markerIndex: null, markerLabel: '今天', zeroBased: true },
)

const wrap = ref<HTMLElement | null>(null)
const width = useWidth(wrap)
const pad = computed(() => ({ top: 14, right: endLabelRoom.value, bottom: 28, left: 48 }))

const allValues = computed(() => props.series.flatMap((s) => s.values.filter((v): v is number => v !== null)))
const domain = computed(() => {
  const vals = allValues.value
  if (vals.length === 0) return niceDomain(0, 1)
  const lo = props.zeroBased ? Math.min(0, ...vals) : Math.min(...vals)
  return niceDomain(lo, Math.max(...vals), 4)
})

const n = computed(() => Math.max(1, props.labels.length))
const endLabelRoom = computed(() => (width.value < 420 ? 12 : 64))
const x = computed(() => linear(0, n.value - 1, pad.value.left, width.value - pad.value.right))
const y = computed(() => linear(domain.value.min, domain.value.max, props.height - pad.value.bottom, pad.value.top))

const paths = computed(() =>
  props.series.map((s) => {
    let d = ''
    let pen = false
    let last: { i: number; v: number } | null = null
    s.values.forEach((v, i) => {
      if (v === null) {
        pen = false
        return
      }
      d += `${pen ? 'L' : 'M'}${x.value(i).toFixed(1)},${y.value(v).toFixed(1)}`
      pen = true
      last = { i, v }
    })
    return { s, d, last: last as { i: number; v: number } | null }
  }),
)

const labelEvery = computed(() => {
  const per = (width.value - 60) / n.value
  return per >= 40 ? 1 : per >= 18 ? 5 : per >= 8 ? 7 : Math.ceil(n.value / 5)
})

const hover = ref<number | null>(null)
function onMove(ev: PointerEvent) {
  const rect = (ev.currentTarget as SVGElement).getBoundingClientRect()
  const px = ev.clientX - rect.left
  const i = Math.round(((px - pad.value.left) / (width.value - pad.value.left - pad.value.right)) * (n.value - 1))
  hover.value = Math.min(n.value - 1, Math.max(0, i))
}
const tipLeft = computed(() => (hover.value === null ? 0 : Math.min(Math.max(x.value(hover.value) - 90, 0), width.value - 180)))
</script>

<template>
  <div ref="wrap" class="chart">
    <div v-if="series.length > 1" class="legend">
      <span v-for="s in series" :key="s.key" class="key">
        <svg width="18" height="10" aria-hidden="true">
          <line x1="1" x2="17" y1="5" y2="5" :stroke="s.color" stroke-width="2" :stroke-dasharray="s.dashed ? '4 3' : undefined" stroke-linecap="round" />
        </svg>
        {{ s.label }}
      </span>
    </div>

    <div class="plot">
      <svg
        :width="width"
        :height="height"
        role="img"
        :aria-label="series.map((s) => s.label).join('、')"
        @pointermove="onMove"
        @pointerdown="onMove"
        @pointerleave="hover = null"
      >
        <g class="grid">
          <template v-for="t in domain.ticks" :key="t">
            <line :x1="pad.left" :x2="width - pad.right" :y1="y(t)" :y2="y(t)" :class="{ zero: t === 0 }" />
            <text :x="pad.left - 8" :y="y(t)" dy="0.32em" text-anchor="end">{{ compact(t) }}</text>
          </template>
          <template v-for="(l, i) in labels" :key="i">
            <text v-if="i % labelEvery === 0 || i === labels.length - 1" :x="x(i)" :y="height - 8" text-anchor="middle">{{ l }}</text>
          </template>
        </g>

        <g v-if="markerIndex !== null && markerIndex >= 0 && markerIndex < labels.length" class="marker">
          <line :x1="x(markerIndex)" :x2="x(markerIndex)" :y1="pad.top" :y2="height - pad.bottom" />
          <text :x="x(markerIndex) + 4" :y="pad.top + 8">{{ markerLabel }}</text>
        </g>

        <g>
          <path
            v-for="p in paths"
            :key="p.s.key"
            :d="p.d"
            fill="none"
            :stroke="p.s.color"
            stroke-width="2"
            stroke-linejoin="round"
            stroke-linecap="round"
            :stroke-dasharray="p.s.dashed ? '5 4' : undefined"
          />
          <template v-for="p in paths" :key="`${p.s.key}-end`">
            <g v-if="p.last">
              <circle :cx="x(p.last.i)" :cy="y(p.last.v)" r="4" :fill="p.s.color" class="ring" />
              <text v-if="width >= 420" :x="x(p.last.i) + 8" :y="y(p.last.v)" dy="0.32em" class="end">{{ compact(p.last.v) }}</text>
            </g>
          </template>
        </g>

        <g v-if="hover !== null" class="cross">
          <line :x1="x(hover)" :x2="x(hover)" :y1="pad.top" :y2="height - pad.bottom" />
          <template v-for="p in paths" :key="`${p.s.key}-h`">
            <circle v-if="p.s.values[hover] != null" :cx="x(hover)" :cy="y(p.s.values[hover]!)" r="4" :fill="p.s.color" class="ring" />
          </template>
        </g>
      </svg>

      <div v-if="hover !== null" class="tip" :style="{ left: `${tipLeft}px` }">
        <div class="tip-head">{{ labels[hover] }}</div>
        <div v-for="s in series" :key="s.key" class="tip-row">
          <span><i class="sw" :style="{ background: s.color }" />{{ s.label }}</span>
          <span class="num">{{ s.values[hover] == null ? '—' : money(s.values[hover]!) }}</span>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.chart {
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.plot {
  position: relative;
  user-select: none;
  touch-action: pan-y;
}
svg {
  display: block;
  overflow: visible;
}
.legend {
  display: flex;
  flex-wrap: wrap;
  gap: 4px 16px;
  font-size: 12px;
  color: var(--ink-2);
}
.key {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}
.grid line {
  stroke: var(--grid);
  shape-rendering: crispEdges;
}
.grid line.zero {
  stroke: var(--axis);
}
.grid text {
  font-size: 11px;
  fill: var(--muted);
  font-variant-numeric: tabular-nums;
}
.marker line {
  stroke: var(--axis);
  shape-rendering: crispEdges;
}
.marker text {
  font-size: 11px;
  fill: var(--ink-2);
}
.ring {
  stroke: var(--surface);
  stroke-width: 2;
}
.end {
  font-size: 12px;
  fill: var(--ink-2);
  font-variant-numeric: tabular-nums;
}
.cross line {
  stroke: var(--ink-2);
  stroke-width: 1;
  shape-rendering: crispEdges;
}
.tip {
  position: absolute;
  top: 0;
  width: 180px;
  pointer-events: none;
  background: var(--surface);
  border: 1px solid var(--line-2);
  border-radius: 4px;
  padding: 8px 10px;
  font-size: 12px;
}
.tip-head {
  font-weight: 600;
  font-size: 13px;
  margin-bottom: 2px;
}
.tip-row {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  color: var(--ink-2);
}
.sw {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 2px;
  margin-right: 6px;
}
</style>
