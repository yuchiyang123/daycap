<script setup lang="ts">
/**
 * 骨架屏：資料還沒回來時先排出版面，平面色塊慢慢明暗呼吸（不用漸層光澤）。
 * page＝整頁（標題＋大數字＋兩塊清單），list＝一塊清單，lines＝幾行文字。
 */
// header＝整頁骨架要不要畫標題（頁面本身已經有標題時傳 false）
withDefaults(defineProps<{ variant?: 'page' | 'list' | 'lines'; rows?: number; header?: boolean }>(), { variant: 'lines', rows: 3, header: true })
const widths = ['92%', '68%', '80%', '55%', '74%', '62%']
</script>

<template>
  <div class="sk" role="status" aria-live="polite" aria-label="載入中">
    <template v-if="variant === 'page'">
      <template v-if="header">
        <div class="bar title" />
        <div class="bar sub" />
      </template>
      <div class="panel box hero">
        <div class="bar small" />
        <div class="bar big" />
        <div class="bar sub" />
      </div>
      <div v-for="p in 2" :key="p" class="panel box">
        <div v-for="r in rows + 1" :key="r" class="row">
          <div class="bar" :style="{ width: widths[(r + p) % widths.length] }" />
          <div class="bar num" />
        </div>
      </div>
    </template>
    <div v-else-if="variant === 'list'" class="panel box">
      <div v-for="r in rows" :key="r" class="row">
        <div class="bar" :style="{ width: widths[r % widths.length] }" />
        <div class="bar num" />
      </div>
    </div>
    <template v-else>
      <div v-for="r in rows" :key="r" class="bar" :style="{ width: widths[r % widths.length] }" />
    </template>
  </div>
</template>

<style scoped>
.sk {
  display: flex;
  flex-direction: column;
  gap: 10px;
  width: 100%;
}
.bar {
  height: 12px;
  border-radius: 3px;
  background: var(--line);
  animation: pulse 1.4s ease-in-out infinite;
}
.title {
  width: 30%;
  height: 22px;
}
.sub {
  width: 55%;
}
.small {
  width: 22%;
  height: 10px;
}
.big {
  width: 45%;
  height: 36px;
}
.box {
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 14px;
}
.row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 16px;
}
.row .bar:first-child {
  flex: 0 1 auto;
}
.num {
  width: 64px;
  flex: none;
}
@keyframes pulse {
  0%,
  100% {
    opacity: 1;
  }
  50% {
    opacity: 0.45;
  }
}
@media (prefers-reduced-motion: reduce) {
  .bar {
    animation: none;
  }
}
</style>
