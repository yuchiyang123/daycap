<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import Sheet from './Sheet.vue'
import { allocatePool, getJars, moveJar } from '../api/endpoints'
import type { JarView } from '../api/types'
import { loadCurrentPeriod, store, setPeriod } from '../lib/store'
import { modeLabel, money, shortDate } from '../lib/format'

/**
 * 分配剩餘：待分配池還有錢時，全部（或一部分）給某一個分類，或依各分類額度的比例分給全部。
 * 每日類分到的錢會加到之後每一天的時段上，月額度類直接加額度。
 */
const emit = defineEmits<{ close: [] }>()
const period = computed(() => store.period!)
const variable = computed(() => period.value.categories.filter((c) => c.mode !== 'Fixed'))

const mode = ref<'single' | 'proportional'>('single')
const amount = ref(String(Math.max(0, period.value.pool.balance)))
const categoryId = ref<number>(variable.value.find((c) => c.mode === 'Envelope')?.categoryId ?? variable.value[0]?.categoryId ?? 0)
const busy = ref(false)
const error = ref<string | null>(null)

const n = computed(() => Math.round(Number(amount.value) || 0))
const valid = computed(() => n.value > 0 && n.value <= period.value.pool.balance)

/** 跟後端同一套最大餘數法，先讓使用者看到每個分類會分到多少 */
const preview = computed(() => {
  if (mode.value === 'single') return []
  const weights = variable.value.map((c) => Math.max(1, c.budget))
  const total = weights.reduce((a, b) => a + b, 0)
  const raw = weights.map((w) => (n.value * w) / total)
  const shares = raw.map(Math.floor)
  let left = n.value - shares.reduce((a, b) => a + b, 0)
  raw
    .map((r, i) => ({ i, rem: r - Math.floor(r) }))
    .sort((a, b) => b.rem - a.rem || a.i - b.i)
    .forEach(({ i }) => {
      if (left > 0) {
        shares[i]++
        left--
      }
    })
  return variable.value.map((c, i) => ({ c, share: shares[i] }))
})

// ---- 分配順序（§11.2）：有到期日、還沒存滿的罐子先補 ----
const jars = ref<JarView[]>([])
onMounted(async () => {
  jars.value = await getJars().catch(() => [])
})
const dueJars = computed(() => jars.value.filter((j) => !j.closed && j.need > 0 && j.dueDate))
async function fillJar(j: JarView) {
  const amount = Math.min(j.need, Math.max(0, period.value.pool.balance))
  if (amount <= 0) return
  busy.value = true
  error.value = null
  try {
    jars.value = await moveJar(j.id, amount)
    await loadCurrentPeriod(true)
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}

async function submit() {
  if (!valid.value) return
  busy.value = true
  error.value = null
  try {
    setPeriod(await allocatePool(period.value.id, { mode: mode.value, categoryId: mode.value === 'single' ? categoryId.value : null, amount: n.value }))
    emit('close')
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <Sheet title="分配剩餘" :subtitle="`待分配池目前 ${money(period.pool.balance)}`" @close="emit('close')">
    <form class="form" @submit.prevent="submit">
      <div v-if="dueJars.length" class="jars">
        <span class="small muted">先補快到期的罐子</span>
        <ul class="list shares">
          <li v-for="j in dueJars" :key="j.id">
            <span>{{ j.name }}<span class="muted small">・{{ shortDate(j.dueDate!) }}・還差 {{ money(j.need) }}</span></span>
            <button type="button" class="btn sm" :disabled="busy || period.pool.balance <= 0" @click="fillJar(j)">
              補 {{ money(Math.min(j.need, Math.max(0, period.pool.balance))) }}
            </button>
          </li>
        </ul>
      </div>
      <div class="seg" role="group" aria-label="分配方式">
        <button type="button" :aria-pressed="mode === 'single'" @click="mode = 'single'">全部給一個分類</button>
        <button type="button" :aria-pressed="mode === 'proportional'" @click="mode = 'proportional'">依比例分到全部</button>
      </div>

      <label class="field">
        金額
        <input v-model="amount" class="input num" inputmode="numeric" />
      </label>
      <div class="quick">
        <button type="button" class="btn sm" @click="amount = String(period.pool.balance)">全部 {{ money(period.pool.balance) }}</button>
        <button type="button" class="btn sm" @click="amount = String(Math.floor(period.pool.balance / 2))">一半</button>
      </div>

      <label v-if="mode === 'single'" class="field">
        給哪個分類
        <select v-model.number="categoryId" class="select">
          <option v-for="c in variable" :key="c.categoryId" :value="c.categoryId">
            {{ c.name }}（{{ modeLabel[c.mode] }}，剩 {{ money(c.budget - c.projected) }}）
          </option>
        </select>
      </label>

      <ul v-else class="list shares">
        <li v-for="p in preview" :key="p.c.categoryId">
          <span>{{ p.c.name }}</span>
          <span class="num">+{{ money(p.share) }}</span>
        </li>
      </ul>

      <p class="muted small">每日類分到的錢會平均加到之後每一天的時段；月額度類直接加額度。在總覽的待分配池流水刪掉那一筆就能還原。</p>
      <p v-if="n > period.pool.balance" class="error-box">超過待分配池餘額。</p>
      <p v-if="error" class="error-box">{{ error }}</p>

      <div class="actions">
        <button type="button" class="btn" @click="emit('close')">先不要</button>
        <button type="submit" class="btn primary" :disabled="!valid || busy">分配</button>
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
.quick {
  display: flex;
  gap: 8px;
  margin-top: -6px;
}
.shares li {
  display: flex;
  justify-content: space-between;
  padding: 6px 0;
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
