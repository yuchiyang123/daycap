<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { createTrip, endTrip, getTrips } from '../api/endpoints'
import type { TripView } from '../api/types'
import { loadCurrentPeriod, store } from '../lib/store'
import { money, shortDate } from '../lib/format'

/**
 * 旅遊（§17）：這段日子的每日時段暫停（額度回待分配池），改用旅遊預算付。
 * 旅途中記花費時可以填外幣，依填的匯率換成台幣。
 */
const trips = ref<TripView[]>([])
const error = ref<string | null>(null)
const busy = ref(false)

onMounted(async () => {
  trips.value = await getTrips().catch(() => [])
})
const shown = computed(() => trips.value.filter((t) => t.status !== 'ended' || !t.jarClosed).slice(0, 5))
const statusLabel: Record<string, string> = { upcoming: '還沒出發', active: '旅遊中', ended: '已回國' }

async function run(fn: () => Promise<TripView[]>) {
  busy.value = true
  error.value = null
  try {
    trips.value = await fn()
    await loadCurrentPeriod(true)
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}

const form = ref<{ name: string; start: string; end: string; budget: string; currency: string; rate: string } | null>(null)
function startCreate() {
  const today = store.period?.today ?? ''
  form.value = { name: '', start: today, end: today, budget: '', currency: '', rate: '' }
}
function save() {
  const f = form.value
  if (!f) return
  const body = {
    name: f.name.trim(),
    startDate: f.start,
    endDate: f.end,
    budget: Math.round(Number(f.budget)),
    currency: f.currency.trim().toUpperCase() || null,
    fxRate: f.rate.trim() ? Number(f.rate) : null,
  }
  form.value = null
  run(() => createTrip(body))
}
const ending = ref<number | null>(null)
function end(t: TripView) {
  if (ending.value !== t.id) {
    ending.value = t.id
    setTimeout(() => ending.value === t.id && (ending.value = null), 3000)
    return
  }
  ending.value = null
  run(() => endTrip(t.id))
}
</script>

<template>
  <div class="trips">
    <p v-if="error" class="error-box">{{ error }}</p>
    <div v-if="shown.length" class="panel">
      <ul class="list">
        <li v-for="t in shown" :key="t.id" class="trip">
          <div class="t-head">
            <span><b>{{ t.name }}</b><span class="tag">{{ statusLabel[t.status] }}</span></span>
            <span class="num muted small">{{ shortDate(t.startDate) }} – {{ shortDate(t.endDate) }}</span>
          </div>
          <div class="muted small num meta">
            <span>預算 {{ money(t.budget) }}</span>
            <span v-if="!t.jarClosed">還剩 {{ money(t.remaining) }}</span>
            <span v-if="t.currency">{{ t.currency }} 匯率 {{ t.fxRate ?? '未填' }}</span>
          </div>
          <div v-if="!t.jarClosed || t.status !== 'ended'" class="actions">
            <button type="button" class="btn sm" :disabled="busy" @click="end(t)">
              {{ ending === t.id ? (t.status === 'upcoming' ? '確認取消這趟' : '確認結束（剩下的回池子）') : t.status === 'upcoming' ? '取消' : '結束旅遊' }}
            </button>
          </div>
        </li>
      </ul>
    </div>
    <p v-else-if="!form" class="muted small">出國或長途旅遊時開一趟：那幾天的每日時段暫停，改用一筆旅遊預算付，回來後自動恢復。</p>

    <div v-if="form" class="panel form">
      <label class="field">名稱<input v-model="form.name" class="input" maxlength="40" placeholder="例如 東京" /></label>
      <div class="two">
        <label class="field">出發<input v-model="form.start" type="date" class="input" /></label>
        <label class="field">回來<input v-model="form.end" type="date" class="input" /></label>
      </div>
      <label class="field">
        旅遊預算（台幣）
        <input v-model="form.budget" class="input num" inputmode="numeric" />
        <span class="hint">從待分配池預留；池子不夠的部分攤到之後的日子</span>
      </label>
      <div class="two">
        <label class="field">幣別（選填）<input v-model="form.currency" class="input" maxlength="3" placeholder="JPY" /></label>
        <label class="field">匯率（選填）<input v-model="form.rate" class="input num" inputmode="decimal" placeholder="1 外幣 = ? 台幣" /></label>
      </div>
      <div class="form-actions">
        <button type="button" class="btn" @click="form = null">取消</button>
        <button type="button" class="btn primary" :disabled="busy || !form.name.trim() || !(Number(form.budget) > 0) || !form.start || !form.end" @click="save">建立</button>
      </div>
    </div>
    <button v-else type="button" class="btn sm" @click="startCreate">新增旅遊</button>
  </div>
</template>

<style scoped>
.trips {
  display: flex;
  flex-direction: column;
  gap: 10px;
  align-items: flex-start;
}
.trips > .panel {
  width: 100%;
}
.trip {
  padding: 10px 16px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.t-head {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}
.tag {
  margin-left: 8px;
  font-size: 11px;
  border: 1px solid var(--line);
  border-radius: 3px;
  padding: 0 5px;
}
.meta,
.actions {
  display: flex;
  gap: 12px;
  flex-wrap: wrap;
}
.small {
  font-size: 12px;
}
.form {
  padding: 14px 16px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.two {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
}
.form-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
@media (max-width: 420px) {
  .two {
    grid-template-columns: 1fr;
  }
}
</style>
