<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { closeMonth, getAccounts, getMonthEnd } from '../api/endpoints'
import type { AccountView, MonthEndReport, ShortfallChoice } from '../api/types'
import { loadCurrentPeriod } from '../lib/store'
import { money, shortDate, signed } from '../lib/format'

/**
 * 發薪日（新的一期在「一天從幾點開始」那一刻開始）上一期還沒月結時，強制先決定上期結果怎麼處理：
 * 結餘 → 帶進這期的待分配池，或轉進存款；超支 → 這期扣、分兩期、或從存款吸收。
 * 不能關掉，選完才放行。要調下期時段金額或先對帳，可以進完整的月結頁。
 */
const props = defineProps<{ periodId: number }>()

const report = ref<MonthEndReport | null>(null)
const accounts = ref<AccountView[]>([])
const decision = ref<ShortfallChoice | null>(null)
const accountId = ref<number | null>(null)
const saving = ref(false)
const error = ref<string | null>(null)

async function load() {
  error.value = null
  decision.value = null
  try {
    const [r, a] = await Promise.all([getMonthEnd(props.periodId), getAccounts().catch(() => ({ accounts: [] as AccountView[] }))])
    report.value = r
    accounts.value = a.accounts.filter((x) => x.type !== 'CreditCard')
    accountId.value = accounts.value[0]?.id ?? null
  } catch (e) {
    error.value = (e as Error).message
  }
}
onMounted(() => {
  void load()
  document.body.style.overflow = 'hidden'
})
onBeforeUnmount(() => (document.body.style.overflow = ''))
watch(() => props.periodId, load)

const surplus = computed(() => (report.value?.result ?? 0) >= 0)
const options = computed<{ v: ShortfallChoice; label: string; hint: string }[]>(() =>
  surplus.value
    ? [
        { v: 'Pool', label: '帶進這期的待分配池', hint: '這期期初的待分配池多這筆，可以分給分類用' },
        { v: 'Savings', label: '轉進存款', hint: '記成帳戶的一筆存入，記得實際把錢轉過去' },
      ]
    : [
        { v: 'Pool', label: '這期期初扣掉', hint: '這期待分配池少這筆' },
        { v: 'Split', label: '分兩期還', hint: '這期、下一期各扣一半' },
        { v: 'Savings', label: '從存款吸收', hint: '從帳戶扣掉，這期不受影響' },
      ],
)
const ready = computed(() => !!decision.value && (decision.value !== 'Savings' || !!accountId.value))

async function submit() {
  if (!ready.value || !report.value) return
  saving.value = true
  error.value = null
  try {
    await closeMonth(props.periodId, {
      decision: decision.value!,
      accountId: decision.value === 'Savings' ? accountId.value : null,
      slotChanges: [],
    })
    await loadCurrentPeriod(true) // previousPeriodNeedsClosing 變成 null，這個視窗就消失
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="gate-backdrop">
    <section class="gate-box" role="dialog" aria-modal="true" aria-label="上一期結算">
      <header>
        <h2>上一期結算</h2>
        <p v-if="report" class="sub num">{{ shortDate(report.startDate) }} – {{ shortDate(report.endDate) }}・新的一期開始了，先決定上期剩下的錢</p>
      </header>

      <p v-if="error" class="error-box">{{ error }}</p>

      <template v-if="report">
        <div class="result">
          <span class="label">{{ surplus ? '上期結餘' : '上期超支' }}</span>
          <span class="big num" :class="report.result > 0 ? 'good' : report.result < 0 ? 'bad' : ''">{{ signed(report.result) }}</span>
          <span v-if="report.envelopeLeftovers?.length" class="sub num">
            待分配池 {{ money(report.poolBalance ?? 0) }}
            <template v-for="x in report.envelopeLeftovers" :key="x.categoryId"> ＋ {{ x.name }}沒花完 {{ money(x.amount) }}</template>
          </span>
        </div>

        <template v-if="report.canClose">
          <fieldset class="choices">
            <legend>{{ surplus ? '結餘要放哪裡？' : '超支怎麼補？' }}</legend>
            <label v-for="o in options" :key="o.v" class="choice" :class="{ on: decision === o.v }">
              <input v-model="decision" type="radio" :value="o.v" name="gate-decision" />
              <span>
                <b>{{ o.label }}</b>
                <span class="hint">{{ o.hint }}</span>
              </span>
            </label>
          </fieldset>
          <label v-if="decision === 'Savings'" class="field">
            帳戶
            <select v-model="accountId" class="select">
              <option v-for="a in accounts" :key="a.id" :value="a.id">{{ a.name }}（{{ money(a.balance) }}）</option>
            </select>
            <span v-if="accounts.length === 0" class="bad small">還沒有登記存款帳戶，先到資產頁新增。</span>
          </label>

          <div class="actions">
            <RouterLink :to="`/month-end/${periodId}`" class="btn quiet sm">完整月結（調下期時段金額）</RouterLink>
            <button type="button" class="btn primary" :disabled="!ready || saving" @click="submit">
              {{ saving ? '處理中…' : '確定' }}
            </button>
          </div>
        </template>
        <template v-else>
          <p class="muted">{{ report.cannotCloseReason }}</p>
          <div class="actions">
            <RouterLink :to="`/month-end/${periodId}`" class="btn primary">去月結</RouterLink>
          </div>
        </template>
      </template>
      <p v-else-if="!error" class="muted">讀取中…</p>
    </section>
  </div>
</template>

<style scoped>
.gate-backdrop {
  position: fixed;
  inset: 0;
  z-index: 60;
  background: color-mix(in srgb, var(--page) 70%, transparent);
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 16px;
}
.gate-box {
  width: min(460px, 100%);
  max-height: calc(100dvh - 32px);
  overflow-y: auto;
  background: var(--surface);
  border: 1px solid var(--line-2);
  border-radius: var(--radius);
  padding: 18px 18px 16px;
  display: flex;
  flex-direction: column;
  gap: 14px;
}
header h2 {
  margin: 0;
  font-size: 18px;
}
.sub {
  margin: 4px 0 0;
  font-size: 13px;
  color: var(--muted);
}
.result {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.label {
  font-size: 12px;
  color: var(--muted);
}
.big {
  font-size: 30px;
  font-weight: 700;
}
.choices {
  border: 0;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 8px;
}
legend {
  font-weight: 600;
  margin-bottom: 6px;
  padding: 0;
}
.choice {
  display: flex;
  gap: 10px;
  align-items: flex-start;
  border: 1px solid var(--line);
  border-radius: var(--radius);
  padding: 10px 12px;
  cursor: pointer;
}
.choice.on {
  border-color: var(--accent);
}
.choice input {
  margin-top: 3px;
}
.choice .hint {
  display: block;
  font-size: 12px;
  color: var(--muted);
}
.field {
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
  justify-content: space-between;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
</style>
