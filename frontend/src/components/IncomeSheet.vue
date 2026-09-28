<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Sheet from './Sheet.vue'
import { addIncomeAdjustment, confirmIncome, deleteIncomeAdjustment } from '../api/endpoints'
import type { IncomeAdjustmentKind } from '../api/types'
import { store, setPeriod } from '../lib/store'
import { money, signed } from '../lib/format'

/**
 * 發薪日確認本期薪資：設定裡的月收入只是預設值，實際會因請假、加班、獎金而變動。
 * 請假依勞基法常見算法試算（日薪 = 月薪 ÷ 30、時薪 = 日薪 ÷ 8；事假全扣、病假 / 生理假扣半），
 * 金額可以直接改成薪資單上的數字。
 */
const emit = defineEmits<{ close: []; allocate: [] }>()
const period = computed(() => store.period!)

const KINDS: { v: IncomeAdjustmentKind; label: string; rate: number | null; deduct: boolean }[] = [
  { v: 'PersonalLeave', label: '事假（全扣）', rate: 1, deduct: true },
  { v: 'SickLeave', label: '病假（扣半薪）', rate: 0.5, deduct: true },
  { v: 'MenstrualLeave', label: '生理假（扣半薪）', rate: 0.5, deduct: true },
  { v: 'OtherDeduction', label: '其他扣款', rate: null, deduct: true },
  { v: 'Overtime', label: '加班費', rate: null, deduct: false },
  { v: 'Bonus', label: '獎金', rate: null, deduct: false },
  { v: 'OtherAddition', label: '其他加給', rate: null, deduct: false },
]

const kind = ref<IncomeAdjustmentKind>('PersonalLeave')
const days = ref('')
const hours = ref('')
const amount = ref('')
const note = ref('')
const amountTouched = ref(false)
const busy = ref(false)
const error = ref<string | null>(null)

const meta = computed(() => KINDS.find((k) => k.v === kind.value)!)
const isLeave = computed(() => meta.value.rate !== null)
const daily = computed(() => period.value.baseIncome / 30)

/** 依天數 / 時數試算的扣薪 */
const suggested = computed(() => {
  if (!isLeave.value) return null
  const d = Number(days.value) || 0
  const h = Number(hours.value) || 0
  if (d <= 0 && h <= 0) return null
  return Math.round((daily.value * d + (daily.value / 8) * h) * meta.value.rate!)
})

watch(suggested, (v) => {
  if (!amountTouched.value) amount.value = v === null ? '' : String(v)
})
watch(kind, () => {
  amountTouched.value = false
  if (!isLeave.value) {
    days.value = ''
    hours.value = ''
    amount.value = ''
  } else {
    amount.value = suggested.value === null ? '' : String(suggested.value)
  }
})

const canAdd = computed(() => Number(amount.value) > 0)

async function add() {
  if (!canAdd.value) return
  busy.value = true
  error.value = null
  try {
    setPeriod(
      await addIncomeAdjustment(period.value.id, {
        kind: kind.value,
        days: isLeave.value && Number(days.value) > 0 ? Number(days.value) : null,
        hours: isLeave.value && Number(hours.value) > 0 ? Number(hours.value) : null,
        amount: Math.round(Number(amount.value)),
        note: note.value.trim() || null,
      }),
    )
    days.value = ''
    hours.value = ''
    amount.value = ''
    note.value = ''
    amountTouched.value = false
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}

async function remove(id: number) {
  setPeriod(await deleteIncomeAdjustment(period.value.id, id))
}

async function confirm() {
  busy.value = true
  try {
    setPeriod(await confirmIncome(period.value.id))
    if (store.period!.pool.balance > 0) emit('allocate')
    else emit('close')
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Sheet title="本期薪資" :subtitle="`預設 ${money(period.baseIncome)}，請假、加班、獎金在這裡加減`" @close="emit('close')">
    <div class="sum num">
      <div>
        <span class="label">實領</span>
        <b class="big">{{ money(period.income) }}</b>
      </div>
      <div class="right">
        <span class="label">和預設差</span>
        <b :class="period.income < period.baseIncome ? 'bad' : period.income > period.baseIncome ? 'good' : ''">
          {{ signed(period.income - period.baseIncome) }}
        </b>
      </div>
    </div>

    <ul v-if="period.incomeAdjustments.length" class="list adj">
      <li v-for="a in period.incomeAdjustments" :key="a.id">
        <span>{{ a.label }}</span>
        <span class="num" :class="a.amount < 0 ? 'bad' : 'good'">{{ signed(a.amount) }}</span>
        <button type="button" class="btn quiet sm danger" @click="remove(a.id)">刪除</button>
      </li>
    </ul>

    <form class="form" @submit.prevent="add">
      <label class="field">
        項目
        <select v-model="kind" class="select">
          <option v-for="k in KINDS" :key="k.v" :value="k.v">{{ k.label }}</option>
        </select>
      </label>
      <div v-if="isLeave" class="row2">
        <label class="field">
          天數
          <input v-model="days" class="input num" inputmode="decimal" placeholder="0" />
        </label>
        <label class="field">
          時數
          <input v-model="hours" class="input num" inputmode="decimal" placeholder="0" />
        </label>
      </div>
      <p v-if="isLeave" class="muted small num">
        日薪 {{ money(daily) }}・時薪 {{ money(daily / 8) }}，{{ meta.rate === 1 ? '全扣' : '扣一半' }}。薪資單金額不同就直接改下面的數字。
      </p>
      <div class="row2">
        <label class="field">
          {{ meta.deduct ? '扣多少' : '加多少' }}
          <input v-model="amount" class="input num" inputmode="numeric" placeholder="0" @input="amountTouched = true" />
        </label>
        <label class="field">
          備註
          <input v-model="note" class="input" maxlength="120" placeholder="選填" />
        </label>
      </div>
      <p v-if="error" class="error-box">{{ error }}</p>
      <button type="submit" class="btn" :disabled="!canAdd || busy">加入這筆</button>
    </form>

    <hr class="rule" />
    <div class="actions">
      <span class="muted small">{{ period.incomeConfirmed ? '本期薪資已確認，之後還是可以改' : '確認後這張卡就不會再出現' }}</span>
      <button type="button" class="btn primary" :disabled="busy" @click="confirm">
        {{ period.incomeConfirmed ? '完成' : period.incomeAdjustments.length ? '確認本期薪資' : '沒有變動，確認' }}
      </button>
    </div>
  </Sheet>
</template>

<style scoped>
.sum {
  display: flex;
  justify-content: space-between;
  align-items: flex-end;
  padding-bottom: 12px;
  border-bottom: 1px solid var(--line);
}
.sum .right {
  text-align: right;
}
.label {
  display: block;
  font-size: 12px;
  color: var(--muted);
}
.big {
  font-size: 28px;
  font-weight: 600;
}
.adj li {
  display: grid;
  grid-template-columns: 1fr auto auto;
  gap: 10px;
  align-items: center;
  padding: 8px 0;
  font-size: 14px;
}
.form {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.form > .btn {
  align-self: flex-start;
}
.row2 {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
}
.small {
  font-size: 12px;
}
.actions {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}
</style>
