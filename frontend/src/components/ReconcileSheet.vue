<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import Sheet from './Sheet.vue'
import { createReconciliation, getAccounts, previewReconciliation } from '../api/endpoints'
import type { AccountView, ReconciliationResult } from '../api/types'
import { loadCurrentPeriod, store } from '../lib/store'
import { logicalToday, money, shortDate, signed } from '../lib/format'

/**
 * 對帳（§12.1）：填各帳戶「今天結束時」的實際餘額（信用卡填欠多少）。
 * 系統用上次對帳 + 期間已知收支推出預期淨額，差額＝沒交代的差異：
 * 少了走超支規則（先池、再攤到之後的日子），多了進待分配池。先試算、確認後才存。
 */
const emit = defineEmits<{ close: []; saved: [] }>()

const accounts = ref<AccountView[]>([])
const values = ref<Record<number, string>>({})
// 資產頁不會載入本期資料，這時用邏輯日的今天
const today = store.period?.today ?? logicalToday()
const date = ref(today)
const usePool = ref(true)
const note = ref('')
const preview = ref<ReconciliationResult | null>(null)
const loading = ref(false)
const saving = ref(false)
const error = ref<string | null>(null)
const acknowledgedLarge = ref(false)

onMounted(async () => {
  const v = await getAccounts()
  accounts.value = v.accounts
  for (const a of v.accounts) values.value[a.id] = String(Math.round(a.balance))
})

const body = computed(() => {
  const lines = accounts.value.map((a) => ({ accountId: a.id, balance: Math.round(Number(values.value[a.id]) || 0) }))
  return { date: date.value, lines, usePool: usePool.value, note: note.value.trim() || null }
})

let timer: ReturnType<typeof setTimeout> | undefined
watch(
  body,
  (b) => {
    clearTimeout(timer)
    if (!b.lines.length || !b.date) return
    timer = setTimeout(async () => {
      loading.value = true
      try {
        preview.value = await previewReconciliation(b)
        error.value = null
      } catch (e) {
        error.value = (e as Error).message
      } finally {
        loading.value = false
      }
    }, 300)
  },
  { deep: true },
)

const net = computed(() => accounts.value.reduce((s, a) => s + (a.type === 'CreditCard' ? -1 : 1) * (Number(values.value[a.id]) || 0), 0))
const needsAck = computed(() => !!preview.value?.largeDiff && !acknowledgedLarge.value)

async function submit() {
  if (needsAck.value) return
  saving.value = true
  error.value = null
  try {
    await createReconciliation(body.value)
    await loadCurrentPeriod(true)
    emit('saved')
    emit('close')
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Sheet title="對帳" subtitle="填這天結束時各帳戶的實際餘額；信用卡填欠多少" @close="emit('close')">
    <p v-if="accounts.length === 0" class="muted">還沒有帳戶，先到資產頁新增。</p>
    <form v-else class="form" @submit.prevent="submit">
      <label class="field">
        日期
        <input v-model="date" type="date" class="input" :max="today" />
      </label>

      <div class="lines">
        <label v-for="a in accounts" :key="a.id" class="line">
          <span>
            {{ a.name }}
            <span class="tag">{{ a.type === 'CreditCard' ? '欠款' : '餘額' }}</span>
          </span>
          <input v-model="values[a.id]" class="input compact num" inputmode="numeric" :aria-label="`${a.name}實際${a.type === 'CreditCard' ? '欠款' : '餘額'}`" />
        </label>
        <div class="line total num">
          <span>淨額</span>
          <b>{{ money(net) }}</b>
        </div>
      </div>

      <label class="check">
        <input v-model="usePool" type="checkbox" />
        <span>
          差額是負的時候，先從待分配池扣
          <span class="hint">不夠或不勾，才攤到之後的日子</span>
        </span>
      </label>

      <div class="impact" :class="{ loading }" aria-live="polite">
        <template v-if="preview && !preview.hasBaseline">
          <p>這是第一次完整對帳，只當作之後比對的基準，不會產生差額。</p>
        </template>
        <template v-else-if="preview">
          <p class="num">預期淨額 {{ money(preview.expected) }}（從 {{ shortDate(preview.baselineDate!) }} 對帳推算）</p>
          <p class="num">實際淨額 {{ money(preview.actual) }}</p>
          <p class="num" :class="preview.diff < 0 ? 'bad' : preview.diff > 0 ? 'good' : ''">
            沒交代的差異 <b>{{ signed(preview.diff) }}</b>
          </p>
          <p v-if="preview.diff > 0">多出來的進待分配池。</p>
          <template v-else-if="preview.diff < 0">
            <p v-if="preview.fromPool">待分配池先扣 {{ money(preview.fromPool) }}</p>
            <p v-if="preview.spread">其餘攤到之後的日子，共 {{ money(preview.spread) }}</p>
            <p v-if="preview.unabsorbed" class="bad">後面沒有額度可攤，待分配池再扣 {{ money(preview.unabsorbed) }}</p>
          </template>
          <p class="pool num">待分配池 {{ money(preview.poolBefore) }} → <b>{{ money(preview.poolAfter) }}</b></p>
        </template>
        <p v-else class="muted">填好餘額會自動試算</p>
      </div>

      <div v-if="preview?.largeDiff" class="warn-box">
        <p><b>差額超過本期變動預算的 3 成。</b>是不是漏記了大筆花費或固定支出？</p>
        <p class="muted small">
          可以先到 <RouterLink to="/ledger" @click="emit('close')">記帳</RouterLink> 把那筆單獨記下，差額會自動變小。
        </p>
        <label class="check">
          <input v-model="acknowledgedLarge" type="checkbox" />
          <span>我確認沒有漏記，照這個差額處理</span>
        </label>
      </div>

      <label class="field">
        備註
        <input v-model="note" class="input" maxlength="120" placeholder="選填，例如 月底對帳" />
      </label>

      <p v-if="error" class="error-box">{{ error }}</p>
      <div class="actions">
        <button type="button" class="btn" @click="emit('close')">取消</button>
        <button type="submit" class="btn primary" :disabled="saving || needsAck || !preview">確認對帳</button>
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
.lines {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.line {
  display: grid;
  grid-template-columns: 1fr 140px;
  gap: 10px;
  align-items: center;
  font-size: 14px;
}
.line.total {
  border-top: 1px solid var(--line);
  padding-top: 8px;
}
.line.total b {
  text-align: right;
  font-size: 16px;
}
.impact {
  border-left: 2px solid var(--ink);
  background: var(--sunk);
  padding: 10px 12px;
  font-size: 14px;
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.impact.loading {
  opacity: 0.6;
}
.pool {
  font-size: 13px;
  color: var(--ink-2);
  border-top: 1px solid var(--line-2);
  padding-top: 6px;
  margin-top: 4px;
}
.warn-box {
  border: 1px solid var(--warn-text);
  border-radius: 4px;
  padding: 10px 12px;
  display: flex;
  flex-direction: column;
  gap: 6px;
  font-size: 14px;
}
.small {
  font-size: 12px;
}
.actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
