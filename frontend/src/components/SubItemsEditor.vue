<script setup lang="ts">
import { ref } from 'vue'
import type { CategoryDto } from '../api/types'
import { money } from '../lib/format'

/** 類別細項（§13）：名稱＋每期上限（選填）。上限只用來提醒哪一項超了，不改變扣款規則。 */
const props = defineProps<{ cat: CategoryDto }>()
const emit = defineEmits<{ touch: [] }>()

const name = ref('')
const cap = ref('')

function add() {
  const n = name.value.trim()
  if (!n) return
  const list = (props.cat.subItems ??= [])
  if (list.some((x) => x.name === n)) return
  list.push({ name: n, cap: cap.value.trim() ? Math.round(Number(cap.value)) : null })
  name.value = ''
  cap.value = ''
  emit('touch')
}
function remove(i: number) {
  props.cat.subItems?.splice(i, 1)
  emit('touch')
}
</script>

<template>
  <div class="subs">
    <span class="muted small">細項（選填）：回報時可以標，例如娛樂 → 電影、遊戲；設上限的超過了會提醒</span>
    <ul v-if="cat.subItems?.length" class="chips">
      <li v-for="(s, i) in cat.subItems" :key="s.name">
        {{ s.name }}<span v-if="s.cap !== null" class="muted num">・上限 {{ money(s.cap) }}</span>
        <button type="button" class="x" :aria-label="`移除${s.name}`" @click="remove(i)">移除</button>
      </li>
    </ul>
    <div class="add">
      <input v-model="name" class="input" maxlength="30" placeholder="細項名稱" @keydown.enter.prevent="add" />
      <input v-model="cap" class="input num" inputmode="numeric" placeholder="上限（選填）" @keydown.enter.prevent="add" />
      <button type="button" class="btn sm" :disabled="!name.trim()" @click="add">加入</button>
    </div>
  </div>
</template>

<style scoped>
.subs {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 0 2px;
}
.small {
  font-size: 12px;
}
.chips {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}
.chips li {
  border: 1px solid var(--line);
  border-radius: 4px;
  padding: 2px 4px 2px 8px;
  font-size: 13px;
  display: flex;
  align-items: center;
  gap: 4px;
}
.x {
  background: none;
  border: 0;
  color: var(--muted);
  font-size: 12px;
  cursor: pointer;
  padding: 2px 4px;
}
.add {
  display: grid;
  grid-template-columns: minmax(0, 1fr) 110px auto;
  gap: 6px;
}
.add .input {
  min-width: 0;
}
</style>
