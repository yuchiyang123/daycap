<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import Sheet from './Sheet.vue'
import { createLivingCheck, previewLivingCheck } from '../api/endpoints'
import type { ReconciliationResult } from '../api/types'
import { loadCurrentPeriod, store } from '../lib/store'
import { logicalToday, money, shortDate, signed } from '../lib/format'

/**
 * 生活錢對帳：薪轉戶、生活帳戶這些沒登記的帳戶加總，扣掉信用卡已刷未繳 = 實際剩下的生活錢。
 * 「應該剩」：有上一次就從上一次的實際推，第一次就從這期的記帳推（上期結轉 + 薪水 − 到現在記的花費）。
 * 差額：多了進待分配池，少了（漏記）先從池子扣。先試算、確認才存。
 */
const emit = defineEmits<{ close: []; saved: [] }>()

const today = store.period?.today ?? logicalToday()
const date = ref(today)
const accounts = ref('')
const card = ref('')
const usePool = ref(true)
const note = ref('')
const preview = ref<ReconciliationResult | null>(null)
const loading = ref(false)
const saving = ref(false)
const error = ref<string | null>(null)
const acknowledgedLarge = ref(false)

const num = (s: string) => {
  const n = Number(s.replace(/,/g, '').trim())
  return s.trim() !== '' && Number.isFinite(n) && n >= 0 ? Math.round(n) : null
}
const body = computed(() => {
  const a = num(accounts.value)
  const c = card.value.trim() === '' ? 0 : num(card.value)
  if (a === null || c === null || !date.value) return null
  return { date: date.value, accountsTotal: a, unpaidCard: c, usePool: usePool.value, note: note.value.trim() || null }
})
const net = computed(() => (body.value ? body.value.accountsTotal - body.value.unpaidCard : null))

let timer: ReturnType<typeof setTimeout> | undefined
watch(
  body,
  (b) => {
    clearTimeout(timer)
    if (!b) {
      preview.value = null
      return
    }
    timer = setTimeout(async () => {
      loading.value = true
      try {
        preview.value = await previewLivingCheck(b)
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

const needsAck = computed(() => !!preview.value?.largeDiff && !acknowledgedLarge.value)

async function submit() {
  if (!body.value || needsAck.value) return
  saving.value = true
  error.value = null
  try {
    await createLivingCheck(body.value)
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
  <Sheet title="生活錢對帳" subtitle="對到記帳算出來「應該剩」的錢" @close="emit('close')">
    <form class="form" @submit.prevent="submit">
      <label class="field">
        日期
        <input v-model="date" type="date" class="input" :max="today" />
      </label>

      <div class="lines">
        <label class="line">
          <span>
            沒登記的帳戶加起來
            <span class="hint">薪轉戶、生活帳戶、現金、電子支付</span>
          </span>
          <input v-model="accounts" class="input compact num" inputmode="numeric" placeholder="0" aria-label="沒登記的帳戶加總" autofocus />
        </label>
        <label class="line">
          <span>
            信用卡已刷、還沒繳
            <span class="hint">已出帳未繳＋未出帳，都要算</span>
          </span>
          <input v-model="card" class="input compact num" inputmode="numeric" placeholder="0" aria-label="信用卡已刷未繳" />
        </label>
        <div class="line total num">
          <span>實際剩</span>
          <b>{{ net === null ? '—' : money(net) }}</b>
        </div>
      </div>

      <label class="check">
        <input v-model="usePool" type="checkbox" />
        <span>
          少了的時候，先從待分配池扣
          <span class="hint">不夠或不勾，才攤到之後的日子</span>
        </span>
      </label>

      <div class="impact" :class="{ loading }" aria-live="polite">
        <template v-if="preview && !preview.hasBaseline">
          <p>這個日期不在任何一期裡，只記下金額，不算差額。</p>
        </template>
        <template v-else-if="preview">
          <p class="num">
            應該剩 {{ money(preview.expected) }}
            <span class="muted small">{{ preview.baselineDate ? `（從 ${shortDate(preview.baselineDate)} 的對帳推算）` : '（這期的薪水 − 到現在記的花費）' }}</span>
          </p>
          <p class="num">實際剩 {{ money(preview.actual) }}</p>
          <p class="num" :class="preview.diff < 0 ? 'bad' : preview.diff > 0 ? 'good' : ''">
            差 <b>{{ signed(preview.diff) }}</b>
            <span v-if="preview.diff === 0" class="muted">・記帳跟實際對得上</span>
          </p>
          <template v-if="preview.diff < 0">
            <p class="muted small">少了：可能有現金、轉帳或刷卡沒記到。想補記的話先到記帳補，差額會跟著變小。</p>
            <p v-if="preview.fromPool">待分配池先扣 {{ money(preview.fromPool) }}</p>
            <p v-if="preview.spread">其餘攤到之後的日子，共 {{ money(preview.spread) }}</p>
            <p v-if="preview.unabsorbed" class="bad">後面沒有額度可攤，待分配池再扣 {{ money(preview.unabsorbed) }}</p>
          </template>
          <p v-else-if="preview.diff > 0" class="muted small">多了：可能有回饋、退款、別人還錢，或多記了。多出來的進待分配池。</p>
          <p class="pool num">待分配池 {{ money(preview.poolBefore) }} → <b>{{ money(preview.poolAfter) }}</b></p>
        </template>
        <p v-else class="muted">填好金額會自動試算</p>
      </div>

      <div v-if="preview?.largeDiff" class="warn-box">
        <p><b>差額超過本期變動預算的 3 成。</b>是不是漏記了大筆花費，或卡費金額填錯？</p>
        <p class="muted small">
          可以先到 <RouterLink to="/ledger" @click="emit('close')">記帳</RouterLink> 把那筆記下，差額會自動變小。
        </p>
        <label class="check">
          <input v-model="acknowledgedLarge" type="checkbox" />
          <span>我確認沒有漏記，照這個差額處理</span>
        </label>
      </div>

      <label class="field">
        備註
        <input v-model="note" class="input" maxlength="120" placeholder="選填，例如 繳完卡費" />
      </label>

      <p v-if="error" class="error-box">{{ error }}</p>
      <div class="actions">
        <button type="button" class="btn" @click="emit('close')">取消</button>
        <button type="submit" class="btn primary" :disabled="saving || needsAck || !preview || !body">確認對帳</button>
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
  gap: 8px;
}
.line {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 140px;
  gap: 10px;
  align-items: center;
  font-size: 14px;
}
.line .hint {
  display: block;
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
.small {
  font-size: 12px;
}
.actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
