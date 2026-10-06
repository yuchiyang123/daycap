<script setup lang="ts">
import { RouterLink, useRoute } from 'vue-router'

/** App 內的 404：網址打錯或頁面已經不存在。 */
const route = useRoute()
</script>

<template>
  <main class="nf">
    <p class="code num">404</p>
    <h1>找不到這個頁面</h1>
    <p class="muted">網址 <code>{{ route.fullPath }}</code> 不存在，可能打錯了，或是這個頁面已經搬走。</p>
    <div class="actions">
      <RouterLink to="/" class="btn primary">回今天</RouterLink>
      <RouterLink to="/overview" class="btn">去總覽</RouterLink>
    </div>
  </main>
</template>

<style scoped>
.nf {
  min-height: 100vh;
  min-height: 100dvh;
  max-width: 480px;
  margin: 0 auto;
  padding: 24px var(--gutter);
  display: flex;
  flex-direction: column;
  justify-content: center;
  gap: 12px;
}
.code {
  font-size: 72px;
  font-weight: 700;
  line-height: 1;
  letter-spacing: -0.02em;
  color: var(--accent);
  font-family: var(--num-font, inherit);
}
h1 {
  font-size: 22px;
}
code {
  font-size: 13px;
  background: var(--sunk);
  padding: 1px 6px;
  border-radius: 3px;
  word-break: break-all;
}
.actions {
  display: flex;
  gap: 8px;
  margin-top: 8px;
}
</style>
