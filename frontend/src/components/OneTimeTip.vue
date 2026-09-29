<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { markTipSeen } from '../api/endpoints'
import { store } from '../lib/store'

/**
 * 一次性提示（§20.9）：概念第一次出現時才教，每個提示只出現一次。
 * 出現當下就記成看過（下次打開不再出現），這次畫面上仍然顯示到關掉為止。
 */
const props = defineProps<{ tip: string }>()
const seenBefore = store.me?.seenTips.includes(props.tip) ?? true
const show = computed(() => !seenBefore)

onMounted(async () => {
  if (seenBefore || !store.me) return
  try {
    store.me.seenTips = await markTipSeen(props.tip)
  } catch {
    /* 記不起來下次再顯示一次也無妨 */
  }
})
</script>

<template>
  <p v-if="show" class="tip"><slot /></p>
</template>

<style scoped>
.tip {
  font-size: 13px;
  border-left: 2px solid var(--accent);
  padding: 6px 10px;
  margin: 0;
  color: var(--ink-2);
}
</style>
