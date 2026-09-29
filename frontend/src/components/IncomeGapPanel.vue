<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRefreshOnReturn } from '../lib/useRefreshOnReturn'
import { addIncomeAdjustment, addTransfer, getRunway } from '../api/endpoints'
import type { RunwayView } from '../api/types'
import { setPeriod, store } from '../lib/store'
import { money, shortDate } from '../lib/format'

/**
 * 收入中斷（§18）：失業、無薪假、接案空窗。
 * 這期沒有收入 → 記一筆薪資調整把收入扣到 0；要從存款撥預算 → 待分配池補進一筆。
 * 並顯示「照目前花法，存款還能撐幾天」＝可動用資產 ÷ 最近 30 天實際平均日花費。
 */
const period = computed(() => store.period!)
const runway = ref<RunwayView | null>(null)
const error = ref<string | null>(null)
const busy = ref(false)
const topUp = ref('')

onMounted(async () => {
  runway.value = await getRunway().catch(() => null)
})
useRefreshOnReturn(async () => (runway.value = await getRunway()))

const alreadyZero = computed(() => period.value.income <= 0)

async function run(fn: () => Promise<void>) {
  busy.value = true
  error.value = null
  try {
    await fn()
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}

const armed = ref(false)
function noIncome() {
  if (!armed.value) {
    armed.value = true
    setTimeout(() => (armed.value = false), 4000)
    return
  }
  armed.value = false
  run(async () => {
    setPeriod(
      await addIncomeAdjustment(period.value.id, {
        kind: 'OtherDeduction',
        days: null,
        hours: null,
        amount: period.value.income,
        note: '收入中斷',
      }),
    )
  })
}

function fromSavings() {
  const n = Math.round(Number(topUp.value))
  if (!(n > 0)) return
  run(async () => {
    setPeriod(await addTransfer(period.value.id, period.value.today, n, '從存款撥入預算（收入中斷）'))
    topUp.value = ''
  })
}
</script>

<template>
  <div class="panel gap">
    <div v-if="runway" class="runway">
      <div>
        <span class="label">照目前花法，存款還能撐</span>
        <span class="big num">{{ runway.runwayDays === null ? '—' : `${runway.runwayDays} 天` }}</span>
      </div>
      <p class="muted small num">
        可動用 {{ money(runway.usableAssets) }}（銀行＋現金＋電子支付 − 信用卡欠款，不含股票）÷
        最近 {{ runway.basisDays }} 天平均每天花 {{ money(runway.avgDailySpend) }}
        <template v-if="runway.basisDays">（{{ shortDate(runway.from) }}–{{ shortDate(runway.to) }}，沒回報的時段照預算算）</template>
      </p>
    </div>

    <p class="muted small">失業、無薪假、接案空窗時用：</p>
    <div class="row">
      <button type="button" class="btn sm" :disabled="busy || alreadyZero" @click="noIncome">
        {{ alreadyZero ? '這期收入已經是 0' : armed ? `確認：這期收入 ${money(period.income)} → 0` : '這期沒有收入' }}
      </button>
    </div>
    <div class="row">
      <input v-model="topUp" class="input num" inputmode="numeric" placeholder="金額" aria-label="從存款撥入預算的金額" />
      <button type="button" class="btn sm" :disabled="busy || !(Number(topUp) > 0)" @click="fromSavings">從存款撥入預算</button>
    </div>
    <p class="muted small">撥入只是讓這期可以花存款裡的錢（加進待分配池），實際花掉時帳戶才會減少。</p>
    <p v-if="error" class="error-box">{{ error }}</p>
  </div>
</template>

<style scoped>
.gap {
  padding: 14px 16px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.runway {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding-bottom: 8px;
  border-bottom: 1px solid var(--line-2);
}
.label {
  display: block;
  font-size: 12px;
  color: var(--muted);
}
.big {
  font-size: 26px;
  font-weight: 600;
}
.small {
  font-size: 12px;
}
.row {
  display: flex;
  gap: 8px;
  align-items: center;
  flex-wrap: wrap;
}
.row .input {
  width: 140px;
}
</style>
