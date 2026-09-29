<script setup lang="ts">
import { computed, onActivated, onMounted, ref } from 'vue'
import AllocationBar, { type Segment } from '../charts/AllocationBar.vue'
import LineChart, { type Series } from '../charts/LineChart.vue'
import Meter from '../charts/Meter.vue'
import { getAssets, saveAssets } from '../api/endpoints'
import type { AccountEdit, AccountType, AssetsView, GoalDto, GoalScope, HoldingDto } from '../api/types'
import ReconcileSheet from '../components/ReconcileSheet.vue'
import DebtsPanel from '../components/DebtsPanel.vue'
import InstallmentsPanel from '../components/InstallmentsPanel.vue'
import TransferSheet from '../components/TransferSheet.vue'
import { money, pct, shortDate, signed } from '../lib/format'

const data = ref<AssetsView | null>(null)
const error = ref<string | null>(null)
const editing = ref(false)
const saving = ref(false)
const refreshing = ref(false)

// 編輯用的草稿
const cash = ref<AccountEdit[]>([])
const sheet = ref<'reconcile' | 'transfer' | null>(null)
const typeLabel: Record<AccountType, string> = { Bank: '銀行', Cash: '現金', EWallet: '電子支付', CreditCard: '信用卡' }
const holdings = ref<HoldingDto[]>([])
const goals = ref<GoalDto[]>([])

async function load(refresh = false) {
  try {
    data.value = await getAssets(refresh)
    error.value = null
  } catch (e) {
    error.value = (e as Error).message
  }
}
onMounted(() => load())
// 從別頁切回來：先顯示上次的資料，背景再抓一次（分帳、分期、對帳都會改到這頁）
let firstActivation = true
onActivated(() => {
  if (firstActivation) firstActivation = false
  else void load()
})

async function refreshQuotes() {
  refreshing.value = true
  await load(true)
  refreshing.value = false
}

function startEdit() {
  if (!data.value) return
  cash.value = data.value.cashAccounts.map((c) => ({ id: c.id, name: c.name, type: c.type, openingBalance: null }))
  holdings.value = data.value.holdings.map(({ id, symbol, name, shares, avgCost, manualPrice }) => ({ id, symbol, name, shares, avgCost, manualPrice }))
  goals.value = data.value.goals.map(({ id, name, targetAmount, targetDate, scope }) => ({ id, name, targetAmount, targetDate, scope }))
  editing.value = true
}

async function save() {
  saving.value = true
  error.value = null
  try {
    data.value = await saveAssets({
      cashAccounts: cash.value.map((c) => ({
        ...c,
        openingBalance: c.id === 0 && c.openingBalance !== null && (c.openingBalance as unknown) !== '' ? Math.round(Number(c.openingBalance) || 0) : null,
      })),
      holdings: holdings.value.map((h) => ({
        ...h,
        shares: Number(h.shares) || 0,
        avgCost: Number(h.avgCost) || 0,
        manualPrice: h.manualPrice === null || (h.manualPrice as unknown) === '' ? null : Number(h.manualPrice),
      })),
      goals: goals.value.map((g) => ({ ...g, targetAmount: Math.round(Number(g.targetAmount) || 0) })),
    })
    editing.value = false
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    saving.value = false
  }
}

const total = computed(() => (data.value ? data.value.cashTotal + data.value.investmentTotal : 0))
const pnl = computed(() => (data.value ? data.value.investmentTotal - data.value.costTotal : 0))

const SLOTS = ['--series-1', '--series-2', '--series-3', '--series-4', '--series-5', '--series-6', '--series-7', '--series-8']
const composition = computed<Segment[]>(() => {
  if (!data.value) return []
  const segs: Segment[] = [{ key: 'cash', label: '現金存款', value: data.value.cashTotal, color: 'var(--series-1)' }]
  const hs = [...data.value.holdings].sort((a, b) => b.marketValue - a.marketValue)
  hs.slice(0, 6).forEach((h, i) => segs.push({ key: h.id, label: `${h.symbol} ${h.name}`, value: h.marketValue, color: `var(${SLOTS[i + 1]})` }))
  const rest = hs.slice(6)
  if (rest.length) segs.push({ key: 'rest', label: '其他持股', value: rest.reduce((a, h) => a + h.marketValue, 0), color: 'var(--series-8)', note: rest.map((h) => h.symbol).join('、') })
  return segs
})

const history = computed<{ labels: string[]; series: Series[] }>(() => {
  const h = data.value?.history ?? []
  return {
    labels: h.map((x) => shortDate(x.date)),
    series: [
      { key: 'total', label: '總資產', color: 'var(--series-1)', values: h.map((x) => x.cash + x.investments) },
      { key: 'inv', label: '投資', color: 'var(--series-2)', values: h.map((x) => x.investments) },
      { key: 'cash', label: '現金', color: 'var(--series-3)', values: h.map((x) => x.cash) },
    ],
  }
})

const scopeLabel: Record<GoalScope, string> = { All: '總資產', Cash: '現金', Investments: '投資' }

function addGoal() {
  const d = new Date()
  d.setFullYear(d.getFullYear() + 1)
  goals.value.push({ id: 0, name: '', targetAmount: 100000, targetDate: `${d.getFullYear()}-12-31`, scope: 'All' })
}
</script>

<template>
  <div class="page wide">
    <header class="page-head">
      <div>
        <h1>資產</h1>
        <p class="sub">
          <template v-if="data?.quotesFetchedAt">收盤價更新於 {{ new Date(data.quotesFetchedAt).toLocaleString('zh-TW', { hour12: false }) }}</template>
          <template v-else>股價來源：證交所 / 櫃買中心每日收盤價</template>
        </p>
      </div>
      <div class="head-actions">
        <button v-if="!editing" class="btn sm" :disabled="refreshing" @click="refreshQuotes">{{ refreshing ? '更新中' : '更新股價' }}</button>
        <button v-if="!editing" class="btn sm primary" @click="startEdit">編輯</button>
      </div>
    </header>

    <p v-if="error" class="error-box">{{ error }}</p>

    <template v-if="data && !editing">
      <section class="panel hero-box">
        <div>
          <span class="label">總資產</span>
          <span class="hero"><span class="hero-unit">NT$</span>{{ money(total) }}</span>
        </div>
        <div class="tiles inner">
          <div class="tile">
            <span class="label">現金</span>
            <span class="value">{{ money(data.cashTotal) }}</span>
          </div>
          <div class="tile">
            <span class="label">投資市值</span>
            <span class="value">{{ money(data.investmentTotal) }}</span>
          </div>
          <div class="tile">
            <span class="label">未實現損益</span>
            <span class="value" :class="pnl > 0 ? 'good' : pnl < 0 ? 'bad' : ''">{{ signed(pnl) }}</span>
            <span class="note">{{ data.costTotal ? pct(pnl / data.costTotal, 1) : '—' }}</span>
          </div>
        </div>
      </section>

      <section v-if="data.goals.length" class="section">
        <h2 class="section-title">理財目標</h2>
        <div class="panel">
          <template v-for="(g, i) in data.goals" :key="g.id">
            <hr v-if="i > 0" class="rule" />
            <div class="goal">
              <div class="g-top">
                <span class="g-name">{{ g.name }} <span class="tag">{{ scopeLabel[g.scope] }}</span></span>
                <span class="num"><b>{{ money(g.current) }}</b><span class="muted"> / {{ money(g.targetAmount) }}</span></span>
              </div>
              <Meter :value="g.current" :max="g.targetAmount" :height="10" level="ok" class="goal-meter" />
              <div class="g-bottom num muted">
                <span>{{ pct(g.progress) }}・目標 {{ g.targetDate }}</span>
                <span v-if="g.progress >= 1" class="good">已達成</span>
                <span v-else>還差 {{ money(g.targetAmount - g.current) }}，{{ g.monthsLeft }} 個月內每月需存 {{ money(g.monthlyNeeded) }}</span>
              </div>
            </div>
          </template>
        </div>
      </section>

      <div class="grid-2">
        <section class="section">
          <h2 class="section-title">資產組成</h2>
          <div class="panel panel-pad">
            <AllocationBar v-if="total > 0" :segments="composition" caption="資產組成" />
            <p v-else class="muted">按「編輯」填入存款和持股。</p>
          </div>
        </section>
        <section class="section">
          <h2 class="section-title">走勢<span class="aside">每次開這頁記一筆</span></h2>
          <div class="panel panel-pad">
            <LineChart v-if="data.history.length >= 2" :series="history.series" :labels="history.labels" :height="200" :zero-based="false" />
            <p v-else class="muted">累積兩天以上的紀錄後會畫出走勢。</p>
          </div>
        </section>
      </div>

      <section class="section">
        <h2 class="section-title">持股</h2>
        <div class="panel table-wrap">
          <table class="tbl">
            <thead>
              <tr>
                <th>標的</th>
                <th class="r">股數</th>
                <th class="r">成本</th>
                <th class="r">價格</th>
                <th class="r">市值</th>
                <th class="r">損益</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="h in data.holdings" :key="h.id">
                <td>
                  <b>{{ h.symbol }}</b> {{ h.name }}
                  <span v-if="h.priceSource === 'manual'" class="tag">手動價</span>
                  <span v-else-if="h.priceSource === 'none'" class="tag">抓不到價格</span>
                </td>
                <td class="r">{{ h.shares.toLocaleString('en-US') }}</td>
                <td class="r">{{ h.avgCost.toLocaleString('en-US', { maximumFractionDigits: 2 }) }}</td>
                <td class="r">{{ h.price === null ? '—' : h.price.toLocaleString('en-US', { maximumFractionDigits: 2 }) }}</td>
                <td class="r">{{ money(h.marketValue) }}</td>
                <td class="r" :class="h.pnl > 0 ? 'good' : h.pnl < 0 ? 'bad' : ''">{{ signed(h.pnl) }}</td>
              </tr>
              <tr v-if="data.holdings.length === 0">
                <td colspan="6" class="muted">還沒有持股。</td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>

      <section class="section">
        <h2 class="section-title">應收應付<span class="aside">代墊、借出借入；不算花費</span></h2>
        <DebtsPanel @changed="load()" />
      </section>

      <section class="section">
        <h2 class="section-title">分期付款<span class="aside">每期自動變成固定支出；提前還款不算花費</span></h2>
        <InstallmentsPanel @changed="load()" />
      </section>

      <section class="section">
        <h2 class="section-title">
          帳戶
          <span class="acct-actions">
            <button type="button" class="btn sm" :disabled="data.cashAccounts.length < 2" @click="sheet = 'transfer'">轉帳・繳卡費</button>
            <button type="button" class="btn sm primary" :disabled="data.cashAccounts.length === 0" @click="sheet = 'reconcile'">對帳</button>
          </span>
        </h2>
        <div class="panel">
          <ul class="list">
            <li v-for="c in data.cashAccounts" :key="c.id" class="acct">
              <span class="acct-main">
                <span>{{ c.name }} <span class="tag">{{ typeLabel[c.type] }}</span></span>
                <span class="muted small">
                  {{ c.reconciledOn ? `${shortDate(c.reconciledOn)} 對帳後推算` : '還沒對帳' }}
                  <template v-if="c.type === 'CreditCard' && c.cardSpendThisPeriod">・本期刷卡 {{ money(c.cardSpendThisPeriod) }}（下月卡費預估）</template>
                </span>
              </span>
              <span class="num" :class="c.type === 'CreditCard' && c.balance > 0 ? 'bad' : ''">
                {{ c.type === 'CreditCard' ? `欠 ${money(c.balance)}` : money(c.balance) }}
              </span>
            </li>
          </ul>
          <p v-if="data.cashAccounts.length === 0" class="empty">還沒有帳戶。按「編輯」新增銀行、現金、電子支付或信用卡。</p>
        </div>
        <p class="muted small">餘額是「最近一次對帳 + 之後的轉帳、繳卡費、資產加減、有記付款帳戶的花費」推算的；沒回報的日常花費只有對帳時才會補上。</p>
      </section>
      <ReconcileSheet v-if="sheet === 'reconcile'" @close="sheet = null" @saved="load()" />
      <TransferSheet v-if="sheet === 'transfer'" @close="sheet = null" @saved="load()" />
    </template>

    <!-- ---------- 編輯模式 ---------- -->
    <form v-if="editing" class="edit" @submit.prevent="save">
      <section class="section">
        <h2 class="section-title">帳戶<span class="aside">既有帳戶的餘額用「對帳」更新</span></h2>
        <div class="panel panel-pad rows">
          <div v-for="(c, i) in cash" :key="i" class="row cash-row">
            <input v-model="c.name" class="input compact" placeholder="帳戶名稱" maxlength="40" aria-label="帳戶名稱" />
            <select v-model="c.type" class="select compact" aria-label="類型">
              <option v-for="(label, k) in typeLabel" :key="k" :value="k">{{ label }}</option>
            </select>
            <input
              v-if="c.id === 0"
              v-model.number="c.openingBalance"
              class="input compact num"
              inputmode="numeric"
              :placeholder="c.type === 'CreditCard' ? '目前欠款' : '目前餘額'"
              aria-label="期初餘額"
            />
            <span v-else class="muted small">已建立</span>
            <button type="button" class="btn quiet sm danger" @click="cash.splice(i, 1)">移除</button>
          </div>
          <button type="button" class="btn sm" @click="cash.push({ id: 0, name: '', type: 'Bank', openingBalance: null })">新增帳戶</button>
          <p class="muted small">移除只是封存：舊的對帳、轉帳紀錄都還在。</p>
        </div>
      </section>

      <section class="section">
        <h2 class="section-title">持股<span class="aside">手動價格留空 = 用收盤價</span></h2>
        <div class="panel panel-pad rows">
          <div class="row hold-row head muted">
            <span>代號</span><span>名稱（選填）</span><span>股數</span><span>平均成本</span><span>手動價格</span><span />
          </div>
          <div v-for="(h, i) in holdings" :key="i" class="row hold-row">
            <input v-model="h.symbol" class="input compact" placeholder="0050" maxlength="16" aria-label="代號" />
            <input v-model="h.name" class="input compact" placeholder="自動帶入" maxlength="40" aria-label="名稱" />
            <input v-model.number="h.shares" class="input compact num" inputmode="decimal" placeholder="股數" aria-label="股數" />
            <input v-model.number="h.avgCost" class="input compact num" inputmode="decimal" placeholder="平均成本" aria-label="平均成本" />
            <input v-model="h.manualPrice" class="input compact num" inputmode="decimal" placeholder="手動價格（選填）" aria-label="手動價格" />
            <button type="button" class="btn quiet sm danger" @click="holdings.splice(i, 1)">移除</button>
          </div>
          <button type="button" class="btn sm" @click="holdings.push({ id: 0, symbol: '', name: '', shares: 0, avgCost: 0, manualPrice: null })">新增持股</button>
        </div>
      </section>

      <section class="section">
        <h2 class="section-title">理財目標</h2>
        <div class="panel panel-pad rows">
          <div v-for="(g, i) in goals" :key="i" class="row goal-row">
            <input v-model="g.name" class="input compact" placeholder="例如 緊急預備金 6 個月" maxlength="40" aria-label="目標名稱" />
            <input v-model.number="g.targetAmount" class="input compact num" inputmode="numeric" placeholder="目標金額" aria-label="目標金額" />
            <input v-model="g.targetDate" type="date" class="input compact" aria-label="目標日期" />
            <select v-model="g.scope" class="select compact" aria-label="計算範圍">
              <option value="All">總資產</option>
              <option value="Cash">只算現金</option>
              <option value="Investments">只算投資</option>
            </select>
            <button type="button" class="btn quiet sm danger" @click="goals.splice(i, 1)">移除</button>
          </div>
          <button type="button" class="btn sm" @click="addGoal">新增目標</button>
        </div>
      </section>

      <div class="actions">
        <button type="button" class="btn" @click="editing = false">取消</button>
        <button type="submit" class="btn primary" :disabled="saving">{{ saving ? '儲存中' : '儲存' }}</button>
      </div>
    </form>
  </div>
</template>

<style scoped>
.head-actions {
  display: flex;
  gap: 8px;
}
.hero-box {
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 18px 20px;
}
.label {
  display: block;
  font-size: 12px;
  color: var(--muted);
  letter-spacing: 0.06em;
  margin-bottom: 4px;
}
.tiles.inner {
  border-radius: 4px;
}
.goal {
  padding: 14px 16px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.g-top,
.g-bottom {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  flex-wrap: wrap;
  align-items: baseline;
}
.g-name {
  font-weight: 600;
}
.g-top b {
  font-size: 17px;
}
.g-bottom {
  font-size: 12px;
}
.acct {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 12px;
  padding: 10px 16px;
}
.edit {
  display: flex;
  flex-direction: column;
  gap: 24px;
}
.rows {
  display: flex;
  flex-direction: column;
  gap: 8px;
  align-items: stretch;
}
.rows > .btn {
  align-self: flex-start;
}
.row {
  display: grid;
  gap: 8px;
  align-items: center;
}
.acct-actions {
  display: flex;
  gap: 6px;
  font-weight: 400;
  letter-spacing: 0;
}
.acct-main {
  display: flex;
  flex-direction: column;
  min-width: 0;
}
.small {
  font-size: 12px;
}
.cash-row {
  grid-template-columns: 1fr 110px 130px auto;
}
.hold-row {
  grid-template-columns: 90px 1fr 90px 100px 100px auto;
}
.hold-row.head {
  font-size: 12px;
}
.goal-row {
  grid-template-columns: 1fr 120px 150px 120px auto;
}
@media (max-width: 720px) {
  .cash-row,
  .hold-row,
  .goal-row {
    grid-template-columns: 1fr 1fr;
    padding-bottom: 8px;
    border-bottom: 1px solid var(--line);
  }
  .hold-row.head {
    display: none;
  }
}
.actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}
</style>
