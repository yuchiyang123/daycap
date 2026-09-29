<script setup lang="ts">
import Skeleton from './Skeleton.vue'
import { computed, onMounted, ref } from 'vue'
import { useRefreshOnReturn } from '../lib/useRefreshOnReturn'
import { createDebt, deleteDebt, getAccounts, getDebts, settleDebt } from '../api/endpoints'
import type { AccountView, DebtKind, DebtsView, DebtView } from '../api/types'
import { money, shortDate } from '../lib/format'

/**
 * 應收應付（§14）：代墊的錢是應收（資產），別人先幫我付的是應付（負債）。
 * 收回 / 還錢只動帳戶，不影響預算；對帳時會算進已知收支。
 */
const emit = defineEmits<{ changed: [] }>()
const data = ref<DebtsView | null>(null)
const accounts = ref<AccountView[]>([])
const error = ref<string | null>(null)
const busy = ref(false)
const showDone = ref(false)

async function load() {
  data.value = await getDebts().catch(() => null)
}
useRefreshOnReturn(load)
onMounted(async () => {
  await load()
  accounts.value = (await getAccounts().catch(() => ({ accounts: [] as AccountView[] }))).accounts
})

const open = computed(() => (data.value?.debts ?? []).filter((d) => d.outstanding !== 0))
const done = computed(() => (data.value?.debts ?? []).filter((d) => d.outstanding === 0))

async function run(fn: () => Promise<DebtsView>) {
  busy.value = true
  error.value = null
  try {
    data.value = await fn()
    emit('changed') // 帳戶餘額跟著變
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}

// ---- 收回 / 還錢 ----
const settling = ref<{ id: number; amount: string; accountId: number | null } | null>(null)
function startSettle(d: DebtView) {
  settling.value = { id: d.id, amount: String(d.outstanding), accountId: accounts.value.find((a) => a.type !== 'CreditCard')?.id ?? null }
}
function confirmSettle() {
  const s = settling.value
  if (!s) return
  const n = Math.round(Number(s.amount))
  if (!(n > 0)) return
  settling.value = null
  run(() => settleDebt(s.id, { amount: n, accountId: s.accountId, date: null }))
}

// ---- 手動記一筆借貸 ----
const adding = ref<{ kind: DebtKind; who: string; amount: string; accountId: number | null; note: string } | null>(null)
function startAdd(kind: DebtKind) {
  adding.value = { kind, who: '', amount: '', accountId: accounts.value.find((a) => a.type !== 'CreditCard')?.id ?? null, note: '' }
}
function confirmAdd() {
  const a = adding.value
  if (!a || !a.who.trim() || !(Number(a.amount) > 0)) return
  adding.value = null
  run(() =>
    createDebt({
      kind: a.kind,
      counterparty: a.who.trim(),
      amount: Math.round(Number(a.amount)),
      date: null,
      accountId: a.kind === 'Receivable' ? a.accountId : null,
      note: a.note.trim() || null,
    }),
  )
}

const removing = ref<number | null>(null)
function remove(d: DebtView) {
  if (removing.value !== d.id) {
    removing.value = d.id
    setTimeout(() => removing.value === d.id && (removing.value = null), 3000)
    return
  }
  removing.value = null
  run(() => deleteDebt(d.id))
}
</script>

<template>
  <div class="debts">
    <p v-if="error" class="error-box">{{ error }}</p>

    <div v-if="data && (data.receivableTotal || data.payableTotal)" class="totals num">
      <span>別人欠我 <b class="good">{{ money(data.receivableTotal) }}</b></span>
      <span>我欠別人 <b class="bad">{{ money(data.payableTotal) }}</b></span>
    </div>

    <Skeleton v-if="!data" variant="list" :rows="2" />
    <div v-else-if="open.length" class="panel">
      <ul class="list">
        <li v-for="d in open" :key="d.id" class="debt">
          <div class="d-main">
            <span>
              <b>{{ d.counterparty }}</b>
              <span class="muted small">{{ d.kind === 'Receivable' ? '要還我' : '我要還' }}・{{ shortDate(d.date) }}<template v-if="d.note">・{{ d.note }}</template></span>
            </span>
            <span class="num" :class="d.kind === 'Receivable' ? 'good' : 'bad'">
              {{ money(d.outstanding) }}<span v-if="d.settled" class="muted small">（原 {{ money(d.amount) }}）</span>
            </span>
          </div>
          <div v-if="settling?.id === d.id" class="settle">
            <input v-model="settling.amount" class="input num" inputmode="numeric" aria-label="金額" />
            <select v-model="settling.accountId" class="select" aria-label="帳戶">
              <option :value="null">不指定帳戶</option>
              <option v-for="a in accounts" :key="a.id" :value="a.id">{{ a.name }}</option>
            </select>
            <button type="button" class="btn sm primary" :disabled="busy" @click="confirmSettle">{{ d.kind === 'Receivable' ? '收到了' : '還了' }}</button>
            <button type="button" class="btn sm quiet" @click="settling = null">取消</button>
          </div>
          <div v-else class="d-actions">
            <button type="button" class="btn sm" :disabled="busy" @click="startSettle(d)">{{ d.kind === 'Receivable' ? '收到還款' : '還錢' }}</button>
            <button v-if="d.sourceEntryId === null" type="button" class="btn sm quiet danger" :disabled="busy" @click="remove(d)">
              {{ removing === d.id ? '確認刪除' : '刪除' }}
            </button>
          </div>
        </li>
      </ul>
    </div>
    <p v-else class="muted small">沒有未結清的應收應付。回報花費時勾「和別人分帳」，或在這裡記一筆借出 / 借入。</p>

    <div v-if="adding" class="panel add">
      <b>{{ adding.kind === 'Receivable' ? '借出（別人欠我）' : '借入（我欠別人）' }}</b>
      <div class="two">
        <label class="field">對象<input v-model="adding.who" class="input" maxlength="40" /></label>
        <label class="field">金額<input v-model="adding.amount" class="input num" inputmode="numeric" /></label>
      </div>
      <label v-if="adding.kind === 'Receivable'" class="field">
        錢從哪個帳戶出去
        <select v-model="adding.accountId" class="select">
          <option :value="null">不指定</option>
          <option v-for="a in accounts" :key="a.id" :value="a.id">{{ a.name }}</option>
        </select>
      </label>
      <label class="field">備註（選填）<input v-model="adding.note" class="input" maxlength="120" /></label>
      <div class="add-actions">
        <button type="button" class="btn" @click="adding = null">取消</button>
        <button type="button" class="btn primary" :disabled="busy || !adding.who.trim() || !(Number(adding.amount) > 0)" @click="confirmAdd">記下</button>
      </div>
    </div>
    <div v-else class="d-actions">
      <button type="button" class="btn sm" @click="startAdd('Receivable')">記一筆借出</button>
      <button type="button" class="btn sm" @click="startAdd('Payable')">記一筆借入</button>
    </div>

    <template v-if="done.length">
      <button type="button" class="btn quiet sm" @click="showDone = !showDone">{{ showDone ? '收起' : `已結清的 ${done.length} 筆` }}</button>
      <ul v-if="showDone" class="done muted small">
        <li v-for="d in done" :key="d.id">{{ shortDate(d.date) }}・{{ d.counterparty }}・{{ d.kind === 'Receivable' ? '應收' : '應付' }} {{ money(d.amount) }}</li>
      </ul>
    </template>
  </div>
</template>

<style scoped>
.debts {
  display: flex;
  flex-direction: column;
  gap: 10px;
  align-items: flex-start;
}
.debts > .panel {
  width: 100%;
}
.totals {
  display: flex;
  gap: 20px;
  flex-wrap: wrap;
  font-size: 14px;
}
.debt {
  padding: 10px 16px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.d-main {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}
.d-main .small {
  margin-left: 6px;
}
.small {
  font-size: 12px;
}
.d-actions,
.settle {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
  align-items: center;
}
.settle .input {
  width: 110px;
}
.settle .select {
  width: auto;
  max-width: 160px;
}
.add {
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
.add-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
.done {
  margin: 0;
  padding-left: 18px;
}
@media (max-width: 420px) {
  .two {
    grid-template-columns: 1fr;
  }
}
</style>
