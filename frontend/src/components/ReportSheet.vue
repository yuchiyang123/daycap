<script setup lang="ts">
import { computed, ref } from 'vue'
import Sheet from './Sheet.vue'
import ImpactPreview from './ImpactPreview.vue'
import ShortfallPicker from './ShortfallPicker.vue'
import SplitField from './SplitField.vue'
import type { ShortfallChoice, SplitRequest } from '../api/types'
import { createEntry, deleteEntry, getAccounts } from '../api/endpoints'
import type { AccountView, CreateEntryRequest, SlotView } from '../api/types'
import { onMounted } from 'vue'
import { store, setPeriod } from '../lib/store'
import { dayLabel, money } from '../lib/format'
import { usePreview } from '../lib/usePreview'

/**
 * 回報某天某個時段（例如今天早餐）。預設勾「輸入的是實際價格」；取消勾選就改成輸入「超支多少」。
 * 超支時預設勾「先從待分配池扣」，待分配池不夠或不勾，才攤到之後的日子。
 */
const props = defineProps<{ date: string; slot: SlotView; categoryName: string }>()
const emit = defineEmits<{ close: [] }>()

const period = computed(() => store.period!)
const existing = computed(() => period.value.entries.find((e) => e.id === props.slot.entryId) ?? null)

const actualMode = ref(existing.value ? existing.value.inputMode === 'Actual' : true)
const amount = ref<string>(existing.value ? String(existing.value.inputAmount) : '')
const usePool = ref(existing.value ? existing.value.usePool : true)
const note = ref(existing.value?.note ?? '')
const saving = ref(false)
const error = ref<string | null>(null)
const payAccounts = ref<AccountView[]>([])
const accountId = ref<number | null>(null)
const split = ref<SplitRequest | null>(null)

// ---- 逐筆加：同一個時段分好幾次買（飲料早上 16、中午 20、晚上 35），每加一筆就先存、先扣 ----
const mode = ref<'once' | 'parts'>(existing.value?.parts?.length ? 'parts' : 'once')
const parts = ref<number[]>([...(existing.value?.parts ?? [])])
const partInput = ref('')
const partValue = computed(() => {
  const n = Number(partInput.value.trim())
  return partInput.value.trim() !== '' && Number.isFinite(n) && n >= 0 ? Math.round(n) : null
})
const partsTotal = computed(() => parts.value.reduce((a, b) => a + b, 0))
const addedMsg = ref<string | null>(null)
function switchMode(m: 'once' | 'parts') {
  mode.value = m
  // 從一次輸入切過來：原本的金額當第一筆
  if (m === 'parts' && parts.value.length === 0 && existing.value && existing.value.inputMode === 'Actual' && existing.value.inputAmount > 0)
    parts.value = [existing.value.inputAmount]
}
async function saveParts(next: number[], msg: string) {
  saving.value = true
  error.value = null
  try {
    if (next.length === 0) {
      // 全部刪掉＝回到沒回報（照預算）
      if (existing.value) setPeriod(await deleteEntry(period.value.id, existing.value.id))
    } else {
      setPeriod(
        await createEntry(period.value.id, {
          date: props.date,
          categoryId: props.slot.categoryId,
          slotId: props.slot.slotId,
          inputMode: 'Actual',
          amount: next.reduce((a, b) => a + b, 0),
          usePool: usePool.value,
          note: note.value.trim() || null,
          subscription: null,
          accountId: accountId.value,
          parts: next,
          ...shortfallExtra(),
        }),
      )
    }
    parts.value = next
    partInput.value = ''
    addedMsg.value = msg
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    saving.value = false
  }
}
const addPart = () => partValue.value !== null && saveParts([...parts.value, partValue.value], `已加 ${money(partValue.value)}，扣好了`)
const removePart = (i: number) => saveParts(parts.value.filter((_, k) => k !== i), `已刪掉 ${money(parts.value[i])}`)
onMounted(async () => {
  payAccounts.value = (await getAccounts().catch(() => ({ accounts: [] as AccountView[] }))).accounts
})

const parsed = computed(() => {
  const v = amount.value.trim()
  if (v === '' || v === '-') return null
  const n = Number(v)
  return Number.isFinite(n) ? Math.round(n) : null
})

const request = computed<CreateEntryRequest | null>(() => {
  // 逐筆加：試算「加上這一筆之後」的影響
  if (mode.value === 'parts') {
    if (partValue.value === null) return null
    const next = [...parts.value, partValue.value]
    return {
      date: props.date, categoryId: props.slot.categoryId, slotId: props.slot.slotId, inputMode: 'Actual',
      amount: next.reduce((a, b) => a + b, 0), usePool: usePool.value, note: note.value.trim() || null, subscription: null, parts: next,
    }
  }
  if (parsed.value === null) return null
  if (actualMode.value && parsed.value < 0) return null
  return {
    date: props.date,
    categoryId: props.slot.categoryId,
    slotId: props.slot.slotId,
    inputMode: actualMode.value ? 'Actual' : 'Overage',
    amount: parsed.value,
    usePool: usePool.value,
    note: note.value.trim() || null,
    subscription: null,
  }
})

const { preview, loading } = usePreview(() => period.value.id, request)
const shortfall = ref<ShortfallChoice>('Pool')
const shortfallAccount = ref<number | null>(null)
const shortfallExtra = () =>
  preview.value && preview.value.entry.unabsorbed > 0
    ? { guardrail: shortfall.value, guardrailAccountId: shortfall.value === 'Savings' ? shortfallAccount.value : null }
    : {}

function quick(v: number) {
  actualMode.value = true
  amount.value = String(v)
}

async function submit() {
  if (!request.value) return
  saving.value = true
  error.value = null
  try {
    setPeriod(await createEntry(period.value.id, { ...request.value, accountId: actualMode.value ? accountId.value : null, split: actualMode.value ? split.value : null, ...shortfallExtra() }))
    emit('close')
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    saving.value = false
  }
}

async function clearReport() {
  if (!existing.value) return
  saving.value = true
  try {
    setPeriod(await deleteEntry(period.value.id, existing.value.id))
    emit('close')
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Sheet :title="`${categoryName}・${slot.name}`" :subtitle="dayLabel(date)" @close="emit('close')">
    <div class="budget-line num">
      <span>這餐額度</span>
      <span>
        <b>{{ money(slot.planned) }}</b>
        <span v-if="slot.planned !== slot.basePlanned" class="muted">（原本 {{ money(slot.basePlanned) }}，因超支攤提或分配而調整）</span>
      </span>
    </div>

    <form class="form" @submit.prevent="mode === 'parts' ? addPart() : submit()">
      <div class="seg mode-seg" role="group" aria-label="怎麼記">
        <button type="button" :aria-pressed="mode === 'once'" @click="switchMode('once')">一次輸入</button>
        <button type="button" :aria-pressed="mode === 'parts'" @click="switchMode('parts')">逐筆加</button>
      </div>

      <template v-if="mode === 'parts'">
        <div class="parts-sum num">
          <span>已經花了 <b>{{ money(partsTotal) }}</b></span>
          <span :class="slot.planned - partsTotal < 0 ? 'bad' : 'muted'">
            {{ slot.planned - partsTotal >= 0 ? `這個時段還剩 ${money(slot.planned - partsTotal)}` : `超過 ${money(partsTotal - slot.planned)}` }}
          </span>
        </div>
        <ul v-if="parts.length" class="parts">
          <li v-for="(p, i) in parts" :key="i" class="num">
            <span>第 {{ i + 1 }} 筆</span>
            <b>{{ money(p) }}</b>
            <button type="button" class="btn quiet sm danger" :disabled="saving" @click="removePart(i)">刪掉</button>
          </li>
        </ul>
        <div class="add-part">
          <input v-model="partInput" class="input num big" inputmode="numeric" placeholder="這次花多少" aria-label="這次花多少" autofocus />
          <button type="submit" class="btn primary" :disabled="partValue === null || saving">加一筆</button>
        </div>
        <p v-if="addedMsg" class="good small">{{ addedMsg }}</p>
        <p class="muted small">每加一筆就先存、先扣這個時段的額度；有折扣就輸入折扣後的價錢。</p>
      </template>

      <template v-else>
      <label class="check">
        <input v-model="actualMode" type="checkbox" />
        <span>
          輸入的是實際價格
          <span class="hint">{{ actualMode ? '直接輸入這餐花多少，系統幫你算差額' : '改成輸入「超支多少」；少花就輸入負數' }}</span>
        </span>
      </label>

      <label class="field">
        {{ actualMode ? '實際花費' : '超支金額' }}
        <input
          v-model="amount"
          class="input num big"
          inputmode="numeric"
          :placeholder="actualMode ? String(slot.planned) : '例如 30'"
          autofocus
        />
      </label>

      <div class="quick">
        <button type="button" class="btn sm" @click="quick(0)">沒花</button>
        <button type="button" class="btn sm" @click="quick(slot.planned)">剛好 {{ money(slot.planned) }}</button>
      </div>
      </template>

      <label class="check">
        <input v-model="usePool" type="checkbox" />
        <span>
          超支先從待分配池扣
          <span class="hint">目前待分配池 {{ money(period.pool.balance) }}；不夠的部分才攤到之後的日子</span>
        </span>
      </label>

      <div class="row2">
        <label class="field">
          備註（選填）
          <input v-model="note" class="input" maxlength="120" placeholder="例如 聚餐" />
        </label>
        <label v-if="actualMode && payAccounts.length" class="field">
          付款帳戶（選填）
          <select v-model="accountId" class="select">
            <option :value="null">不指定</option>
            <option v-for="a in payAccounts" :key="a.id" :value="a.id">{{ a.name }}{{ a.type === 'CreditCard' ? '（信用卡）' : '' }}</option>
          </select>
        </label>
      </div>

      <SplitField v-if="actualMode && mode === 'once'" v-model="split" :my-share="parsed" />

      <ImpactPreview :preview="preview" :loading="loading" />
      <ShortfallPicker
        v-if="preview && preview.entry.unabsorbed > 0"
        v-model:choice="shortfall"
        v-model:account-id="shortfallAccount"
        :amount="preview.entry.unabsorbed"
      />

      <p v-if="error" class="error-box">{{ error }}</p>

      <div class="actions">
        <button v-if="existing" type="button" class="btn quiet danger" :disabled="saving" @click="clearReport">清除回報</button>
        <span class="spacer" />
        <template v-if="mode === 'parts'">
          <button type="button" class="btn" @click="emit('close')">完成</button>
        </template>
        <template v-else>
          <button type="button" class="btn" @click="emit('close')">取消</button>
          <button type="submit" class="btn primary" :disabled="!request || saving">{{ existing ? '更新' : '回報' }}</button>
        </template>
      </div>
    </form>
  </Sheet>
</template>

<style scoped>
.budget-line {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  font-size: 14px;
  color: var(--ink-2);
  padding-bottom: 12px;
  border-bottom: 1px solid var(--line);
}
.budget-line b {
  color: var(--ink);
  font-size: 16px;
}
.form {
  display: flex;
  flex-direction: column;
  gap: 14px;
}
.big {
  font-size: 24px;
  font-weight: 600;
  min-height: 52px;
}
.quick {
  display: flex;
  gap: 8px;
}
.mode-seg {
  align-self: flex-start;
}
.parts-sum {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
  font-size: 14px;
}
.parts-sum b {
  font-size: 18px;
}
.parts {
  list-style: none;
  margin: 0;
  padding: 0;
  border-top: 1px solid var(--line);
}
.parts li {
  display: grid;
  grid-template-columns: 1fr auto auto;
  gap: 12px;
  align-items: center;
  padding: 6px 0;
  border-bottom: 1px solid var(--line);
  font-size: 14px;
}
.add-part {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 8px;
  align-items: center;
}
.small {
  font-size: 12px;
}
.row2 {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
}
.actions {
  display: flex;
  gap: 8px;
  align-items: center;
}
.spacer {
  flex: 1;
}
</style>
