<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { closeJar, createJar, getJars, getSettings, moveJar, updateJar } from '../api/endpoints'
import type { FixedItemDto, JarKind, JarView } from '../api/types'
import { loadCurrentPeriod, store } from '../lib/store'
import { money, shortDate } from '../lib/format'

/**
 * 罐子（§11.2）：預約支出、年繳預留款、儲蓄目標，都是「有目標金額（與到期日）的罐子」。
 * 錢從待分配池存進去；快到期的排前面（分配順序 1）。
 */
const jars = ref<JarView[]>([])
const error = ref<string | null>(null)
const busy = ref(false)
const showClosed = ref(false)

const kindLabel: Record<JarKind, string> = { Reservation: '預約支出', Annual: '年繳預留', Goal: '儲蓄目標' }
const kindHint: Record<JarKind, string> = {
  Reservation: '例如 10/20 紅包 3,600。登記當下就從待分配池扣；池子不夠的攤到之後每天。付的時候在「額外花費」選從這個罐子付。',
  Annual: '每期提撥一份，到期從這裡付，不會某個月突然被扣一大筆。',
  Goal: '旅遊基金、緊急預備金。可以設定「省下的錢自動存幾 %」。',
}

const open = computed(() => jars.value.filter((j) => !j.closed))
const closed = computed(() => jars.value.filter((j) => j.closed))

async function load() {
  jars.value = await getJars().catch(() => [])
}
onMounted(load)

async function run(fn: () => Promise<JarView[]>) {
  busy.value = true
  error.value = null
  try {
    jars.value = await fn()
    await loadCurrentPeriod(true) // 待分配池跟著變
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}

// ---- 存入 / 拿回 ----
const moving = ref<{ id: number; dir: 1 | -1; amount: string } | null>(null)
function startMove(j: JarView, dir: 1 | -1) {
  const pool = Math.max(0, store.period?.pool.balance ?? 0)
  const suggested = dir === 1 ? Math.min(j.need || pool, pool) : j.balance
  moving.value = { id: j.id, dir, amount: suggested > 0 ? String(Math.round(suggested)) : '' }
}
const confirmMove = () => {
  const m = moving.value
  if (!m) return
  const n = Math.round(Number(m.amount))
  if (!(n > 0)) return
  moving.value = null
  run(() => moveJar(m.id, n * m.dir))
}

const closing = ref<number | null>(null)
function close(j: JarView) {
  if (closing.value !== j.id) {
    closing.value = j.id
    setTimeout(() => closing.value === j.id && (closing.value = null), 3000)
    return
  }
  closing.value = null
  run(() => closeJar(j.id))
}

// ---- 新增 / 編輯 ----
interface Form {
  id: number | null
  kind: JarKind
  name: string
  target: string
  due: string
  monthly: string
  auto: string
  fixedItemId: number | null
}
const form = ref<Form | null>(null)
const yearlyItems = ref<FixedItemDto[]>([])

async function startCreate() {
  form.value = { id: null, kind: 'Reservation', name: '', target: '', due: '', monthly: '', auto: '', fixedItemId: null }
  const s = await getSettings().catch(() => null)
  yearlyItems.value = (s?.settings.categories ?? []).flatMap((c) => c.fixedItems).filter((f) => f.cycle !== 'Monthly' && f.isActive)
}
function startEdit(j: JarView) {
  form.value = {
    id: j.id,
    kind: j.kind,
    name: j.name,
    target: String(j.targetAmount),
    due: j.dueDate ?? '',
    monthly: j.monthlyAmount ? String(j.monthlyAmount) : '',
    auto: j.autoSurplusPercent ? String(j.autoSurplusPercent) : '',
    fixedItemId: j.fixedItemId,
  }
}
function pickFixedItem() {
  const f = form.value
  const item = yearlyItems.value.find((i) => i.id === f?.fixedItemId)
  if (!f || !item) return
  f.name = item.name
  f.target = String(item.amount)
}
function save() {
  const f = form.value
  if (!f) return
  const body = {
    kind: f.kind,
    name: f.name.trim(),
    targetAmount: Math.round(Number(f.target)),
    dueDate: f.due || null,
    monthlyAmount: f.monthly.trim() ? Math.round(Number(f.monthly)) : null,
    autoSurplusPercent: f.auto.trim() ? Number(f.auto) : null,
    fixedItemId: f.kind === 'Annual' ? f.fixedItemId : null,
  }
  const id = f.id
  form.value = null
  run(() => (id === null ? createJar(body) : updateJar(id, body)))
}

const pctOf = (j: JarView) => (j.targetAmount > 0 ? Math.min(100, (j.balance / j.targetAmount) * 100) : 0)
function dueText(j: JarView) {
  if (!j.dueDate) return null
  if (j.daysLeft === null) return shortDate(j.dueDate)
  if (j.daysLeft < 0) return `${shortDate(j.dueDate)}・已過期`
  if (j.daysLeft === 0) return `${shortDate(j.dueDate)}・今天`
  return `${shortDate(j.dueDate)}・還有 ${j.daysLeft} 天`
}
</script>

<template>
  <div class="jars">
    <p v-if="error" class="error-box">{{ error }}</p>

    <div v-if="open.length" class="panel">
      <div v-for="(j, i) in open" :key="j.id" class="jar" :class="{ first: i === 0 }">
        <div class="j-head">
          <span>
            <b>{{ j.name }}</b>
            <span class="tag">{{ kindLabel[j.kind] }}</span>
          </span>
          <span class="num"><b>{{ money(j.balance) }}</b><span class="muted"> / {{ money(j.targetAmount) }}</span></span>
        </div>
        <div class="bar" role="img" :aria-label="`${Math.round(pctOf(j))}%`"><span :style="{ width: pctOf(j) + '%' }"></span></div>
        <div class="j-meta muted small">
          <span v-if="dueText(j)">{{ dueText(j) }}</span>
          <span v-if="j.need > 0" class="num">還差 {{ money(j.need) }}</span>
          <span v-if="j.monthlyAmount" class="num">每期提撥 {{ money(j.monthlyAmount) }}</span>
          <span v-if="j.autoSurplusPercent">省下的自動存 {{ j.autoSurplusPercent }}%</span>
        </div>
        <div v-if="moving?.id === j.id" class="move">
          <input v-model="moving.amount" class="input num" inputmode="numeric" :aria-label="moving.dir === 1 ? '存入金額' : '拿回金額'" />
          <button type="button" class="btn sm primary" :disabled="busy" @click="confirmMove">{{ moving.dir === 1 ? '從池子存入' : '拿回池子' }}</button>
          <button type="button" class="btn sm quiet" @click="moving = null">取消</button>
        </div>
        <div v-else class="j-actions">
          <button type="button" class="btn sm" :disabled="busy" @click="startMove(j, 1)">存入</button>
          <button v-if="j.balance > 0" type="button" class="btn sm" :disabled="busy" @click="startMove(j, -1)">拿回</button>
          <button type="button" class="btn sm quiet" @click="startEdit(j)">編輯</button>
          <button type="button" class="btn sm quiet danger" :disabled="busy" @click="close(j)">{{ closing === j.id ? '確認關閉（剩下的回池子）' : '關閉' }}</button>
        </div>
      </div>
    </div>
    <p v-else-if="!form" class="muted small">還沒有罐子。紅包、年繳保費、旅遊基金這類「之後一定要付或想存」的錢，放進罐子就不會混在每天的額度裡。</p>

    <div v-if="form" class="panel form">
      <div class="seg" role="radiogroup">
        <button
          v-for="k in (['Reservation', 'Annual', 'Goal'] as JarKind[])"
          :key="k"
          type="button"
          :class="{ on: form.kind === k }"
          :disabled="form.id !== null && form.kind !== k"
          @click="form.kind = k"
        >
          {{ kindLabel[k] }}
        </button>
      </div>
      <p class="muted small">{{ kindHint[form.kind] }}</p>
      <label v-if="form.kind === 'Annual' && form.id === null && yearlyItems.length" class="field">
        從年繳 / 季繳固定支出轉過來（選填）
        <select v-model="form.fixedItemId" class="select" @change="pickFixedItem">
          <option :value="null">不用</option>
          <option v-for="f in yearlyItems" :key="f.id" :value="f.id">{{ f.name }}（{{ money(f.amount) }}）</option>
        </select>
        <span v-if="form.fixedItemId" class="hint">那個固定支出從明天起停用，改由這個罐子付</span>
      </label>
      <label class="field">名稱<input v-model="form.name" class="input" maxlength="40" /></label>
      <div class="two">
        <label class="field">目標金額<input v-model="form.target" class="input num" inputmode="numeric" /></label>
        <label class="field">
          {{ form.kind === 'Reservation' ? '哪天要付' : form.kind === 'Annual' ? '下次繳費日' : '希望哪天存到（選填）' }}
          <input v-model="form.due" type="date" class="input" />
        </label>
      </div>
      <label v-if="form.kind === 'Annual'" class="field">
        每期提撥（選填）
        <input v-model="form.monthly" class="input num" inputmode="numeric" :placeholder="form.target ? String(Math.ceil(Number(form.target) / 12)) : '目標 ÷ 12'" />
      </label>
      <label v-if="form.kind === 'Goal'" class="field">
        時段省下的錢自動存進來幾 %（選填）
        <input v-model="form.auto" class="input num" inputmode="decimal" placeholder="例如 50" />
      </label>
      <div class="form-actions">
        <button type="button" class="btn" @click="form = null">取消</button>
        <button type="button" class="btn primary" :disabled="busy || !form.name.trim() || !(Number(form.target) > 0)" @click="save">
          {{ form.id === null ? '建立' : '儲存' }}
        </button>
      </div>
    </div>
    <button v-else type="button" class="btn sm" @click="startCreate">新增罐子</button>

    <template v-if="closed.length">
      <button type="button" class="btn quiet sm" @click="showClosed = !showClosed">{{ showClosed ? '收起' : `已關閉的 ${closed.length} 個` }}</button>
      <ul v-if="showClosed" class="closed muted small">
        <li v-for="j in closed" :key="j.id">{{ j.name }}・{{ kindLabel[j.kind] }}・{{ money(j.targetAmount) }}</li>
      </ul>
    </template>
  </div>
</template>

<style scoped>
.jars {
  display: flex;
  flex-direction: column;
  gap: 10px;
  align-items: flex-start;
}
.jars > .panel {
  width: 100%;
}
.jar {
  padding: 12px 16px;
  display: flex;
  flex-direction: column;
  gap: 6px;
  border-top: 1px solid var(--line);
}
.jar.first {
  border-top: 0;
}
.j-head {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}
.tag {
  margin-left: 8px;
  font-size: 11px;
  color: var(--muted);
  border: 1px solid var(--line);
  border-radius: 3px;
  padding: 0 5px;
}
.bar {
  height: 6px;
  background: var(--sunk);
  border-radius: 3px;
  overflow: hidden;
}
.bar span {
  display: block;
  height: 100%;
  background: var(--accent);
}
.j-meta {
  display: flex;
  gap: 12px;
  flex-wrap: wrap;
}
.small {
  font-size: 12px;
}
.j-actions,
.move {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
  align-items: center;
}
.move .input {
  width: 120px;
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
.seg {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  border: 1px solid var(--line);
  border-radius: 4px;
  overflow: hidden;
}
.seg button {
  padding: 8px 4px;
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
.seg button:disabled:not(.on) {
  opacity: 0.4;
  cursor: default;
}
.form-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
.closed {
  margin: 0;
  padding-left: 18px;
}
@media (max-width: 420px) {
  .two {
    grid-template-columns: 1fr;
  }
}
</style>
