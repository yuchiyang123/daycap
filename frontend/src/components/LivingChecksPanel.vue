<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import LivingCheckSheet from './LivingCheckSheet.vue'
import { deleteLivingCheck, listLivingChecks } from '../api/endpoints'
import type { LivingCheckSummary } from '../api/types'
import { loadCurrentPeriod, store } from '../lib/store'
import { money, shortDate, signed } from '../lib/format'

/** 資產頁：生活錢對帳的按鈕與歷史（差額取自本期重播結果）。 */
const checks = ref<LivingCheckSummary[]>([])
const open = ref(false)
const error = ref<string | null>(null)

async function load() {
  try {
    checks.value = await listLivingChecks()
  } catch (e) {
    error.value = (e as Error).message
  }
}
onMounted(() => {
  void load()
  void loadCurrentPeriod() // 差額在本期的重播結果裡；資產頁不一定載入過
})

// 本期的差額（第一次是起點，沒有差額）
const diffs = computed(() => new Map((store.period?.reconciliations ?? []).filter((r) => r.living).map((r) => [r.id, r])))

async function remove(id: number) {
  error.value = null
  try {
    await deleteLivingCheck(id)
    await Promise.all([load(), loadCurrentPeriod(true)])
  } catch (e) {
    error.value = (e as Error).message
  }
}
</script>

<template>
  <div class="panel">
    <div class="head">
      <p class="muted small">
        薪轉戶、生活帳戶沒登記也能對：填它們的總額和信用卡還沒繳的金額，看跟記帳算出來「應該剩」的差多少。建議繳完卡費、月結前各對一次。
      </p>
      <button type="button" class="btn sm primary" @click="open = true">生活錢對帳</button>
    </div>
    <ul v-if="checks.length" class="list">
      <li v-for="(c, i) in checks" :key="c.id" class="row">
        <span class="d num muted">{{ shortDate(c.date) }}</span>
        <span class="t">
          <span class="num">帳戶 {{ money(c.accountsTotal) }} − 卡費 {{ money(c.unpaidCard) }} = <b>{{ money(c.net) }}</b></span>
          <span v-if="c.note" class="muted small">・{{ c.note }}</span>
        </span>
        <span class="a num">
          <template v-if="diffs.get(c.id)">
            <span :class="diffs.get(c.id)!.diff < 0 ? 'bad' : diffs.get(c.id)!.diff > 0 ? 'good' : 'muted'">{{ signed(diffs.get(c.id)!.diff) }}</span>
          </template>
          <span v-else-if="i === checks.length - 1" class="muted small">起點</span>
          <span v-else class="muted small">—</span>
        </span>
        <button type="button" class="btn quiet sm danger" @click="remove(c.id)">刪除</button>
      </li>
    </ul>
    <p v-if="error" class="error-box" style="margin: 0 16px 12px">{{ error }}</p>
    <LivingCheckSheet v-if="open" @close="open = false" @saved="load()" />
  </div>
</template>

<style scoped>
.head {
  display: flex;
  gap: 12px;
  align-items: center;
  justify-content: space-between;
  padding: 12px 16px;
}
.head p {
  margin: 0;
}
.head .btn {
  flex-shrink: 0;
}
.small {
  font-size: 12px;
}
.row {
  display: grid;
  grid-template-columns: 44px minmax(0, 1fr) auto auto;
  gap: 10px;
  align-items: center;
  padding: 9px 16px;
  font-size: 13px;
}
.d {
  font-size: 12px;
}
.a {
  font-weight: 600;
}
</style>
