<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { logout } from '../api/endpoints'
import { unlock } from '../lib/applock'

/** App 鎖畫面（§21.3）：蓋住整個 app，用 passkey 解鎖。 */
const busy = ref(false)
const failed = ref(false)

async function tryUnlock() {
  busy.value = true
  failed.value = !(await unlock())
  busy.value = false
}
onMounted(tryUnlock)

async function signOut() {
  await logout()
  window.location.href = '/login'
}
</script>

<template>
  <div class="lock" role="dialog" aria-modal="true" aria-label="日額已上鎖">
    <div class="box">
      <b class="brand">日額</b>
      <p class="muted">已上鎖</p>
      <button type="button" class="btn primary" :disabled="busy" @click="tryUnlock">{{ busy ? '驗證中…' : '解鎖' }}</button>
      <p v-if="failed" class="muted small">沒有解開。可以再試一次；這台裝置的 passkey 不見了就登出重新登入。</p>
      <button type="button" class="btn quiet sm" @click="signOut">登出</button>
    </div>
  </div>
</template>

<style scoped>
.lock {
  position: fixed;
  inset: 0;
  z-index: 1000;
  background: var(--page);
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 16px;
}
.box {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  text-align: center;
  max-width: 320px;
}
.brand {
  font-size: 28px;
  letter-spacing: 0.1em;
}
.small {
  font-size: 12px;
}
</style>
