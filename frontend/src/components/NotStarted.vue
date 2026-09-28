<script setup lang="ts">
import { RouterLink } from 'vue-router'
import type { NotStartedDto } from '../api/types'
import { dayLabel, shortDate } from '../lib/format'

/** 設了開始日期、還沒到：不排額度、不計算，只告訴你哪天開始。 */
defineProps<{ info: NotStartedDto }>()
</script>

<template>
  <div class="page">
    <header class="page-head">
      <div>
        <h1>還沒開始</h1>
        <p class="sub">開始日期之前不排額度、不計算</p>
      </div>
    </header>

    <section class="panel box">
      <span class="label">開始日期</span>
      <span class="hero">{{ dayLabel(info.startDate) }}</span>
      <p class="ink-2">
        還有 <b class="num">{{ info.daysUntilStart }}</b> 天。第一期是 {{ shortDate(info.firstPeriodStart) }} – {{ shortDate(info.firstPeriodEnd) }}，
        之後每期從週期起始日開始。
      </p>
    </section>

    <section class="section">
      <h2 class="section-title">在那之前</h2>
      <ol class="panel steps">
        <li>到設定填好實拿月薪、發薪日，還有食衣住行育樂各佔多少。</li>
        <li>把房租、帳單、訂閱這些固定支出填進去（可以選月繳、季繳、年繳）。</li>
        <li>資產頁可以先把存款和持股填好，理財目標不受開始日期影響。</li>
      </ol>
    </section>

    <div class="actions">
      <RouterLink to="/assets" class="btn">資產</RouterLink>
      <RouterLink to="/settings" class="btn primary">前往設定</RouterLink>
    </div>
  </div>
</template>

<style scoped>
.box {
  padding: 20px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.label {
  font-size: 12px;
  color: var(--muted);
  letter-spacing: 0.06em;
}
.hero {
  font-size: 32px;
}
.steps {
  margin: 0;
  padding: 14px 16px 14px 36px;
  display: flex;
  flex-direction: column;
  gap: 8px;
  font-size: 14px;
}
.actions {
  display: flex;
  gap: 8px;
  justify-content: flex-end;
}
</style>
