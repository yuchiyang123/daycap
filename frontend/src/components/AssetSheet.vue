<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { RouterLink } from 'vue-router'
import Sheet from './Sheet.vue'
import { addAssetAdjustment, getAssets } from '../api/endpoints'
import type { CashAccountDto } from '../api/types'
import { money, toIso } from '../lib/format'

/**
 * 直接加減存款帳戶，不經過任何預算分類：例如拿存款買大東西、收到紅包、退稅。
 * 帳戶餘額會立刻跟著變，在記帳頁刪掉這筆就還原。
 */
const emit = defineEmits<{ close: []; saved: [] }>()

const accounts = ref<CashAccountDto[]>([])
const accountId = ref<number>(0)
const dir = ref<'out' | 'in'>('out')
const amount = ref('')
const note = ref('')
const date = ref(toIso(new Date()))
const busy = ref(false)
const error = ref<string | null>(null)
const loaded = ref(false)

onMounted(async () => {
  accounts.value = (await getAssets()).cashAccounts
  accountId.value = accounts.value[0]?.id ?? 0
  loaded.value = true
})

const account = computed(() => accounts.value.find((a) => a.id === accountId.value))
const n = computed(() => Math.round(Number(amount.value) || 0))

async function submit() {
  if (n.value <= 0 || !accountId.value) return
  busy.value = true
  error.value = null
  try {
    await addAssetAdjustment({ cashAccountId: accountId.value, date: date.value, amount: dir.value === 'out' ? -n.value : n.value, note: note.value.trim() || null })
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
  <Sheet title="資產加減" subtitle="直接動存款帳戶，不影響任何預算分類" @close="emit('close')">
    <p v-if="loaded && accounts.length === 0" class="muted">
      還沒有存款帳戶。先到 <RouterLink to="/assets">資產頁</RouterLink> 按「編輯」新增一個。
    </p>
    <form v-else class="form" @submit.prevent="submit">
      <div class="seg" role="group" aria-label="方向">
        <button type="button" :aria-pressed="dir === 'out'" @click="dir = 'out'">支出（減少）</button>
        <button type="button" :aria-pressed="dir === 'in'" @click="dir = 'in'">收入（增加）</button>
      </div>
      <div class="row2">
        <label class="field">
          帳戶
          <select v-model.number="accountId" class="select">
            <option v-for="a in accounts" :key="a.id" :value="a.id">{{ a.name }}</option>
          </select>
        </label>
        <label class="field">
          金額
          <input v-model="amount" class="input num" inputmode="numeric" placeholder="0" autofocus />
        </label>
      </div>
      <p v-if="account" class="muted small num">
        {{ account.name }}：{{ money(account.balance) }} →
        <b>{{ money(account.balance + (dir === 'out' ? -n : n)) }}</b>
      </p>
      <div class="row2">
        <label class="field">
          日期
          <input v-model="date" type="date" class="input" />
        </label>
        <label class="field">
          備註
          <input v-model="note" class="input" maxlength="120" placeholder="例如 用存款買 PSP" />
        </label>
      </div>
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
.small {
  font-size: 12px;
  margin-top: -6px;
}
.actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
