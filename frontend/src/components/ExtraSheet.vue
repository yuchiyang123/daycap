<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Sheet from './Sheet.vue'
import ImpactPreview from './ImpactPreview.vue'
import ShortfallPicker from './ShortfallPicker.vue'
import type { ShortfallChoice, JarView } from '../api/types'
import { createEntry, getAccounts, getJars } from '../api/endpoints'
import type { AccountView } from '../api/types'
import { onMounted } from 'vue'
import type { BillingCycle, CreateEntryRequest } from '../api/types'
import { store, setPeriod } from '../lib/store'
import { dayLabel, money } from '../lib/format'
import { usePreview } from '../lib/usePreview'

/**
 * 不屬於任何時段的花費：臨時買飲料（每日額度類 → 超支處理同時段）、買衣服（月額度類 → 從月額度扣）。
 * 勾「這是訂閱」會在下個週期起自動變成固定支出。
 */
const props = defineProps<{ date: string; categoryId?: number }>()
const emit = defineEmits<{ close: [] }>()

const period = computed(() => store.period!)
const choices = computed(() => period.value.categories.filter((c) => c.mode !== 'Fixed'))
const fixedTargets = computed(() => period.value.categories.filter((c) => c.mode === 'Fixed'))

const date = ref(props.date)
const categoryId = ref<number>(props.categoryId ?? choices.value.find((c) => c.mode === 'Envelope')?.categoryId ?? choices.value[0]?.categoryId ?? 0)
const amount = ref('')
const note = ref('')
const usePool = ref(true)
const isSub = ref(false)
const subName = ref('')
const subTarget = ref<number>(fixedTargets.value.find((c) => c.group === 'Other')?.categoryId ?? fixedTargets.value[0]?.categoryId ?? 0)
const subCycle = ref<BillingCycle>('Monthly')
const saving = ref(false)
const error = ref<string | null>(null)
const payAccounts = ref<AccountView[]>([])
const accountId = ref<number | null>(null)
// 從罐子付（§11.2）：預約、年繳到期時用
const jars = ref<JarView[]>([])
const jarId = ref<number | null>(null)
onMounted(async () => {
  payAccounts.value = (await getAccounts().catch(() => ({ accounts: [] as AccountView[] }))).accounts
  jars.value = (await getJars().catch(() => [] as JarView[])).filter((j) => !j.closed && j.balance > 0)
})

watch(note, (v, old) => {
  if (!subName.value || subName.value === old) subName.value = v
})

const category = computed(() => choices.value.find((c) => c.categoryId === categoryId.value))
const isEnvelope = computed(() => category.value?.mode === 'Envelope')
const remaining = computed(() => (category.value ? category.value.budget - category.value.projected : 0))

const request = computed<CreateEntryRequest | null>(() => {
  const n = Number(amount.value)
  if (!amount.value.trim() || !Number.isFinite(n) || n < 0 || !category.value) return null
  return {
    date: date.value,
    categoryId: categoryId.value,
    slotId: null,
    inputMode: 'Actual',
    amount: Math.round(n),
    usePool: usePool.value,
    note: note.value.trim() || null,
    subscription: null,
    jarId: jarId.value,
  }
})

const { preview, loading } = usePreview(() => period.value.id, request)
const shortfall = ref<ShortfallChoice>('Pool')
const shortfallAccount = ref<number | null>(null)
const shortfallExtra = () =>
  preview.value && preview.value.entry.unabsorbed > 0
    ? { guardrail: shortfall.value, guardrailAccountId: shortfall.value === 'Savings' ? shortfallAccount.value : null }
    : {}

async function submit() {
  if (!request.value) return
  if (isSub.value && (!subName.value.trim() || !subTarget.value)) {
    error.value = '訂閱要有名稱，並選一個固定支出分類放進去。'
    return
  }
  saving.value = true
  error.value = null
  try {
    const body: CreateEntryRequest = {
      ...request.value,
      subscription: isSub.value ? { name: subName.value.trim(), targetCategoryId: subTarget.value, cycle: subCycle.value, dueDay: null } : null,
      accountId: accountId.value,
      ...shortfallExtra(),
    }
    setPeriod(await createEntry(period.value.id, body))
    emit('close')
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Sheet title="記一筆花費" :subtitle="'扣在分類額度上；' + dayLabel(date)" @close="emit('close')">
    <form class="form" @submit.prevent="submit">
      <label class="field">
        日期
        <input v-model="date" type="date" class="input" :min="period.startDate" :max="period.endDate" required />
      </label>
      <div class="row2">
        <label class="field">
          分類
          <select v-model.number="categoryId" class="select">
            <option v-for="c in choices" :key="c.categoryId" :value="c.categoryId">
              {{ c.name }}{{ c.mode === 'Daily' ? '（每日）' : '（月額度）' }}
            </option>
          </select>
        </label>
        <label class="field">
          金額
          <input v-model="amount" class="input num" inputmode="numeric" placeholder="0" autofocus />
        </label>
      </div>
      <p class="muted small num">
        <template v-if="isEnvelope">本期月額度還剩 {{ money(remaining) }}</template>
        <template v-else>每日額度類沒有這個時段，整筆都算超支</template>
      </p>

      <div class="row2">
        <label class="field">
          備註
          <input v-model="note" class="input" maxlength="120" placeholder="例如 手搖飲、Netflix" />
        </label>
        <label class="field">
          付款帳戶（選填）
          <select v-model="accountId" class="select">
            <option :value="null">不指定</option>
            <option v-for="a in payAccounts" :key="a.id" :value="a.id">{{ a.name }}{{ a.type === 'CreditCard' ? '（信用卡）' : '' }}</option>
          </select>
        </label>
      </div>

      <label v-if="jars.length" class="field">
        從罐子付（選填）
        <select v-model="jarId" class="select">
          <option :value="null">不用，照分類額度</option>
          <option v-for="j in jars" :key="j.id" :value="j.id">{{ j.name }}（罐子裡 {{ money(j.balance) }}）</option>
        </select>
        <span v-if="jarId" class="hint">罐子付得起的部分不算進這期預算；超過的照一般規則{{ jars.find((j) => j.id === jarId)?.kind === 'Reservation' ? '；預約付完剩下的回待分配池' : '' }}</span>
      </label>

      <label class="check">
        <input v-model="usePool" type="checkbox" />
        <span>
          {{ isEnvelope ? '超過月額度的部分從待分配池扣' : '超支先從待分配池扣' }}
          <span class="hint">目前待分配池 {{ money(period.pool.balance) }}</span>
        </span>
      </label>

      <label class="check">
        <input v-model="isSub" type="checkbox" :disabled="fixedTargets.length === 0" />
        <span>
          這是訂閱
          <span class="hint">這期先當成一般花費；下一期起自動變成固定支出，不用再回報</span>
        </span>
      </label>
      <div v-if="isSub" class="sub-box">
        <label class="field">
          訂閱名稱
          <input v-model="subName" class="input" maxlength="60" />
        </label>
        <div class="row2">
          <label class="field">
            放進哪個固定支出
            <select v-model.number="subTarget" class="select">
              <option v-for="c in fixedTargets" :key="c.categoryId" :value="c.categoryId">{{ c.name }}</option>
            </select>
          </label>
          <label class="field">
            週期
            <select v-model="subCycle" class="select">
              <option value="Monthly">月繳</option>
              <option value="Quarterly">季繳</option>
              <option value="Yearly">年繳</option>
            </select>
          </label>
        </div>
      </div>

      <ImpactPreview :preview="preview" :loading="loading" :envelope="isEnvelope" />
      <ShortfallPicker
        v-if="preview && preview.entry.unabsorbed > 0"
        v-model:choice="shortfall"
        v-model:account-id="shortfallAccount"
        :amount="preview.entry.unabsorbed"
      />
      <p v-if="error" class="error-box">{{ error }}</p>

      <div class="actions">
        <button type="button" class="btn" @click="emit('close')">取消</button>
        <button type="submit" class="btn primary" :disabled="!request || saving">記下</button>
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
.row2 {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
}
.small {
  font-size: 12px;
  margin-top: -8px;
}
.sub-box {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding-left: 28px;
}
.actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
