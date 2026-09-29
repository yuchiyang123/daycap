<script setup lang="ts">
import Skeleton from './Skeleton.vue'
import { computed, onMounted, ref, watch } from 'vue'
import { useRefreshOnReturn } from '../lib/useRefreshOnReturn'
import { createInstallment, deleteInstallment, getAccounts, getInstallments, getSettings, prepayInstallment, previewInstallment } from '../api/endpoints'
import type { AccountView, CategoryDto, CreateInstallmentRequest, InstallmentMode, InstallmentView, PrepayMode } from '../api/types'
import { loadCurrentPeriod } from '../lib/store'
import { logicalToday, money, shortDate } from '../lib/format'

/**
 * 分期（§15）：每期繳款＝這期的固定支出；剩下的本金是負債。
 * 提前還款從資產付，負債減少，不算花費、不扣預算；可選每期變少或期數變短。
 */
const emit = defineEmits<{ changed: [] }>()
const items = ref<InstallmentView[]>([])
const loaded = ref(false)
const fixedCats = ref<CategoryDto[]>([])
const accounts = ref<AccountView[]>([])
const error = ref<string | null>(null)
const busy = ref(false)
const expanded = ref<number | null>(null)
useRefreshOnReturn(async () => (items.value = await getInstallments()))

onMounted(async () => {
  items.value = await getInstallments().catch(() => [])
  loaded.value = true
  fixedCats.value = ((await getSettings().catch(() => null))?.settings.categories ?? []).filter((c) => c.mode === 'Fixed')
  accounts.value = (await getAccounts().catch(() => ({ accounts: [] as AccountView[] }))).accounts.filter((a) => a.type !== 'CreditCard')
})

const active = computed(() => items.value.filter((i) => i.remaining > 0))
const finished = computed(() => items.value.filter((i) => i.remaining === 0))
const debtTotal = computed(() => active.value.reduce((a, i) => a + i.principalRemaining, 0))

async function run(fn: () => Promise<InstallmentView[]>) {
  busy.value = true
  error.value = null
  try {
    items.value = await fn()
    await loadCurrentPeriod(true) // 這期的固定支出跟著變
    emit('changed')
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}

// ---- 新增 ----
interface Form {
  name: string
  categoryId: number
  mode: InstallmentMode
  periods: string
  first: string
  monthly: string
  principal: string
  rate: string
  fee: string
}
const form = ref<Form | null>(null)
const preview = ref<InstallmentView | null>(null)
function startCreate() {
  form.value = {
    name: '',
    categoryId: fixedCats.value[0]?.id ?? 0,
    mode: 'Simple',
    periods: '12',
    first: logicalToday(),
    monthly: '',
    principal: '',
    rate: '0',
    fee: '',
  }
}
const body = computed<CreateInstallmentRequest | null>(() => {
  const f = form.value
  if (!f || !f.name.trim() || !(Number(f.periods) >= 1) || !f.first) return null
  if (f.mode === 'Simple' && !(Number(f.monthly) > 0)) return null
  if (f.mode === 'Detailed' && !(Number(f.principal) > 0)) return null
  return {
    name: f.name.trim(),
    categoryId: f.categoryId,
    mode: f.mode,
    periods: Math.round(Number(f.periods)),
    firstDueDate: f.first,
    monthlyAmount: f.mode === 'Simple' ? Math.round(Number(f.monthly)) : null,
    principal: f.mode === 'Detailed' ? Math.round(Number(f.principal)) : null,
    annualRatePercent: f.mode === 'Detailed' ? Number(f.rate) || 0 : null,
    fee: f.mode === 'Detailed' ? Math.round(Number(f.fee) || 0) : null,
    note: null,
  }
})
let timer: ReturnType<typeof setTimeout> | undefined
watch(body, (b) => {
  clearTimeout(timer)
  if (!b || b.mode !== 'Detailed') {
    preview.value = null
    return
  }
  timer = setTimeout(async () => {
    preview.value = await previewInstallment(b).catch(() => null)
  }, 300)
})
function save() {
  const b = body.value
  if (!b) return
  form.value = null
  preview.value = null
  run(() => createInstallment(b))
}

// ---- 提前還款 ----
const prepaying = ref<{ id: number; amount: string; mode: PrepayMode; accountId: number | null } | null>(null)
function startPrepay(i: InstallmentView) {
  prepaying.value = { id: i.id, amount: '', mode: 'ReduceTerm', accountId: accounts.value[0]?.id ?? null }
}
function confirmPrepay() {
  const p = prepaying.value
  if (!p || !(Number(p.amount) > 0)) return
  prepaying.value = null
  run(() => prepayInstallment(p.id, { amount: Math.round(Number(p.amount)), mode: p.mode, accountId: p.accountId, date: null }))
}

const removing = ref<number | null>(null)
function remove(i: InstallmentView) {
  if (removing.value !== i.id) {
    removing.value = i.id
    setTimeout(() => removing.value === i.id && (removing.value = null), 3000)
    return
  }
  removing.value = null
  run(() => deleteInstallment(i.id))
}
</script>

<template>
  <div class="inst">
    <p v-if="error" class="error-box">{{ error }}</p>
    <p v-if="active.length" class="num small">剩下的本金（負債）合計 <b>{{ money(debtTotal) }}</b></p>

    <Skeleton v-if="!loaded" variant="list" :rows="2" />
    <div v-else-if="active.length" class="panel">
      <ul class="list">
        <li v-for="i in active" :key="i.id" class="item">
          <div class="i-head">
            <b>{{ i.name }}</b>
            <span class="num">{{ i.paidPayments }}/{{ i.totalPayments }} 期</span>
          </div>
          <div class="muted small num meta">
            <span v-if="i.nextDueDate">下期 {{ shortDate(i.nextDueDate) }} 繳 {{ money(i.nextPayment ?? 0) }}</span>
            <span>剩本金 {{ money(i.principalRemaining) }}</span>
            <span>還要繳 {{ money(i.remaining) }}</span>
            <span v-if="i.costTotal">利息＋手續費 {{ money(i.costTotal) }}</span>
          </div>
          <div v-if="prepaying?.id === i.id" class="prepay">
            <input v-model="prepaying.amount" class="input num" inputmode="numeric" placeholder="提前還多少" aria-label="提前還款金額" />
            <select v-model="prepaying.mode" class="select" aria-label="提前還款方式">
              <option value="ReduceTerm">期數變短（每期金額不變）</option>
              <option value="ReduceAmount">每期變少（期數不變）</option>
            </select>
            <select v-model="prepaying.accountId" class="select" aria-label="從哪個帳戶">
              <option :value="null">不指定帳戶</option>
              <option v-for="a in accounts" :key="a.id" :value="a.id">{{ a.name }}</option>
            </select>
            <button type="button" class="btn sm primary" :disabled="busy" @click="confirmPrepay">還款</button>
            <button type="button" class="btn sm quiet" @click="prepaying = null">取消</button>
          </div>
          <div v-else class="actions">
            <button type="button" class="btn sm" :disabled="busy" @click="startPrepay(i)">提前還款</button>
            <button type="button" class="btn sm quiet" @click="expanded = expanded === i.id ? null : i.id">{{ expanded === i.id ? '收起繳款表' : '繳款表' }}</button>
            <button type="button" class="btn sm quiet danger" :disabled="busy" @click="remove(i)">{{ removing === i.id ? '確認刪除' : '刪除' }}</button>
          </div>
          <div v-if="expanded === i.id" class="table-wrap">
            <table class="tbl sched num">
              <thead>
                <tr><th>期</th><th>日期</th><th class="r">繳款</th><th class="r">本金</th><th class="r">利息</th><th class="r">手續費</th><th class="r">剩本金</th></tr>
              </thead>
              <tbody>
                <tr v-for="r in i.schedule" :key="r.index">
                  <td>{{ r.index }}</td>
                  <td>{{ shortDate(r.dueDate) }}</td>
                  <td class="r">{{ money(r.payment) }}</td>
                  <td class="r">{{ money(r.principal) }}</td>
                  <td class="r">{{ money(r.interest) }}</td>
                  <td class="r">{{ money(r.fee) }}</td>
                  <td class="r">{{ money(r.balanceAfter) }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </li>
      </ul>
    </div>
    <p v-else-if="!form" class="muted small">沒有進行中的分期。手機、筆電分期買的，記在這裡，每期會自動變成那期的固定支出。</p>

    <div v-if="form" class="panel form">
      <div class="seg" role="radiogroup">
        <button type="button" :class="{ on: form.mode === 'Simple' }" @click="form.mode = 'Simple'">簡易：每月 X 元共 N 期</button>
        <button type="button" :class="{ on: form.mode === 'Detailed' }" @click="form.mode = 'Detailed'">細部：本金、利率、手續費</button>
      </div>
      <div class="two">
        <label class="field">名稱<input v-model="form.name" class="input" maxlength="40" placeholder="例如 手機" /></label>
        <label class="field">
          算在哪個固定類別
          <select v-model="form.categoryId" class="select">
            <option v-for="c in fixedCats" :key="c.id" :value="c.id">{{ c.name }}</option>
          </select>
        </label>
      </div>
      <div class="two">
        <label v-if="form.mode === 'Simple'" class="field">每期金額<input v-model="form.monthly" class="input num" inputmode="numeric" /></label>
        <label v-else class="field">本金<input v-model="form.principal" class="input num" inputmode="numeric" /></label>
        <label class="field">期數<input v-model="form.periods" class="input num" inputmode="numeric" /></label>
      </div>
      <div v-if="form.mode === 'Detailed'" class="two">
        <label class="field">年利率 %<input v-model="form.rate" class="input num" inputmode="decimal" /></label>
        <label class="field">手續費總額<input v-model="form.fee" class="input num" inputmode="numeric" placeholder="0 利率含手續費就填這裡" /></label>
      </div>
      <label class="field">第一期繳款日<input v-model="form.first" type="date" class="input" /></label>
      <p v-if="preview" class="muted small num">
        每期約 {{ money(preview.schedule[0]?.payment ?? 0) }}，共 {{ preview.totalPayments }} 期，利息＋手續費 {{ money(preview.costTotal) }}
      </p>
      <div class="form-actions">
        <button type="button" class="btn" @click="form = null">取消</button>
        <button type="button" class="btn primary" :disabled="busy || !body" @click="save">建立</button>
      </div>
    </div>
    <button v-else type="button" class="btn sm" :disabled="!fixedCats.length" @click="startCreate">新增分期</button>
    <p v-if="!fixedCats.length" class="muted small">要先在設定裡有一個「固定」類別。</p>

    <p v-if="finished.length" class="muted small">已繳完：{{ finished.map((i) => i.name).join('、') }}</p>
  </div>
</template>

<style scoped>
.inst {
  display: flex;
  flex-direction: column;
  gap: 10px;
  align-items: flex-start;
}
.inst > .panel {
  width: 100%;
}
.small {
  font-size: 12px;
}
.item {
  padding: 10px 16px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.i-head {
  display: flex;
  justify-content: space-between;
  gap: 8px;
}
.meta {
  display: flex;
  gap: 12px;
  flex-wrap: wrap;
}
.actions,
.prepay {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
  align-items: center;
}
.prepay .input {
  width: 120px;
}
.prepay .select {
  width: auto;
  max-width: 220px;
}
.sched {
  font-size: 12px;
}
.form {
  padding: 14px 16px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.two {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
}
.seg {
  display: grid;
  grid-template-columns: 1fr 1fr;
  border: 1px solid var(--line);
  border-radius: 4px;
  overflow: hidden;
}
.seg button {
  padding: 8px 4px;
  background: none;
  border: 0;
  font: inherit;
  font-size: 13px;
  color: inherit;
  cursor: pointer;
}
.seg button + button {
  border-left: 1px solid var(--line);
}
.seg button.on {
  background: var(--ink);
  color: var(--surface);
}
.form-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
@media (max-width: 420px) {
  .two {
    grid-template-columns: 1fr;
  }
}
</style>
