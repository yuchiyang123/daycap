<script setup lang="ts">
import { computed, ref } from 'vue'
import { useWidth } from '../lib/useWidth'
import { niceDomain, linear } from '../lib/scale'
import { money, shortDate, signed, weekday } from '../lib/format'
import type { DayView } from '../api/types'

/**
 * 每日 +/-：發散柱狀圖，藍 = 少花（進待分配池），紅 = 超支。中線是 0。
 * 還沒到的日子不畫柱，只留刻度；假日在軸下方標一個小方塊。
 * 每一欄整欄都是感應區，手機點一下就有數字。
 */
const props = defineProps<{ days: DayView[] }>()
const emit = defineEmits<{ select: [date: string] }>()

const wrap = ref<HTMLElement | null>(null)
const width = useWidth(wrap)
const height = 180
const pad = { top: 12, right: 8, bottom: 34, left: 44 }

const domain = computed(() => {
  const vals = props.days.filter((d) => d.status !== 'future').map((d) => d.net)
  const lo = Math.min(0, ...vals)
  const hi = Math.max(0, ...vals)
  if (lo === 0 && hi === 0) return niceDomain(-100, 100, 4)
  const m = Math.max(Math.abs(lo), Math.abs(hi))
  return niceDomain(lo < 0 ? -m : 0, hi > 0 ? m : 0, 4)
})

const plotW = computed(() => Math.max(10, width.value - pad.left - pad.right))
const band = computed(() => plotW.value / Math.max(1, props.days.length))
const barW = computed(() => Math.max(2, Math.min(24, band.value - 2)))
const y = computed(() => linear(domain.value.min, domain.value.max, height - pad.bottom, pad.top))

const bars = computed(() =>
  props.days.map((d, i) => {
    const x = pad.left + i * band.value + (band.value - barW.value) / 2
    const y0 = y.value(0)
    const y1 = y.value(d.net)
    return {
      d,
      i,
      x,
      top: Math.min(y0, y1),
      h: Math.abs(y1 - y0),
      positive: d.net > 0,
      show: d.status !== 'future' && d.net !== 0,
    }
  }),
)

/** 柱子只有「資料端」是圓角，貼著基準線那端是直角。 */
function barPath(b: (typeof bars.value)[number]): string {
  const r = Math.min(4, b.h, barW.value / 2)
  const x0 = b.x
  const x1 = b.x + barW.value
  if (b.positive) {
    const yb = b.top + b.h
    return `M${x0},${yb}V${b.top + r}Q${x0},${b.top} ${x0 + r},${b.top}H${x1 - r}Q${x1},${b.top} ${x1},${b.top + r}V${yb}Z`
  }
  const yt = b.top
  const yb = b.top + b.h
  return `M${x0},${yt}V${yb - r}Q${x0},${yb} ${x0 + r},${yb}H${x1 - r}Q${x1},${yb} ${x1},${yb - r}V${yt}Z`
}

const labelEvery = computed(() => (band.value >= 22 ? 2 : band.value >= 12 ? 5 : 7))
const hover = ref<number | null>(null)
const hovered = computed(() => (hover.value === null ? null : bars.value[hover.value]))
const tipLeft = computed(() => {
  if (!hovered.value) return 0
  const cx = hovered.value.x + barW.value / 2
  return Math.min(Math.max(cx - 80, 0), width.value - 160)
})
</script>

<template>
  <div ref="wrap" class="chart" @pointerleave="hover = null">
    <svg :width="width" :height="height" role="img" aria-label="每日差額">
      <g class="grid">
        <template v-for="t in domain.ticks" :key="t">
          <line :x1="pad.left" :x2="width - pad.right" :y1="y(t)" :y2="y(t)" :class="{ zero: t === 0 }" />
          <text :x="pad.left - 8" :y="y(t)" dy="0.32em" text-anchor="end">{{ t === 0 ? '0' : signed(t) }}</text>
        </template>
      </g>

      <g>
        <template v-for="b in bars" :key="b.d.date">
          <path v-if="b.show" :d="barPath(b)" :class="b.positive ? 'pos' : 'neg'" :opacity="hover === null || hover === b.i ? 1 : 0.45" />
          <rect
            v-if="b.d.isHoliday"
            :x="b.x + barW / 2 - 2"
            :y="height - pad.bottom + 5"
            width="4"
            height="4"
            class="hol"
          />
          <text
            v-if="b.i % labelEvery === 0 || b.d.status === 'today'"
            :x="b.x + barW / 2"
            :y="height - pad.bottom + 22"
            text-anchor="middle"
            class="xl"
            :class="{ today: b.d.status === 'today' }"
          >
            {{ b.d.status === 'today' ? '今天' : shortDate(b.d.date) }}
          </text>
          <rect
            :x="pad.left + b.i * band"
            :y="pad.top"
            :width="band"
            :height="height - pad.top - pad.bottom + 20"
            fill="transparent"
            class="hit"
            @pointerenter="hover = b.i"
            @pointerdown="hover = b.i"
            @click="emit('select', b.d.date)"
          />
        </template>
      </g>
    </svg>

    <div v-if="hovered" class="tip" :style="{ left: `${tipLeft}px` }">
      <div class="tip-head">
        {{ shortDate(hovered.d.date) }}（{{ weekday(hovered.d.date) }}）
        <span v-if="hovered.d.isHoliday" class="tag">{{ hovered.d.holidayName ?? '假日' }}</span>
      </div>
      <div class="tip-row">
        <span>額度</span><span class="num">{{ money(hovered.d.planned) }}</span>
      </div>
      <div class="tip-row">
        <span>差額</span>
        <span class="num" :class="hovered.d.net > 0 ? 'good' : hovered.d.net < 0 ? 'bad' : ''">
          {{ hovered.d.status === 'future' ? '尚未到' : signed(hovered.d.net) }}
        </span>
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
.xl.today {
  fill: var(--ink);
  font-weight: 600;
}
.pos {
  fill: var(--pos);
}
.neg {
  fill: var(--neg);
}
.hol {
  fill: var(--muted);
}
.hit {
  cursor: pointer;
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
  display: flex;
  gap: 6px;
  align-items: center;
}
.tip-row {
  display: flex;
  justify-content: space-between;
  color: var(--ink-2);
}
</style>
