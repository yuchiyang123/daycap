<script setup lang="ts">
import { computed } from 'vue'
import type { EntryPreview } from '../api/types'
import { money, signed } from '../lib/format'

/** 按下送出前，用後端試算結果說清楚「這筆會造成什麼影響」。 */
const props = defineProps<{ preview: EntryPreview | null; loading: boolean; envelope?: boolean }>()

const lines = computed(() => {
  const p = props.preview
  if (!p) return []
  const e = p.entry
  const out: { text: string; tone?: 'good' | 'bad' }[] = []
  if (props.envelope) {
    out.push({ text: `這個分類本期剩 ${money(p.categoryRemainingBefore)} → ${money(p.categoryRemainingAfter)}` })
    if (e.envelopeOver > 0) out.push({ text: `超過月額度 ${money(e.envelopeOver)}，從待分配池扣`, tone: 'bad' })
    return out
  }
  if (e.diff === 0) {
    out.push({ text: '剛好照預算，不影響其他日子' })
  } else if (e.diff < 0) {
    out.push({ text: `少花 ${money(-e.diff)}，存進待分配池`, tone: 'good' })
  } else {
    out.push({ text: `超支 ${money(e.diff)}`, tone: 'bad' })
    if (e.fromPool > 0) out.push({ text: `待分配池先扣 ${money(e.fromPool)}` })
    if (e.spread > 0) out.push({ text: `接下來 ${e.spreadDays} 天每天少約 ${money(e.spreadPerDay)}，共 ${money(e.spread)}` })
    if (e.unabsorbed > 0) out.push({ text: `${money(e.unabsorbed)} 攤不下去（護欄：每個時段最多扣到一半），下面選怎麼處理`, tone: 'bad' })
  }
  return out
})
</script>

<template>
  <div class="impact" :class="{ loading }" aria-live="polite">
    <template v-if="preview">
      <p v-for="(l, i) in lines" :key="i" :class="l.tone">{{ l.text }}</p>
      <div class="pool num">
        <span>待分配池</span>
        <span>
          {{ money(preview.poolBefore) }} → <b>{{ money(preview.poolAfter) }}</b>
          <span class="muted">（{{ signed(preview.poolAfter - preview.poolBefore) }}）</span>
        </span>
      </div>
    </template>
    <p v-else class="muted">輸入金額後會顯示影響</p>
  </div>
</template>

<style scoped>
.impact {
  border-left: 2px solid var(--ink);
  background: var(--sunk);
  padding: 10px 12px;
  font-size: 14px;
  display: flex;
  flex-direction: column;
  gap: 4px;
  transition: opacity 0.15s;
}
.impact.loading {
  opacity: 0.6;
}
.pool {
  display: flex;
  justify-content: space-between;
  gap: 8px;
  font-size: 13px;
  color: var(--ink-2);
  margin-top: 4px;
  padding-top: 6px;
  border-top: 1px solid var(--line-2);
}
</style>
