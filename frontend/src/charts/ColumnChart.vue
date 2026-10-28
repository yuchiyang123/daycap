<script setup lang="ts">
import { computed, ref } from 'vue'
import { useWidth } from '../lib/useWidth'
import { niceDomain, linear } from '../lib/scale'
import { compact, money, signed } from '../lib/format'

/**
 * 直條圖：每一欄一個實際值，另外可以畫一條「計畫」短橫線當對照。
 * 柱子只有資料端是圓角；整欄都是感應區，手機點一下就有數字。
 */
export interface Column {
  key: string
  label: string
  value: number | null
  plan?: number | null
  note?: string
}

const props = withDefaults(defineProps<{ columns: Column[]; height?: number; caption?: string; valueLabel?: string; planLabel?: string }>(), {
  height: 200,
  valueLabel: '實際',
  planLabel: '計畫',
})

const wrap = ref<HTMLElement | null>(null)
const width = useWidth(wrap)
const pad = { top: 12, right: 8, bottom: 28, left: 44 }

const domain = computed(() => {
  const vals = props.columns.flatMap((c) => [c.value ?? 0, c.plan ?? 0])
  return niceDomain(0, Math.max(100, ...vals), 4)
})
const plotW = computed(() => Math.max(10, width.value - pad.left - pad.right))
const band = computed(() => plotW.value / Math.max(1, props.columns.length))
const barW = computed(() => Math.max(4, Math.min(40, band.value * 0.6)))
const y = computed(() => linear(domain.value.min, domain.value.max, props.height - pad.bottom, pad.top))

const bars = computed(() =>
  props.columns.map((c, i) => {
    const x = pad.left + i * band.value + (band.value - barW.value) / 2
    const top = c.value === null ? 0 : y.value(c.value)
    const h = c.value === null ? 0 : y.value(0) - top
    const r = Math.min(4, h, barW.value / 2)
    const base = y.value(0)
    const d = `M${x},${base}V${top + r}Q${x},${top} ${x + r},${top}H${x + barW.value - r}Q${x + barW.value},${top} ${x + barW.value},${top + r}V${base}Z`
    return { c, i, x, d, show: c.value !== null && h > 0, over: c.plan != null && c.value != null && c.value > c.plan }
  }),
)
const hover = ref<number | null>(null)
const hovered = computed(() => (hover.value === null ? null : bars.value[hover.value]))
const tipLeft = computed(() => {
  if (!hovered.value) return 0
  return Math.min(Math.max(hovered.value.x + barW.value / 2 - 80, 0), width.value - 160)
})
</script>

<template>
  <div ref="wrap" class="chart" @pointerleave="hover = null">
    <svg :width="width" :height="height" role="img" :aria-label="caption ?? '直條圖'">
      <g class="grid">
        <template v-for="t in domain.ticks" :key="t">
          <line :x1="pad.left" :x2="width - pad.right" :y1="y(t)" :y2="y(t)" :class="{ zero: t === 0 }" />
          <text :x="pad.left - 8" :y="y(t)" dy="0.32em" text-anchor="end">{{ compact(t) }}</text>
        </template>
      </g>
      <g v-for="b in bars" :key="b.c.key">
        <path v-if="b.show" :d="b.d" :class="b.over ? 'bar over' : 'bar'" :opacity="hover === null || hover === b.i ? 1 : 0.45" />
        <line
          v-if="b.c.plan != null"
          :x1="b.x - 4"
          :x2="b.x + barW + 4"
          :y1="y(b.c.plan)"
          :y2="y(b.c.plan)"
          class="plan"
        />
        <text :x="b.x + barW / 2" :y="height - pad.bottom + 18" text-anchor="middle" class="xl">{{ b.c.label }}</text>
        <rect
          :x="pad.left + b.i * band"
          :y="pad.top"
          :width="band"
          :height="height - pad.top - pad.bottom"
          fill="transparent"
          @pointerenter="hover = b.i"
          @pointerdown="hover = b.i"
        />
      </g>
    </svg>
    <div class="legend-line">
      <span><i class="sw bar-sw" />{{ valueLabel }}</span>
      <span v-if="columns.some((c) => c.plan != null)"><i class="sw plan-sw" />{{ planLabel }}</span>
    </div>
    <div v-if="hovered" class="tip" :style="{ left: `${tipLeft}px` }">
      <div class="tip-head">{{ hovered.c.note ?? hovered.c.label }}</div>
      <div class="tip-row"><span>{{ valueLabel }}</span><span class="num">{{ hovered.c.value === null ? '尚未到' : money(hovered.c.value) }}</span></div>
      <div v-if="hovered.c.plan != null" class="tip-row"><span>{{ planLabel }}</span><span class="num">{{ money(hovered.c.plan) }}</span></div>
      <div v-if="hovered.c.plan != null && hovered.c.value !== null" class="tip-row">
        <span>差額</span>
        <span class="num" :class="hovered.c.plan - hovered.c.value >= 0 ? 'good' : 'bad'">{{ signed(hovered.c.plan - hovered.c.value) }}</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.chart {
  position: relative;
  width: 100%;
  user-select: none;
}
svg {
  display: block;
  overflow: visible;
}
.grid line {
  stroke: var(--grid);
  stroke-width: 1;
  shape-rendering: crispEdges;
}
.grid line.zero {
  stroke: var(--axis);
}
.grid text,
.xl {
  font-size: 11px;
  fill: var(--muted);
  font-variant-numeric: tabular-nums;
}
.bar {
  fill: var(--series-1);
}
.bar.over {
  fill: var(--neg);
}
.plan {
  stroke: var(--ink-2);
  stroke-width: 2;
  stroke-linecap: round;
}
.legend-line {
  display: flex;
  gap: 14px;
  font-size: 12px;
  color: var(--muted);
  margin-top: 6px;
}
.sw {
  display: inline-block;
  width: 10px;
  height: 8px;
  margin-right: 5px;
  border-radius: 2px;
}
.bar-sw {
  background: var(--series-1);
}
.plan-sw {
  height: 2px;
  vertical-align: 3px;
  background: var(--ink-2);
}
.tip {
  position: absolute;
  top: 0;
  width: 160px;
  pointer-events: none;
  background: var(--surface);
  border: 1px solid var(--line-2);
  border-radius: 4px;
  padding: 8px 10px;
  font-size: 12px;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.tip-head {
  font-weight: 600;
  font-size: 13px;
}
.tip-row {
  display: flex;
  justify-content: space-between;
  gap: 8px;
}
</style>
