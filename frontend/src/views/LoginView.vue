<script setup lang="ts">
import { ref } from 'vue'
import { useRoute } from 'vue-router'
import { externalLoginUrl, login } from '../api/endpoints'
import { ApiError } from '../api/http'

const route = useRoute()
const userName = ref('')
const password = ref('')
const busy = ref(false)
const error = ref<string | null>(null)

async function submit() {
  if (!userName.value || !password.value) return
  busy.value = true
  error.value = null
  try {
    await login(userName.value, password.value)
    // 整頁重新載入，讓所有狀態從已登入開始
    window.location.href = (route.query.redirect as string) || '/'
  } catch (e) {
    error.value =
      e instanceof ApiError && e.status === 429 ? '失敗太多次，請 10 分鐘後再試。' : '帳號或密碼錯誤。'
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="login">
    <div class="box">
      <div class="brand">
        <span class="mark">日額</span>
        <span class="muted">每天能花多少，先排好</span>
      </div>

      <form class="form" @submit.prevent="submit">
        <label class="field">
          帳號
          <input v-model="userName" class="input" autocomplete="username" autofocus />
        </label>
        <label class="field">
          密碼
          <input v-model="password" class="input" type="password" autocomplete="current-password" />
        </label>
        <p v-if="error" class="error-box">{{ error }}</p>
        <button class="btn primary block" :disabled="busy">{{ busy ? '登入中' : '登入' }}</button>
      </form>

      <div class="or"><span>或</span></div>
      <div class="ext">
        <a class="btn block" :href="externalLoginUrl('google')">使用 Google 登入</a>
        <a class="btn block" :href="externalLoginUrl('github')">使用 GitHub 登入</a>
      </div>
      <p class="muted foot">帳號共用 matthewyu.uk 的 Mini-SSO。</p>
    </div>
  </div>
</template>

<style scoped>
.login {
  min-height: 100dvh;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px var(--gutter);
}
.box {
  width: 100%;
  max-width: 360px;
  display: flex;
  flex-direction: column;
  gap: 20px;
}
.brand {
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.mark {
  font-size: 32px;
  font-weight: 700;
  letter-spacing: 0.12em;
}
.form,
.ext {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.or {
  display: flex;
  align-items: center;
  gap: 12px;
  color: var(--muted);
  font-size: 12px;
}
.or::before,
.or::after {
  content: '';
  flex: 1;
  height: 1px;
  background: var(--line);
}
.foot {
  font-size: 12px;
  text-align: center;
}
</style>
