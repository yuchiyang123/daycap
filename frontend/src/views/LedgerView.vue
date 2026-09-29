<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import ExtraSheet from '../components/ExtraSheet.vue'
import AssetSheet from '../components/AssetSheet.vue'
import IncomeSheet from '../components/IncomeSheet.vue'
import TransferSheet from '../components/TransferSheet.vue'
import ReconcileSheet from '../components/ReconcileSheet.vue'
import AllocateSheet from '../components/AllocateSheet.vue'
import { deleteAssetAdjustment, deleteEntry, deleteReconciliation, deleteTransfer2, listAssetAdjustments, listReconciliations, listTransfers } from '../api/endpoints'
import type { AssetAdjustmentView, ReconciliationSummary, TransferView } from '../api/types'
import { loadCurrentPeriod } from '../lib/store'
import { store, setPeriod } from '../lib/store'
import { dayLabel, money, signed } from '../lib/format'

/**
 * 記帳：日額之外的傳統流水。兩種記法——
 * 1. 花費：扣在某個分類的額度上（例如 PSP → 娛樂），超過額度照規則從待定區扣。
 * 2. 資產加減：直接動存款帳戶，不影響預算。
 * 下面把本期的回報、花費、資產加減、薪資調整依日期排在一起。
 */
const period = computed(() => store.period!)
const assetRows = ref<AssetAdjustmentView[]>([])
const transferRows = ref<TransferView[]>([])
const reconRows = ref<ReconciliationSummary[]>([])
const filter = ref<'all' | 'spend' | 'asset'>('all')
const adding = ref<'spend' | 'asset' | 'income' | 'allocate' | 'transfer' | 'reconcile' | null>(null)
const confirmId = ref<string | null>(null)

async function loadAssets() {
  const [a, t, r] = await Promise.all([
    listAssetAdjustments(period.value.startDate, period.value.endDate),
    listTransfers(period.value.startDate, period.value.endDate),
    listReconciliations(),
  ])
  assetRows.value = a
  transferRows.value = t
  reconRows.value = r.filter((x) => x.date >= period.value.startDate && x.date <= period.value.endDate)
}
onMounted(loadAssets)

interface Row {
  key: string
  date: string
  title: string
  sub: string
  amount: number
  kind: 'spend' | 'asset' | 'income'
  tone: '' | 'good' | 'bad'
  del?: () => Promise<void>
}

const rows = computed<Row[]>(() => {
  const out: Row[] = []
  for (const e of period.value.entries) {
    const slot = e.slotName ? `・${e.slotName}` : ''
    let sub = `${e.categoryName}${slot}`
    if (e.slotName) sub += e.diff === 0 ? '・剛好' : e.diff > 0 ? `・超支 ${money(e.diff)}` : `・少花 ${money(-e.diff)}`
    if (e.envelopeOver) sub += `・超出月額度 ${money(e.envelopeOver)}`
    out.push({
      key: `e${e.id}`,
      date: e.date,
      title: e.note || (e.slotName ? `${e.categoryName}${slot}` : e.categoryName),
      sub,
      amount: -e.actual,
      kind: 'spend',
      tone: '',
      del: async () => setPeriod(await deleteEntry(period.value.id, e.id)),
    })
  }
  for (const a of assetRows.value) {
    out.push({
      key: `a${a.id}`,
      date: a.date,
      title: a.note || (a.amount < 0 ? '資產支出' : '資產收入'),
      sub: `資產・${a.accountName}${a.source === 'settlement' ? '・自動結算' : ''}`,
      amount: a.amount,
      kind: 'asset',
      tone: a.amount > 0 ? 'good' : 'bad',
      del:
        a.source === 'manual'
          ? async () => {
              await deleteAssetAdjustment(a.id)
              await loadAssets()
            }
          : undefined,
    })
  }
  for (const t of transferRows.value) {
    out.push({
      key: `t${t.id}`,
      date: t.date,
      title: t.note || (t.kind === 'CardPayment' ? '繳卡費' : '轉帳'),
      sub: `${t.kind === 'CardPayment' ? '繳卡費' : '互轉'}・${t.fromName} → ${t.toName}・不算花費`,
      amount: t.amount,
      kind: 'asset',
      tone: '',
      del: async () => {
        await deleteTransfer2(t.id)
        await loadAssets()
      },
    })
  }
  for (const r of reconRows.value) {
    const rv = period.value.reconciliations.find((x) => x.id === r.id)
    out.push({
      key: `r${r.id}`,
      date: r.date,
      title: r.note || (r.isFull ? '對帳' : '帳戶期初餘額'),
      sub: rv ? `對帳・預期 ${money(rv.expected)}・沒交代 ${signed(rv.diff)}` : `對帳・淨額 ${money(r.net)}${r.isFull ? '（基準）' : ''}`,
      amount: rv ? rv.diff : 0,
      kind: 'asset',
      tone: rv && rv.diff < 0 ? 'bad' : rv && rv.diff > 0 ? 'good' : '',
      del: async () => {
        await deleteReconciliation(r.id)
        await loadAssets()
        await loadCurrentPeriod(true)
      },
    })
  }
  for (const i of period.value.incomeAdjustments) {
    out.push({ key: `i${i.id}`, date: period.value.startDate, title: i.label, sub: '薪資調整', amount: i.amount, kind: 'income', tone: i.amount < 0 ? 'bad' : 'good' })
  }
  return out.sort((a, b) => b.date.localeCompare(a.date))
})

const visible = computed(() =>
  rows.value.filter((r) => filter.value === 'all' || (filter.value === 'spend' ? r.kind === 'spend' : r.kind !== 'spend')),
)
const groups = computed(() => {
  const m = new Map<string, Row[]>()
  for (const r of visible.value) {
    if (!m.has(r.date)) m.set(r.date, [])
    m.get(r.date)!.push(r)
  }
  return [...m.entries()]
})

const spendTotal = computed(() => period.value.entries.reduce((a, e) => a + e.actual, 0))
const assetNet = computed(() => assetRows.value.reduce((a, r) => a + r.amount, 0))

async function remove(r: Row) {
  if (confirmId.value !== r.key) {
    confirmId.value = r.key
    setTimeout(() => {
      if (confirmId.value === r.key) confirmId.value = null
    }, 3000)
    return
  }
  await r.del?.()
  confirmId.value = null
}
</script>

<template>
  <div class="page">
    <header class="page-head">
      <div>
        <h1>記帳</h1>
        <p class="sub">日額以外的花費，或直接動存款</p>
      </div>
    </header>

    <div class="adds">
      <button class="btn primary add" @click="adding = 'spend'">
        <b>記一筆花費</b>
        <span>扣在分類額度上，例如 PSP → 娛樂</span>
      </button>
      <button class="btn add" @click="adding = 'asset'">
        <b>資產加減</b>
        <span>直接動存款帳戶，不影響預算</span>
      </button>
      <button class="btn add" @click="adding = 'transfer'">
        <b>轉帳・繳卡費</b>
        <span>帳戶之間的移動，不算花費</span>
      </button>
      <button class="btn add" @click="adding = 'reconcile'">
        <b>對帳</b>
        <span>填實際餘額，補回沒回報的差額</span>
      </button>
    </div>

    <div class="tiles">
      <div class="tile">
        <span class="label">本期回報的花費</span>
        <span class="value">{{ money(spendTotal) }}</span>
        <span class="note">含每日時段與額外花費</span>
      </div>
      <div class="tile">
        <span class="label">資產加減</span>
        <span class="value" :class="assetNet < 0 ? 'bad' : assetNet > 0 ? 'good' : ''">{{ signed(assetNet) }}</span>
        <span class="note">本期期間</span>
      </div>
      <button class="tile tile-btn" @click="adding = 'income'">
        <span class="label">本期實領</span>
        <span class="value">{{ money(period.income) }}</span>
        <span class="note">點這裡調整薪資</span>
      </button>
      <button class="tile tile-btn" :disabled="period.pool.balance <= 0" @click="adding = 'allocate'">
        <span class="label">待定區</span>
        <span class="value" :class="period.pool.balance < 0 ? 'bad' : ''">{{ money(period.pool.balance) }}</span>
        <span class="note">{{ period.pool.balance > 0 ? '點這裡分配剩餘' : '沒有可分配的' }}</span>
      </button>
    </div>

    <section class="section">
      <h2 class="section-title">
        流水
        <span class="seg" role="group" aria-label="篩選">
          <button type="button" :aria-pressed="filter === 'all'" @click="filter = 'all'">全部</button>
          <button type="button" :aria-pressed="filter === 'spend'" @click="filter = 'spend'">花費</button>
          <button type="button" :aria-pressed="filter === 'asset'" @click="filter = 'asset'">資產・薪資</button>
        </span>
      </h2>
      <div v-if="groups.length === 0" class="panel empty">這期還沒有任何紀錄。沒回報的時段都當作照預算花掉。</div>
      <div v-for="[date, list] in groups" :key="date" class="panel day">
        <div class="day-head">{{ dayLabel(date) }}</div>
        <ul class="list">
          <li v-for="r in list" :key="r.key" class="row">
            <span class="main">
              <span class="t">{{ r.title }}</span>
              <span class="s muted">{{ r.sub }}</span>
            </span>
            <span class="amt num" :class="r.tone">{{ r.kind === 'spend' ? money(r.amount) : signed(r.amount) }}</span>
            <button v-if="r.del" type="button" class="btn quiet sm danger" @click="remove(r)">{{ confirmId === r.key ? '確認' : '刪除' }}</button>
            <span v-else />
          </li>
        </ul>
      </div>
    </section>

    <ExtraSheet v-if="adding === 'spend'" :date="period.today" @close="adding = null" />
    <AssetSheet v-if="adding === 'asset'" @close="adding = null" @saved="loadAssets" />
    <IncomeSheet v-if="adding === 'income'" @close="adding = null" @allocate="adding = 'allocate'" />
    <AllocateSheet v-if="adding === 'allocate'" @close="adding = null" />
    <TransferSheet v-if="adding === 'transfer'" @close="adding = null" @saved="loadAssets" />
    <ReconcileSheet v-if="adding === 'reconcile'" @close="adding = null" @saved="loadAssets" />
  </div>
</template>

<style scoped>
.adds {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(200px, 100%), 1fr));
  gap: 10px;
}
.add {
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  min-height: 64px;
  padding: 10px 14px;
  white-space: normal;
  text-align: left;
}
.add b {
  font-size: 15px;
}
.add span {
  font-size: 12px;
  font-weight: 400;
  opacity: 0.85;
}
.tile .label {
  font-size: 12px;
  color: var(--muted);
}
.tile-btn {
  border: 0;
  text-align: left;
  cursor: pointer;
  font: inherit;
  color: inherit;
}
.tile-btn:hover:not(:disabled) {
  background: var(--sunk);
}
.tile-btn:disabled {
  cursor: default;
}
.section-title .seg {
  font-weight: 400;
  letter-spacing: 0;
}
.section-title .seg button {
  min-height: 28px;
  padding: 0 10px;
  font-size: 12px;
}
.day-head {
  padding: 8px 16px;
  font-size: 12px;
  color: var(--ink-2);
  border-bottom: 1px solid var(--line);
  font-weight: 600;
}
.row {
  display: grid;
  grid-template-columns: 1fr auto 52px;
  gap: 10px;
  align-items: center;
  padding: 10px 16px;
}
.main {
  display: flex;
  flex-direction: column;
  min-width: 0;
}
.t {
  font-size: 15px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.s {
  font-size: 12px;
}
.amt {
  font-weight: 600;
}
</style>
