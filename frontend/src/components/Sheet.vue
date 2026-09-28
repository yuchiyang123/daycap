<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue'

/** 手機是從底部滑出的面板，寬螢幕是置中的對話框。Esc / 點背景關閉。 */
const props = defineProps<{ title: string; subtitle?: string }>()
const emit = defineEmits<{ close: [] }>()
const box = ref<HTMLElement | null>(null)

function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape') emit('close')
}

onMounted(async () => {
  document.addEventListener('keydown', onKey)
  document.body.style.overflow = 'hidden'
  await nextTick()
  const body = box.value?.querySelector('.body')
  ;(body?.querySelector<HTMLElement>('[autofocus]') ?? body?.querySelector<HTMLElement>('input, select, button'))?.focus()
})
onBeforeUnmount(() => {
  document.removeEventListener('keydown', onKey)
  document.body.style.overflow = ''
})
void props
</script>

<template>
  <div class="backdrop" @click.self="emit('close')">
    <section ref="box" class="sheet" role="dialog" aria-modal="true" :aria-label="title">
      <header>
        <div>
          <h2>{{ title }}</h2>
          <p v-if="subtitle" class="sub">{{ subtitle }}</p>
        </div>
        <button class="btn quiet sm" type="button" @click="emit('close')">關閉</button>
      </header>
      <div class="body">
        <slot />
      </div>
    </section>
  </div>
</template>

<style scoped>
.backdrop {
  position: fixed;
  inset: 0;
  z-index: 50;
  background: rgba(10, 10, 9, 0.4);
  display: flex;
  align-items: flex-end;
  justify-content: center;
}
.sheet {
  width: 100%;
  max-width: 520px;
  max-height: 92dvh;
  overflow-y: auto;
  background: var(--surface);
  border-top: 1px solid var(--line-2);
  border-radius: 10px 10px 0 0;
  padding: 16px var(--gutter) calc(20px + env(safe-area-inset-bottom, 0px));
}
header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 12px;
  margin-bottom: 14px;
}
h2 {
  font-size: 17px;
  font-weight: 700;
}
.sub {
  font-size: 13px;
  color: var(--muted);
  margin-top: 2px;
}
.body {
  display: flex;
  flex-direction: column;
  gap: 14px;
}
@media (min-width: 720px) {
  .backdrop {
    align-items: center;
  }
  .sheet {
    border: 1px solid var(--line-2);
    border-radius: 8px;
    padding-bottom: 20px;
  }
}
</style>
