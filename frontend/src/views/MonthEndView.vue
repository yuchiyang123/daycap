<script setup lang="ts">
import Skeleton from '../components/Skeleton.vue'
import { computed, onMounted, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import ReconcileSheet from '../components/ReconcileSheet.vue'
import { closeMonth, getAccounts, getMonthEnd } from '../api/endpoints'
import type { AccountView, MonthEndReport, ShortfallChoice } from '../api/types'
import { loadCurrentPeriod } from '../lib/store'
import { money, shortDate, signed } from '../lib/format'

/**
 * 月結（§12.2）：對帳 → 看報告 → 決定結果怎麼處理、下期時段要不要調 → 關帳。
 * 關帳後這期鎖住，結果變成下一期的期初項目。
 */
const route = useRoute()
const periodId = computed(() => Number(route.params.periodId))

const report = ref<MonthEndReport | null>(null)
const accounts = ref<AccountView[]>([])
const error = ref<string | null>(null)
const reconciling = ref(false)
const closing = ref(false)

const decision = ref<ShortfallChoice>('Pool')
const accountId = ref<number | null>(null)
/** 下期時段金額：key = `${categoryId}-${slotId}` */
const edits = ref<Record<string, { workday: string; holiday: string }>>({})

async function load() {
  error.value = null
  try {
    report.value = await getMonthEnd(periodId.value)
    edits.value = {}
    for (const s of report.value.slots)
      edits.value[`${s.categoryId}-${s.slotId}`] = { workday: String(s.workdayAmount), holiday: String(s.holidayAmount) }
  } catch (e) {
    error.value = (e as Error).message
  }
}

onMounted(async () => {
  await load()
  accounts.value = (await getAccounts().catch(() => ({ accounts: [] as AccountView[] }))).accounts.filter((a) => a.type !== 'CreditCard')
  accountId.value = accounts.value[0]?.id ?? null
})

const surplus = computed(() => (report.value?.result ?? 0) >= 0)
const options = computed<{ v: ShortfallChoice; label: string; hint: string }[]>(() =>
  surplus.value
    ? [
        { v: 'Pool', label: '帶進下一期', hint: '下一期期初待分配池多這筆' },
        { v: 'Savings', label: '轉進存款', hint: '記成帳戶的一筆收入，下一期從 0 開始' },
      ]
    : [
        { v: 'Pool', label: '下一期期初扣掉', hint: '下一期待分配池少這筆' },
        { v: 'Split', label: '分兩期還', hint: '下一期、再下一期各扣一半' },
        { v: 'Savings', label: '從存款吸收', hint: '從帳戶扣掉，下一期不受影響' },
      ],
)

const slotChanges = computed(() =>
  (report.value?.slots ?? [])
    .map((s) => {
      const e = edits.value[`${s.categoryId}-${s.slotId}`]
      return { s, w: Math.round(Number(e?.workday)), h: Math.round(Number(e?.holiday)) }
    })
    .filter(({ s, w, h }) => Number.isFinite(w) && Number.isFinite(h) && w >= 0 && h >= 0 && (w !== s.workdayAmount || h !== s.holidayAmount))
    .map(({ s, w, h }) => ({ categoryId: s.categoryId, slotId: s.slotId, workdayAmount: w, holidayAmount: h })),
)

const armed = ref(false)
async function close() {
  if (!report.value) return
  if (!armed.value) {
    armed.value = true
    setTimeout(() => (armed.value = false), 4000)
    return
  }
  armed.value = false
  closing.value = true
  error.value = null
  try {
    report.value = await closeMonth(periodId.value, {
      decision: decision.value,
      accountId: decision.value === 'Savings' ? accountId.value : null,
      slotChanges: slotChanges.value,
    })
    await loadCurrentPeriod(true)
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    closing.value = false
  }
}

function suggest(avgActual: number, current: number) {
  if (avgActual <= 0) return null
  const rounded = Math.round(avgActual / 5) * 5
  return Math.abs(rounded - current) >= 5 ? rounded : null
}
</script>

<template>
  <div class="page">
    <header class="page-head">
      <div>
        <h1>月結</h1>
        <p v-if="report" class="sub">{{ shortDate(report.startDate) }} – {{ shortDate(report.endDate) }}</p>
      </div>
      <RouterLink to="/" class="btn quiet sm">回今天</RouterLink>
    </header>

    <p v-if="error" class="error-box">{{ error }}</p>

    <template v-if="report">
      <!-- 已月結：只顯示結果 -->
      <section v-if="report.summary" class="panel done">
        <span class="label">這期結果</span>
        <span class="big num" :class="report.summary.result >= 0 ? 'good' : 'bad'">{{ signed(report.summary.result) }}</span>
        <p>{{ report.summary.message }}</p>
        <p class="muted small">月結時間 {{ new Date(report.summary.closedAt).toLocaleString('zh-TW') }}・這期已鎖住，之後補登會記在當期。</p>
      </section>

      <template v-else>
        <!-- 1. 對帳 -->
        <section class="panel step">
          <div class="step-head">
            <span class="step-no">1</span>
            <b>對帳</b>
          </div>
          <p v-if="report.needsReconciliation" class="muted">
            月結前要做一次完整對帳（最後 7 天內），把各帳戶的實際餘額填進來，沒交代的差額才會算進結果。
          </p>
          <p v-else class="muted">最近 7 天內已經完整對帳過。</p>
          <button type="button" class="btn" :class="{ primary: report.needsReconciliation }" @click="reconciling = true">
            {{ report.needsReconciliation ? '開始對帳' : '再對一次' }}
          </button>
        </section>

        <!-- 2. 報告 -->
        <section class="panel step">
          <div class="step-head">
            <span class="step-no">2</span>
            <b>這期報告</b>
          </div>
          <div class="result">
            <div>
              <span class="label">結果（收入 − 所有支出）</span>
              <span class="big num" :class="report.result >= 0 ? 'good' : 'bad'">{{ signed(report.result) }}</span>
            </div>
            <div v-if="report.previousResult !== null">
              <span class="label">上一期</span>
              <span class="mid num">{{ signed(report.previousResult) }}</span>
            </div>
            <div v-if="report.unexplained !== 0">
              <span class="label">對帳差額</span>
              <span class="mid num">{{ signed(report.unexplained) }}</span>
            </div>
          </div>

          <template v-if="report.topOverspends.length">
            <h3 class="sub-title">超支最多</h3>
            <ul class="rows">
              <li v-for="o in report.topOverspends" :key="o.label">
                <span>{{ o.label }}<span class="muted small">・{{ o.days }} 天</span></span>
                <span class="num bad">{{ money(o.total) }}</span>
              </li>
            </ul>
          </template>

          <template v-if="report.subItemOvers?.length">
            <h3 class="sub-title">細項超過上限</h3>
            <ul class="rows">
              <li v-for="s in report.subItemOvers" :key="`${s.categoryId}-${s.name}`">
                <span>{{ s.name }}<span class="muted small">・花 {{ money(s.spent) }}／上限 {{ money(s.cap ?? 0) }}</span></span>
                <span class="num bad">超 {{ money(s.over) }}</span>
              </li>
            </ul>
          </template>

          <template v-if="report.goals.length">
            <h3 class="sub-title">目標</h3>
            <ul class="rows">
              <li v-for="g in report.goals" :key="g.name">
                <span>
                  {{ g.name }}
                  <span class="muted small">
                    ・{{ Math.round(g.progress * 100) }}%
                    <template v-if="g.estimatedDate">・預估 {{ shortDate(g.estimatedDate) }} 達成</template>
                  </span>
                </span>
                <span class="num">{{ money(g.current) }} / {{ money(g.target) }}</span>
              </li>
            </ul>
          </template>
        </section>

        <!-- 3. 決定 -->
        <section class="panel step">
          <div class="step-head">
            <span class="step-no">3</span>
            <b>{{ surplus ? '結餘怎麼處理' : '超支怎麼補' }}</b>
          </div>
          <label v-for="o in options" :key="o.v" class="check">
            <input v-model="decision" type="radio" :value="o.v" name="decision" />
            <span>{{ o.label }}<span class="hint">{{ o.hint }}</span></span>
          </label>
          <label v-if="decision === 'Savings'" class="field acct">
            帳戶
            <select v-model="accountId" class="select">
              <option v-for="a in accounts" :key="a.id" :value="a.id">{{ a.name }}（{{ money(a.balance) }}）</option>
            </select>
          </label>

          <template v-if="report.slots.length">
            <h3 class="sub-title">下一期的時段金額<span class="muted small">・照這期平均實際花費參考</span></h3>
            <div class="slot-table">
              <div class="slot-row head">
                <span>時段</span>
                <span>平均實際</span>
                <span>上班日</span>
                <span>假日</span>
              </div>
              <div v-for="s in report.slots" :key="`${s.categoryId}-${s.slotId}`" class="slot-row">
                <span>
                  {{ s.slotName }}
                  <span class="muted small">{{ s.categoryName }}・回報 {{ s.reportedDays }} 天</span>
                </span>
                <span class="num">
                  {{ money(s.avgActual) }}
                  <button
                    v-if="suggest(s.avgActual, s.workdayAmount) !== null"
                    type="button"
                    class="btn quiet sm"
                    @click="edits[`${s.categoryId}-${s.slotId}`].workday = String(suggest(s.avgActual, s.workdayAmount))"
                  >
                    套用
                  </button>
                </span>
                <input v-model="edits[`${s.categoryId}-${s.slotId}`].workday" class="input num" inputmode="numeric" />
                <input v-model="edits[`${s.categoryId}-${s.slotId}`].holiday" class="input num" inputmode="numeric" />
              </div>
            </div>
            <p class="muted small">改了的時段從下一期開始生效；超過預算的話會擋下來，到設定頁再調。</p>
          </template>
        </section>

        <p v-if="!report.canClose" class="notice-line">{{ report.cannotCloseReason }}</p>
        <button type="button" class="btn primary block" :disabled="!report.canClose || closing" @click="close">
          {{ closing ? '月結中…' : armed ? '確認月結（之後這期就鎖住了）' : '月結' }}
        </button>
      </template>
    </template>
    <Skeleton v-else-if="!error" variant="page" :header="false" />

    <ReconcileSheet v-if="reconciling" @close="reconciling = false" @saved="reconciling = false; load()" />
  </div>
</template>

<style scoped>
.page {
  display: flex;
  flex-direction: column;
  gap: 16px;
}
.step,
.done {
  padding: 14px 16px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.step-head {
  display: flex;
  align-items: center;
  gap: 10px;
}
.step-no {
  width: 22px;
  height: 22px;
  border: 1px solid var(--ink);
  border-radius: 50%;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  font-size: 12px;
  font-weight: 600;
}
.label {
  display: block;
  font-size: 12px;
  color: var(--muted);
  letter-spacing: 0.06em;
}
.big {
  font-size: 28px;
  font-weight: 600;
}
.mid {
  font-size: 18px;
  font-weight: 500;
}
.small {
  font-size: 12px;
}
.result {
  display: flex;
  gap: 24px;
  flex-wrap: wrap;
  align-items: flex-end;
}
.sub-title {
  font-size: 13px;
  font-weight: 600;
  margin: 6px 0 0;
}
.rows {
  list-style: none;
  margin: 0;
  padding: 0;
}
.rows li {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  padding: 6px 0;
  border-top: 1px solid var(--line-2);
  font-size: 14px;
}
.check input,
.check input::after {
  border-radius: 50%;
}
.acct {
  padding-left: 28px;
}
.slot-table {
  display: flex;
  flex-direction: column;
}
.slot-row {
  display: grid;
  grid-template-columns: minmax(0, 1.4fr) minmax(0, 1fr) 72px 72px;
  gap: 8px;
  align-items: center;
  padding: 6px 0;
  border-top: 1px solid var(--line-2);
  font-size: 14px;
}
.slot-row > span:first-child {
  display: flex;
  flex-direction: column;
  min-width: 0;
}
.slot-row.head {
  font-size: 12px;
  color: var(--muted);
  border-top: 0;
}
.slot-row .input {
  width: 100%;
  min-width: 0;
}
.block {
  width: 100%;
}
@media (max-width: 420px) {
  .slot-row {
    grid-template-columns: minmax(0, 1fr) 64px 64px;
  }
  .slot-row > span:nth-child(2) {
    display: none;
  }
}
</style>
