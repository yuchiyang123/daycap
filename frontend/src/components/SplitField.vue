<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { getDebts } from '../api/endpoints'
import type { SplitKind, SplitRequest } from '../api/types'
import { money } from '../lib/format'

/**
 * 分帳（§14）：代墊的錢不是花費。上面填的金額是「我的份」。
 * 我全付 → 多付的記應收；對方先付 → 我的份照扣預算，記應付，錢還沒從我的帳戶出去。
 */
const props = defineProps<{ myShare: number | null }>()
const model = defineModel<SplitRequest | null>({ default: null })

const on = ref(false)
const kind = ref<SplitKind>('IPaid')
const who = ref('')
const total = ref('')
const people = ref<string[]>([])

onMounted(async () => {
  const d = await getDebts().catch(() => null)
  people.value = [...new Set((d?.debts ?? []).map((x) => x.counterparty))]
})

const totalNum = computed(() => Math.round(Number(total.value)))
const owed = computed(() => (kind.value === 'IPaid' ? totalNum.value - (props.myShare ?? 0) : (props.myShare ?? 0)))

watch([on, kind, who, total], () => {
  model.value =
    on.value && who.value.trim()
      ? { kind: kind.value, counterparty: who.value.trim(), total: kind.value === 'IPaid' ? totalNum.value || null : null }
      : null
})
</script>

<template>
  <div class="split">
    <label class="check">
      <input v-model="on" type="checkbox" />
      <span>和別人分帳<span class="hint">上面的金額填「我的份」</span></span>
    </label>
    <template v-if="on">
      <div class="seg" role="radiogroup">
        <button type="button" :class="{ on: kind === 'IPaid' }" @click="kind = 'IPaid'">我全付</button>
        <button type="button" :class="{ on: kind === 'TheyPaid' }" @click="kind = 'TheyPaid'">對方先付</button>
      </div>
      <div class="row">
        <label class="field">
          跟誰
          <input v-model="who" class="input" maxlength="40" list="split-people" placeholder="名字" />
          <datalist id="split-people">
            <option v-for="p in people" :key="p" :value="p" />
          </datalist>
        </label>
        <label v-if="kind === 'IPaid'" class="field">
          我總共付了
          <input v-model="total" class="input num" inputmode="numeric" placeholder="例如 800" />
        </label>
      </div>
      <p v-if="who.trim() && owed > 0" class="muted small num">
        {{ kind === 'IPaid' ? `記應收：${who.trim()} 要還你 ${money(owed)}（不算花費）` : `記應付：你欠 ${who.trim()} ${money(owed)}（還錢時從帳戶扣，預算不動）` }}
      </p>
      <p v-else-if="kind === 'IPaid' && total && owed <= 0" class="bad small">總共付的要比我的份多。</p>
    </template>
  </div>
</template>

<style scoped>
.split {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.seg {
  display: grid;
  grid-template-columns: 1fr 1fr;
  border: 1px solid var(--line);
  border-radius: 4px;
  overflow: hidden;
  max-width: 280px;
}
.seg button {
  padding: 7px 4px;
  background: none;
  border: 0;
  font: inherit;
  font-size: 14px;
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
.row {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
}
.small {
  font-size: 12px;
}
@media (max-width: 420px) {
  .row {
    grid-template-columns: 1fr;
  }
}
</style>
