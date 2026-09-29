<script setup lang="ts">
import { computed, ref } from 'vue'
import DayPanel from '../components/DayPanel.vue'
import ReportSheet from '../components/ReportSheet.vue'
import ExtraSheet from '../components/ExtraSheet.vue'
import DailyNetChart from '../charts/DailyNetChart.vue'
import type { SlotView } from '../api/types'
import { store } from '../lib/store'
import { dayLabel, money, parseDate, signed, shortDate } from '../lib/format'

const period = computed(() => store.period!)
const selected = ref<string>(period.value.today)
const selectedDay = computed(() => period.value.days.find((d) => d.date === selected.value) ?? period.value.days[0])

/** 月曆格：前面補空白讓第一天對齊星期。 */
const cells = computed(() => {
  const first = parseDate(period.value.days[0].date).getDay()
  const list = [...Array.from({ length: first }, () => null), ...period.value.days]
  while (list.length % 7 !== 0) list.push(null)
  return list
})

const reported = computed(() => period.value.days.filter((d) => d.status !== 'future'))
const sumNet = computed(() => reported.value.reduce((a, d) => a + d.net, 0))
const overDays = computed(() => reported.value.filter((d) => d.net < 0).length)
const underDays = computed(() => reported.value.filter((d) => d.net > 0).length)
const holidayCount = computed(() => period.value.days.filter((d) => d.isHoliday).length)

const reporting = ref<{ slot: SlotView; name: string } | null>(null)
const addingExtra = ref(false)
</script>

<template>
  <div class="page wide">
    <header class="page-head">
      <div>
        <h1>本期</h1>
        <p class="sub">{{ shortDate(period.startDate) }} – {{ shortDate(period.endDate) }}・{{ period.days.length }} 天，其中假日 {{ holidayCount }} 天</p>
      </div>
    </header>

    <div class="tiles">
      <div class="tile">
        <span class="label">累計差額</span>
        <span class="value" :class="sumNet > 0 ? 'good' : sumNet < 0 ? 'bad' : ''">{{ signed(sumNet) }}</span>
        <span class="note">到今天為止</span>
      </div>
      <div class="tile">
        <span class="label">超支天數</span>
        <span class="value">{{ overDays }}</span>
        <span class="note">少花 {{ underDays }} 天</span>
      </div>
      <div class="tile">
        <span class="label">待分配池</span>
        <span class="value" :class="period.pool.balance < 0 ? 'bad' : ''">{{ money(period.pool.balance) }}</span>
        <span class="note">期初 {{ money(period.pool.opening) }}</span>
      </div>
    </div>

    <section class="section">
      <h2 class="section-title">每日差額<span class="aside">藍 = 少花　紅 = 超支　點柱子看那天</span></h2>
      <div class="panel panel-pad">
        <DailyNetChart :days="period.days" @select="(d) => (selected = d)" />
      </div>
    </section>

    <div class="cols">
      <section class="section">
        <h2 class="section-title">月曆</h2>
        <div class="panel cal">
          <div v-for="w in ['日', '一', '二', '三', '四', '五', '六']" :key="w" class="wd">{{ w }}</div>
          <template v-for="(d, i) in cells" :key="i">
            <div v-if="!d" class="cell blank" />
            <button
              v-else
              type="button"
              class="cell"
              :class="{ hol: d.isHoliday, today: d.status === 'today', sel: d.date === selected, future: d.status === 'future' }"
              :aria-label="`${dayLabel(d.date)} 額度 ${d.planned}`"
              @click="selected = d.date"
            >
              <span class="dn">{{ parseDate(d.date).getDate() }}</span>
              <span class="hn">{{ d.holidayName ?? '' }}</span>
              <span class="pl num">{{ money(d.planned) }}</span>
              <span class="nt num" :class="d.net > 0 ? 'good' : d.net < 0 ? 'bad' : 'muted'">
                {{ d.net === 0 ? '' : signed(d.net) }}
              </span>
            </button>
          </template>
        </div>
        <p class="legend-note muted">灰底 = 假日（含國定假日、補假；補班日算上班日）</p>
      </section>

      <section class="section">
        <h2 class="section-title">
          {{ dayLabel(selectedDay.date) }}
          <span class="aside">{{ selectedDay.isHoliday ? selectedDay.holidayName ?? '假日' : '上班日' }}</span>
        </h2>
        <DayPanel :day="selectedDay" @report="(s, n) => (reporting = { slot: s, name: n })" @extra="addingExtra = true" />
      </section>
    </div>

    <ReportSheet
      v-if="reporting"
      :date="selectedDay.date"
      :slot="reporting.slot"
      :category-name="reporting.name"
      @close="reporting = null"
    />
    <ExtraSheet v-if="addingExtra" :date="selectedDay.date" @close="addingExtra = false" />
  </div>
</template>

<style scoped>
.tile .label {
  font-size: 12px;
  color: var(--muted);
}
.cols {
  display: grid;
  gap: 24px;
}
@media (min-width: 900px) {
  .cols {
    grid-template-columns: 1.1fr 1fr;
    align-items: start;
  }
}
.cal {
  display: grid;
  grid-template-columns: repeat(7, minmax(0, 1fr));
  gap: 1px;
  background: var(--line);
  overflow: hidden;
}
.wd {
  background: var(--surface);
  text-align: center;
  font-size: 12px;
  color: var(--muted);
  padding: 6px 0;
}
.cell {
  background: var(--surface);
  border: 0;
  padding: 6px 6px 5px;
  min-height: 72px;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 0;
  text-align: left;
  cursor: pointer;
  position: relative;
  min-width: 0;
}
.cell.blank {
  cursor: default;
}
.cell.hol {
  background: var(--sunk);
}
.cell:hover {
  outline: 1px solid var(--line-2);
  outline-offset: -1px;
}
.cell.sel {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}
.dn {
  font-size: 13px;
  font-weight: 600;
}
.cell.today .dn {
  background: var(--ink);
  color: var(--surface);
  border-radius: 3px;
  padding: 0 4px;
}
.hn {
  font-size: 10px;
  color: var(--muted);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 100%;
  min-height: 14px;
}
.pl {
  font-size: 12px;
  color: var(--ink-2);
  margin-top: auto;
}
.cell.future .pl {
  color: var(--muted);
}
.nt {
  font-size: 12px;
  font-weight: 600;
  min-height: 16px;
}
.legend-note {
  font-size: 12px;
}
@media (max-width: 420px) {
  .cell {
    min-height: 60px;
    padding: 4px 3px;
  }
  .pl {
    font-size: 11px;
  }
  .hn {
    display: none;
  }
}
</style>
