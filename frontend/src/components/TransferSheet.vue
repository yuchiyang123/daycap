<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import Sheet from './Sheet.vue'
import { addTransfer2, getAccounts } from '../api/endpoints'
import type { AccountView, TransferKind } from '../api/types'
import { store } from '../lib/store'
import { money, logicalToday } from '../lib/format'

/** 帳戶互轉、繳卡費（§5）：都不算花費，淨資產不變。 */
const emit = defineEmits<{ close: []; saved: [] }>()

const accounts = ref<AccountView[]>([])
const kind = ref<TransferKind>('Transfer')
const fromId = ref(0)
const toId = ref(0)
const amount = ref('')
const note = ref('')
const date = ref(store.period?.today ?? logicalToday())
const busy = ref(false)
const error = ref<string | null>(null)

onMounted(async () => {
  accounts.value = (await getAccounts()).accounts
  fromId.value = assets.value[0]?.id ?? 0
  pickTo()
})

const assets = computed(() => accounts.value.filter((a) => a.type !== 'CreditCard'))
const cards = computed(() => accounts.value.filter((a) => a.type === 'CreditCard'))
const toChoices = computed(() => (kind.value === 'CardPayment' ? cards.value : accounts.value.filter((a) => a.id !== fromId.value)))

function pickTo() {
  if (!toChoices.value.some((a) => a.id === toId.value)) toId.value = toChoices.value[0]?.id ?? 0
}
watch([kind, fromId], pickTo)

/** 繳卡費預設帶入這張卡目前的欠款 */
watch([kind, toId], () => {
  if (kind.value === 'CardPayment' && !amount.value) {
    const c = cards.value.find((a) => a.id === toId.value)
    if (c && c.balance > 0) amount.value = String(Math.round(c.balance))
  }
})

const n = computed(() => Math.round(Number(amount.value) || 0))

async function submit() {
  if (n.value <= 0 || !fromId.value || !toId.value) return
  busy.value = true
  error.value = null
  try {
    await addTransfer2({ date: date.value, kind: kind.value, fromAccountId: fromId.value, toAccountId: toId.value, amount: n.value, note: note.value.trim() || null })
    emit('saved')
    emit('close')
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Sheet title="轉帳・繳卡費" subtitle="帳戶之間的移動，不算花費" @close="emit('close')">
    <p v-if="accounts.length < 2" class="muted">至少要有兩個帳戶。</p>
    <form v-else class="form" @submit.prevent="submit">
      <div class="seg" role="group" aria-label="種類">
        <button type="button" :aria-pressed="kind === 'Transfer'" @click="kind = 'Transfer'">互轉</button>
        <button type="button" :aria-pressed="kind === 'CardPayment'" :disabled="!cards.length" @click="kind = 'CardPayment'">繳卡費</button>
      </div>
      <div class="row2">
        <label class="field">
          從
          <select v-model.number="fromId" class="select">
            <option v-for="a in assets" :key="a.id" :value="a.id">{{ a.name }}（{{ money(a.balance) }}）</option>
          </select>
        </label>
        <label class="field">
          到
          <select v-model.number="toId" class="select">
            <option v-for="a in toChoices" :key="a.id" :value="a.id">
              {{ a.name }}{{ a.type === 'CreditCard' ? `（欠 ${money(a.balance)}）` : '' }}
            </option>
          </select>
        </label>
      </div>
      <div class="row2">
        <label class="field">
          金額
          <input v-model="amount" class="input num" inputmode="numeric" placeholder="0" />
        </label>
        <label class="field">
          日期
          <input v-model="date" type="date" class="input" />
        </label>
      </div>
      <label class="field">
        備註
        <input v-model="note" class="input" maxlength="120" placeholder="選填" />
      </label>
      <p v-if="error" class="error-box">{{ error }}</p>
      <div class="actions">
        <button type="button" class="btn" @click="emit('close')">取消</button>
        <button type="submit" class="btn primary" :disabled="n <= 0 || busy">記下</button>
      </div>
    </form>
  </Sheet>
</template>

<style scoped>
.form {
  display: flex;
  flex-direction: column;
  gap: 14px;
}
.seg {
  align-self: flex-start;
}
.row2 {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
}
.actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
