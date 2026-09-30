<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { applyTemplate, getOnboarding, previewTemplate, skipOnboarding } from '../api/endpoints'
import type { BlockChoice, TemplateDto, TemplatePreview } from '../api/types'
import { loadCurrentPeriod, store } from '../lib/store'
import { money, shortDate, signed } from '../lib/format'
import { haptic } from '../lib/haptics'

/**
 * 新手引導（§20）：三題＋收入 → 範本預覽（看到每天大概能花多少）→ 示範一張超支卡、一張沒回報的卡 → 開始用。
 * 只有一個概念一開始就要教：沒回報＝照預算花；省下的錢要回報才算數。其他提示等用到時才出現一次。
 */
const router = useRouter()
const step = ref<1 | 2 | 3>(1)
const templates = ref<TemplateDto[]>([])
const error = ref<string | null>(null)

// ---- 第 1 步 ----
const rents = ref<boolean | null>(null)
const eatsOut = ref<boolean | null>(null)
const taipei = ref<boolean | null>(null)
const income = ref('')
const payday = ref('5')
const incomeNum = computed(() => Math.round(Number(income.value)))
const step1Ready = computed(
  () => rents.value !== null && eatsOut.value !== null && taipei.value !== null && incomeNum.value > 0 && Number(payday.value) >= 1 && Number(payday.value) <= 31,
)
const code = computed(() => `${rents.value ? 'rent' : 'home'}-${eatsOut.value ? 'out' : 'cook'}-${taipei.value ? 'tpe' : 'other'}`)
const template = computed(() =>
  rents.value === null || eatsOut.value === null || taipei.value === null ? null : templates.value.find((t) => t.code === code.value) ?? null,
)

onMounted(async () => {
  try {
    templates.value = (await getOnboarding()).templates
  } catch (e) {
    error.value = (e as Error).message
  }
})

// ---- 第 2 步：預覽 ----
const choices = ref<Record<string, { checked: boolean; amount: string }>>({})
const savingsMin = ref('')
const preview = ref<TemplatePreview | null>(null)
const loading = ref(false)

function toPreview() {
  if (!template.value) return
  choices.value = {}
  for (const b of template.value.blocks) choices.value[b.key] = { checked: b.defaultChecked, amount: '' }
  step.value = 2
  refresh()
}

const request = computed(() => ({
  templateCode: code.value,
  income: incomeNum.value,
  paydayDay: Number(payday.value),
  blocks: Object.entries(choices.value).map<BlockChoice>(([key, c]) => ({
    key,
    checked: c.checked,
    amount: c.checked && c.amount.trim() !== '' && Number.isFinite(Number(c.amount)) ? Math.round(Number(c.amount)) : null,
  })),
  savingsMinPercent: savingsMin.value.trim() === '' ? null : Number(savingsMin.value),
}))

let seq = 0
let timer: ReturnType<typeof setTimeout> | undefined
function refresh() {
  clearTimeout(timer)
  timer = setTimeout(async () => {
    const my = ++seq
    loading.value = true
    try {
      const p = await previewTemplate(request.value)
      if (my === seq) preview.value = p
    } catch (e) {
      if (my === seq) error.value = (e as Error).message
    } finally {
      if (my === seq) loading.value = false
    }
  }, 250)
}
watch([choices, savingsMin], () => step.value === 2 && refresh(), { deep: true })

const row = (key: string) => preview.value?.blocks.find((b) => b.key === key)
const canApply = computed(() => !!preview.value && preview.value.errors.length === 0 && !loading.value)

const applying = ref(false)
async function apply() {
  applying.value = true
  error.value = null
  try {
    await applyTemplate(request.value)
    step.value = 3
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    applying.value = false
  }
}

// ---- 第 3 步：示範（不存任何資料）----
const demo = ref<'overspend' | 'overspent' | 'unreported'>('overspend')
const dragX = ref(0)
let startX = 0
let dragging = false
function down(e: PointerEvent) {
  dragging = true
  startX = e.clientX
  ;(e.currentTarget as HTMLElement).setPointerCapture(e.pointerId)
}
let pastThreshold = false
function move(e: PointerEvent) {
  if (!dragging) return
  dragX.value = Math.max(0, e.clientX - startX)
  const past = dragX.value > ((e.currentTarget as HTMLElement).offsetWidth || 300) * 0.35
  if (past && !pastThreshold) haptic('tick')
  pastThreshold = past
}
function up(e: PointerEvent) {
  dragging = false
  pastThreshold = false
  const w = (e.currentTarget as HTMLElement).offsetWidth || 300
  if (dragX.value > w * 0.35) {
    haptic('confirm')
    demo.value = 'overspent'
  }
  dragX.value = 0
}

async function finish() {
  if (store.me) store.me.onboarded = true
  await loadCurrentPeriod(true)
  router.replace('/')
}

async function skip() {
  await skipOnboarding()
  if (store.me) store.me.onboarded = true
  router.replace('/settings')
}

const segs = [
  { label: '住哪裡', model: rents, yes: '在外租屋', no: '住家裡' },
  { label: '三餐', model: eatsOut, yes: '大多外食', no: '大多自己煮' },
  { label: '地區', model: taipei, yes: '雙北', no: '其他縣市' },
]
</script>

<template>
  <div class="page welcome">
    <header class="page-head">
      <div>
        <h1>開始用日額</h1>
        <p class="sub">{{ step }} / 3・{{ step === 1 ? '三個問題' : step === 2 ? '看看範本' : '一件要知道的事' }}</p>
      </div>
      <button v-if="step < 3" type="button" class="btn quiet sm" @click="skip">我自己設定</button>
    </header>

    <p v-if="error" class="error-box">{{ error }}</p>

    <!-- 1. 三題＋收入 -->
    <section v-if="step === 1" class="panel pad">
      <div v-for="s in segs" :key="s.label" class="q">
        <span class="q-label">{{ s.label }}</span>
        <div class="seg">
          <button type="button" :class="{ on: s.model.value === true }" @click="s.model.value = true">{{ s.yes }}</button>
          <button type="button" :class="{ on: s.model.value === false }" @click="s.model.value = false">{{ s.no }}</button>
        </div>
      </div>
      <label class="field">
        每月實際領到多少
        <input v-model="income" class="input num" inputmode="numeric" placeholder="例如 40000" />
      </label>
      <label class="field">
        每月幾號發薪
        <input v-model="payday" class="input num short" inputmode="numeric" />
        <span class="hint">遇到假日會提前一個工作日，之後可以在設定改</span>
      </label>
      <p v-if="template" class="muted small">對應範本：{{ template.name }}</p>
      <button type="button" class="btn primary block" :disabled="!step1Ready || !template" @click="toPreview">下一步</button>
    </section>

    <!-- 2. 套用預覽 -->
    <template v-if="step === 2 && template">
      <section class="panel pad">
        <div class="t-head">
          <b>{{ template.name }}</b>
          <span class="num muted">月收入 {{ money(incomeNum) }}</span>
        </div>
        <p class="muted small">勾選的才套用；填了實際金額就用你填的（轉成固定支出先扣）。沒勾＝這項 0。</p>

        <div class="blocks" :class="{ loading }">
          <div class="b-row head">
            <span>項目</span>
            <span>範本 → 調整後</span>
            <span>實際金額</span>
          </div>
          <div v-for="b in template.blocks" :key="b.key" class="b-row">
            <label class="check b-name">
              <input v-model="choices[b.key].checked" type="checkbox" />
              <span>{{ b.name }}</span>
            </label>
            <span class="num pct">
              {{ b.percent }}%
              <template v-if="row(b.key) && row(b.key)!.actualPercent !== b.percent">→ <b>{{ row(b.key)!.actualPercent }}%</b></template>
            </span>
            <span class="amt">
              <input
                v-model="choices[b.key].amount"
                class="input num"
                inputmode="numeric"
                :disabled="!choices[b.key].checked"
                :placeholder="row(b.key) ? String(row(b.key)!.amount) : ''"
              />
              <span v-if="row(b.key)?.delta" class="delta num" :class="row(b.key)!.delta > 0 ? 'good' : 'bad'">
                {{ row(b.key)!.locked ? '你填的' : '調整' }} {{ signed(row(b.key)!.delta) }}
              </span>
            </span>
            <span v-if="b.hint" class="b-hint">{{ b.hint }}</span>
          </div>
        </div>

        <label class="field">
          儲蓄最少佔收入幾 %（選填）
          <input v-model="savingsMin" class="input num short" inputmode="decimal" placeholder="例如 10" />
          <span class="hint">其他項目被擠壓時，儲蓄最後才動，也不會低於這個比例</span>
        </label>
      </section>

      <section v-if="preview" class="panel pad">
        <p v-for="e in preview.errors" :key="e" class="bad">{{ e }}</p>
        <p v-for="w in preview.warnings" :key="w" class="notice-line">{{ w }}</p>
        <template v-if="preview.meals">
          <span class="label">第一期 {{ shortDate(preview.meals.periodStart) }} – {{ shortDate(preview.meals.periodEnd) }}・平日 {{ preview.meals.weekdays }} 天、假日 {{ preview.meals.holidays }} 天</span>
          <div class="today">
            <span class="label">餐費平均每天</span>
            <span class="hero"><span class="hero-unit">NT$</span>{{ money(preview.meals.perDayAverage) }}</span>
          </div>
          <ul v-if="preview.meals.slots" class="meals">
            <li v-for="s in preview.meals.slots" :key="s.name">
              <span>{{ s.name }}</span>
              <span class="num">平日 {{ money(s.workday) }}・假日 {{ money(s.holiday) }}</span>
            </li>
          </ul>
          <p v-else class="muted small">每餐金額要等分配器算（{{ preview.meals.problem }}）；套用後可以在設定頁的餐費表先自己填。</p>
        </template>
        <p v-if="preview.unallocated > 0" class="muted small num">沒分配到的 {{ money(preview.unallocated) }} 會留在待分配池。</p>
        <p class="muted small">電信、訂閱等固定支出的金額之後在設定頁補上。</p>
        <p class="fine">
          範本數字是從常見理財法則（例如 50/30/20）出發、依租屋與外食情況調整的起點，不是統計資料。僅供起點參考，請依個人狀況調整。
        </p>
      </section>

      <div class="actions">
        <button type="button" class="btn" @click="step = 1">上一步</button>
        <button type="button" class="btn primary" :disabled="!canApply || applying" @click="apply">{{ applying ? '套用中…' : '套用' }}</button>
      </div>
    </template>

    <!-- 3. 唯一要先懂的概念 -->
    <template v-if="step === 3">
      <section class="panel pad concept">
        <b class="big">沒回報＝照預算花。<br />省下的錢要回報才算數。</b>
        <p class="muted">每天每個時段都有額度。忙到沒回報，系統當你剛好花完；花得比較少，回報了才會存進待分配池。</p>
      </section>

      <div v-if="demo === 'overspend'" class="demo">
        <span class="muted small">示範（不會存）：午餐額度 120，實際花了 200。往右滑確認。</span>
        <div
          class="card"
          :style="{ transform: `translateX(${dragX}px) rotate(${dragX / 30}deg)` }"
          @pointerdown="down"
          @pointermove="move"
          @pointerup="up"
          @pointercancel="up"
        >
          <span class="muted small">今天・餐費</span>
          <b class="c-name">午餐</b>
          <span class="c-amt num">實際 200</span>
          <span class="muted small">額度 120</span>
        </div>
        <button type="button" class="btn block" @click="demo = 'overspent'">或按這裡確認</button>
      </div>
      <section v-else-if="demo === 'overspent'" class="panel pad">
        <b>超支 80</b>
        <p>先從待分配池扣；池子不夠的部分，平均攤到之後的日子，每天少一點（每個時段最多扣到一半）。</p>
        <button type="button" class="btn block" @click="demo = 'unreported'">下一張</button>
      </section>
      <section v-else class="panel pad">
        <b>晚餐・還沒回報</b>
        <p>過了這個時段沒回報，就當作照預算花了 150，不影響其他日子。要記得回報的是「比預算少」和「比預算多」的時候。</p>
        <button type="button" class="btn primary block" @click="finish">開始使用</button>
      </section>
    </template>
  </div>
</template>

<style scoped>
.welcome {
  display: flex;
  flex-direction: column;
  gap: 16px;
  max-width: 640px;
}
.pad {
  padding: 16px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.q {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.q-label,
.label {
  font-size: 12px;
  color: var(--muted);
  letter-spacing: 0.06em;
}
.seg {
  display: grid;
  grid-template-columns: 1fr 1fr;
  border: 1px solid var(--line);
  border-radius: 4px;
  overflow: hidden;
}
.seg button {
  padding: 10px 8px;
  background: none;
  border: 0;
  font: inherit;
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
.short {
  max-width: 120px;
}
.small {
  font-size: 12px;
}
.block {
  width: 100%;
}
.t-head {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
}
.blocks {
  display: flex;
  flex-direction: column;
  transition: opacity 0.15s;
}
.blocks.loading {
  opacity: 0.6;
}
.b-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) 120px;
  gap: 8px;
  align-items: center;
  padding: 8px 0;
  border-top: 1px solid var(--line-2);
  font-size: 14px;
}
.b-row.head {
  border-top: 0;
  font-size: 12px;
  color: var(--muted);
}
.amt {
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.amt .input {
  width: 100%;
  min-width: 0;
}
.delta {
  font-size: 11px;
}
.b-hint {
  grid-column: 1 / -1;
  font-size: 12px;
  color: var(--muted);
  margin-top: -4px;
}
.today {
  display: flex;
  flex-direction: column;
}
.meals {
  list-style: none;
  margin: 0;
  padding: 0;
}
.meals li {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  padding: 4px 0;
  font-size: 14px;
}
.fine {
  font-size: 11px;
  color: var(--muted);
  border-top: 1px solid var(--line-2);
  padding-top: 8px;
}
.actions {
  display: flex;
  justify-content: space-between;
  gap: 8px;
}
.big {
  font-size: 20px;
  line-height: 1.5;
}
.concept {
  border-left: 3px solid var(--accent);
}
.demo {
  display: flex;
  flex-direction: column;
  gap: 10px;
  overflow-x: clip;
}
.card {
  margin: 0 20px;
  background: var(--surface);
  border: 1px solid var(--ink);
  border-radius: 6px;
  padding: 16px 18px;
  display: flex;
  flex-direction: column;
  gap: 4px;
  touch-action: pan-y;
  user-select: none;
  cursor: grab;
}
.c-name {
  font-size: 20px;
}
.c-amt {
  font-size: 26px;
  font-weight: 600;
}
@media (max-width: 420px) {
  .b-row {
    grid-template-columns: minmax(0, 1fr) 96px;
  }
  .b-row .pct {
    grid-column: 1;
    grid-row: 2;
    font-size: 12px;
    color: var(--muted);
  }
  .b-row .amt {
    grid-row: 1 / 3;
    grid-column: 2;
  }
  .b-row.head > span:nth-child(2) {
    display: none;
  }
}
</style>
