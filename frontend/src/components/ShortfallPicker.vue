<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { getAccounts } from '../api/endpoints'
import type { AccountView, ShortfallChoice } from '../api/types'
import { money } from '../lib/format'

/**
 * 超支護欄（§10.2）：攤到之後的日子會把某天扣到下限以下、或後面沒天數可攤時，
 * 讓使用者選剩下的怎麼處理。預設從待分配池扣。
 */
defineProps<{ amount: number }>()
const choice = defineModel<ShortfallChoice>('choice', { default: 'Pool' })
const accountId = defineModel<number | null>('accountId', { default: null })

const accounts = ref<AccountView[]>([])
onMounted(async () => {
  accounts.value = (await getAccounts().catch(() => ({ accounts: [] as AccountView[] }))).accounts.filter((a) => a.type !== 'CreditCard')
  if (accountId.value === null) accountId.value = accounts.value[0]?.id ?? null
})

const options: { v: ShortfallChoice; label: string; hint: string }[] = [
  { v: 'Pool', label: '從待分配池扣', hint: '池子可能變成負的' },
  { v: 'NextPeriod', label: '延到下一期', hint: '下一期期初少這筆' },
  { v: 'Split', label: '分兩期還', hint: '下一期、再下一期各一半' },
  { v: 'Savings', label: '從存款吸收', hint: '直接從帳戶扣，不影響預算' },
]
</script>

<template>
  <fieldset class="picker">
    <legend>
      還有 <b class="num">{{ money(amount) }}</b> 攤不下去（每個時段最多扣到原本的一半）
    </legend>
    <label v-for="o in options" :key="o.v" class="check">
      <input v-model="choice" type="radio" :value="o.v" name="shortfall" />
      <span>{{ o.label }}<span class="hint">{{ o.hint }}</span></span>
    </label>
    <label v-if="choice === 'Savings'" class="field acct">
      從哪個帳戶
      <select v-model="accountId" class="select">
        <option v-for="a in accounts" :key="a.id" :value="a.id">{{ a.name }}（{{ money(a.balance) }}）</option>
      </select>
    </label>
  </fieldset>
</template>

<style scoped>
.picker {
  border: 1px solid var(--warn-text);
  border-radius: 4px;
  padding: 10px 12px;
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: 8px;
}
legend {
  font-size: 13px;
  padding: 0 4px;
}
.check input {
  border-radius: 50%;
}
.check input::after {
  border-radius: 50%;
}
.acct {
  padding-left: 28px;
}
</style>
