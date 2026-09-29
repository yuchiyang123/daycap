<script setup lang="ts">
import Skeleton from './Skeleton.vue'
import { onMounted, ref } from 'vue'
import { api } from '../api/http'
import { currentSubscription, isStandalone, pushSupported, subscribePush, unsubscribePush } from '../lib/push'

/**
 * 每晚通知（§9.4）：一天最多一則，點開就是今天的卡片疊；超支、發薪等當天的通知併進這一則。
 * 每台裝置各自訂閱；時間和開關是帳號層級。
 */
interface PushSettings {
  enabled: boolean
  time: string
  devices: number
}
const settings = ref<PushSettings | null>(null)
const loaded = ref(false)
const subscribed = ref(false)
const busy = ref(false)
const msg = ref<string | null>(null)
const error = ref<string | null>(null)
const supported = pushSupported()
const ios = /iPhone|iPad/.test(navigator.userAgent)

onMounted(async () => {
  settings.value = await api<PushSettings>('/api/push/settings').catch(() => null)
  loaded.value = true
  subscribed.value = !!(await currentSubscription().catch(() => null))
})

async function run(fn: () => Promise<void>) {
  busy.value = true
  error.value = null
  msg.value = null
  try {
    await fn()
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}

const toggleDevice = () =>
  run(async () => {
    if (subscribed.value) await unsubscribePush()
    else await subscribePush()
    subscribed.value = !!(await currentSubscription())
    settings.value = await api<PushSettings>('/api/push/settings')
  })

const save = () =>
  run(async () => {
    settings.value = await api<PushSettings>('/api/push/settings', { method: 'PUT', body: settings.value })
    msg.value = '已儲存'
  })

const test = () =>
  run(async () => {
    const r = await api<{ delivered: number }>('/api/push/test', { method: 'POST' })
    msg.value = r.delivered > 0 ? `已送出到 ${r.delivered} 台裝置` : '沒有送達任何裝置，先在這台裝置打開通知'
  })
</script>

<template>
  <div class="panel push">
    <p class="muted small">每晚一則：提醒還沒確認的時段，點開就是今天的卡片。一天最多一則，其他通知會併進來。</p>

    <p v-if="!supported" class="notice-line">
      {{ ios && !isStandalone() ? 'iPhone 要先用 Safari「分享 → 加入主畫面」，再從主畫面打開日額，才能收通知。' : '這個瀏覽器不支援網頁通知。' }}
    </p>
    <div v-else class="row">
      <span>這台裝置{{ subscribed ? '已經會收到通知' : '還沒打開通知' }}</span>
      <button type="button" class="btn sm" :disabled="busy" @click="toggleDevice">{{ subscribed ? '這台不要收' : '在這台打開' }}</button>
    </div>

    <Skeleton v-if="!loaded" :rows="2" />
    <template v-else-if="settings">
      <label class="check">
        <input v-model="settings.enabled" type="checkbox" />
        <span>每晚通知<span class="hint">關掉就所有裝置都不發</span></span>
      </label>
      <label class="field">
        幾點發
        <input v-model="settings.time" type="time" class="input short" />
      </label>
      <div class="row">
        <span class="muted small">已訂閱 {{ settings.devices }} 台裝置</span>
        <span class="btns">
          <button type="button" class="btn sm" :disabled="busy || settings.devices === 0" @click="test">現在送一則試試</button>
          <button type="button" class="btn sm primary" :disabled="busy" @click="save">儲存</button>
        </span>
      </div>
    </template>
    <p v-if="msg" class="muted small">{{ msg }}</p>
    <p v-if="error" class="error-box">{{ error }}</p>
  </div>
</template>

<style scoped>
.push {
  padding: 14px 16px;
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.btns {
  display: flex;
  gap: 8px;
}
.short {
  max-width: 140px;
}
.small {
  font-size: 12px;
}
</style>
