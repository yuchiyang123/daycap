<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api } from '../api/http'
import { logout } from '../api/endpoints'
import { disableLock, enableLock, lockConfig, lockSupported, setLockMinutes } from '../lib/applock'
import { store } from '../lib/store'

/**
 * 資料與安全（§21）：匯出、登入中的裝置、App 鎖、修改紀錄、刪除帳號。
 */
interface SessionView {
  id: number
  device: string
  firstSeenAt: string
  lastSeenAt: string
  revoked: boolean
  current: boolean
}
interface HistoryItem {
  at: string
  kind: string
  action: string
  summary: string
  id: number
  replacesId: number | null
}

const error = ref<string | null>(null)
const msg = ref<string | null>(null)
const busy = ref(false)

// ---- 裝置 ----
const sessions = ref<SessionView[]>([])
onMounted(async () => {
  sessions.value = await api<SessionView[]>('/api/sessions').catch(() => [])
  lockAvailable.value = await lockSupported()
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
const revoke = (id: number) =>
  run(async () => {
    sessions.value = await api<SessionView[]>(`/api/sessions/${id}/revoke`, { method: 'POST' })
  })
const confirmOthers = ref(false)
function revokeOthers() {
  if (!confirmOthers.value) {
    confirmOthers.value = true
    setTimeout(() => (confirmOthers.value = false), 4000)
    return
  }
  confirmOthers.value = false
  run(async () => {
    sessions.value = await api<SessionView[]>('/api/sessions/revoke-others', { method: 'POST' })
    msg.value = '其他裝置下次打開時會被登出。'
  })
}
const fmt = (iso: string) => new Date(iso).toLocaleString('zh-TW', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })

// ---- App 鎖 ----
const lockAvailable = ref(false)
const lockOn = ref(!!lockConfig())
const lockMinutes = ref(String(lockConfig()?.minutes ?? 1))
const toggleLock = () =>
  run(async () => {
    if (lockOn.value) {
      disableLock()
      lockOn.value = false
      return
    }
    await enableLock(store.me?.userName ?? '', Math.max(0, Number(lockMinutes.value) || 0))
    lockOn.value = true
    msg.value = '已開啟。下次打開、或離開超過設定的時間回來，要先解鎖。'
  })
function saveMinutes() {
  setLockMinutes(Math.max(0, Number(lockMinutes.value) || 0))
}

// ---- 修改紀錄 ----
const history = ref<HistoryItem[] | null>(null)
const loadHistory = () =>
  run(async () => {
    history.value = await api<HistoryItem[]>('/api/me/history?days=30')
  })

// ---- 刪除帳號 ----
const PHRASE = '刪除我的資料'
const deleting = ref(false)
const phrase = ref('')
const deleteAll = () =>
  run(async () => {
    await api<void>('/api/me/delete', { method: 'POST', body: { confirm: phrase.value } })
    disableLock()
    await logout()
    window.location.href = '/login'
  })
</script>

<template>
  <div class="trust">
    <p v-if="error" class="error-box">{{ error }}</p>
    <p v-if="msg" class="muted small">{{ msg }}</p>

    <div class="panel block">
      <b>匯出資料</b>
      <p class="muted small">全部資料（JSON，含每一筆修改與刪除的紀錄），或只有回報與花費（CSV，Excel 打得開）。</p>
      <div class="row">
        <a class="btn sm" href="/api/me/export" download>下載全部（JSON）</a>
        <a class="btn sm" href="/api/me/export/entries.csv" download>下載花費（CSV）</a>
      </div>
    </div>

    <div class="panel block">
      <b>App 鎖</b>
      <p v-if="!lockAvailable" class="muted small">這台裝置不支援（要有 Face ID、指紋或裝置密碼的 passkey）。</p>
      <template v-else>
        <p class="muted small">打開 app 時要用這台裝置的 Face ID / 指紋解鎖。只擋畫面，每台裝置各自設定。</p>
        <div class="row">
          <button type="button" class="btn sm" :class="{ primary: !lockOn }" :disabled="busy" @click="toggleLock">
            {{ lockOn ? '關閉這台的 App 鎖' : '在這台開啟' }}
          </button>
          <label v-if="lockOn" class="inline">
            離開
            <input v-model="lockMinutes" class="input num tiny" inputmode="numeric" @change="saveMinutes" />
            分鐘後回來要解鎖
          </label>
        </div>
      </template>
    </div>

    <div class="panel block">
      <b>登入中的裝置</b>
      <ul v-if="sessions.length" class="sessions">
        <li v-for="s in sessions" :key="s.id" :class="{ muted: s.revoked }">
          <span>
            {{ s.device }}
            <span v-if="s.current" class="tag">這台</span>
            <span v-else-if="s.revoked" class="tag">已登出</span>
            <span class="muted small">・最近 {{ fmt(s.lastSeenAt) }}</span>
          </span>
          <button v-if="!s.current && !s.revoked" type="button" class="btn quiet sm danger" :disabled="busy" @click="revoke(s.id)">登出這台</button>
        </li>
      </ul>
      <p v-else class="muted small">正式站登入後才會記錄。</p>
      <button type="button" class="btn sm" :disabled="busy" @click="revokeOthers">{{ confirmOthers ? '確認：登出其他所有裝置' : '登出其他所有裝置' }}</button>
    </div>

    <div class="panel block">
      <b>修改紀錄</b>
      <p class="muted small">每一筆都只新增不覆寫：改了什麼、什麼時候、取代哪一筆都查得到。</p>
      <button v-if="history === null" type="button" class="btn sm" :disabled="busy" @click="loadHistory">看最近 30 天</button>
      <ul v-else class="history">
        <li v-for="h in history" :key="`${h.kind}-${h.id}-${h.at}`">
          <span class="muted small num">{{ fmt(h.at) }}</span>
          <span><b>{{ h.kind }}</b>・{{ h.action }}<template v-if="h.replacesId">（取代 #{{ h.replacesId }}）</template></span>
          <span class="small">{{ h.summary }}</span>
        </li>
        <li v-if="!history.length" class="muted small">最近 30 天沒有紀錄。</li>
      </ul>
    </div>

    <div class="panel block danger-zone">
      <b>刪除帳號</b>
      <p class="muted small">
        這個站上你的所有資料（設定、回報、帳戶、對帳、罐子、分帳、分期、通知）會真的刪掉，不能復原。先下載一份匯出。
        伺服器每天的備份保留 30 天，之後自動輪替刪除；Mini-SSO 的登入帳號是另一個服務，不會一起刪。
      </p>
      <template v-if="deleting">
        <label class="field">
          輸入「{{ PHRASE }}」確認
          <input v-model="phrase" class="input" />
        </label>
        <div class="row">
          <button type="button" class="btn sm" @click="deleting = false">取消</button>
          <button type="button" class="btn sm danger" :disabled="busy || phrase.trim() !== PHRASE" @click="deleteAll">永久刪除</button>
        </div>
      </template>
      <button v-else type="button" class="btn sm quiet danger" @click="deleting = true">刪除我的帳號資料</button>
    </div>
  </div>
</template>

<style scoped>
.trust {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.block {
  padding: 14px 16px;
  display: flex;
  flex-direction: column;
  gap: 8px;
  align-items: flex-start;
}
.row {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  align-items: center;
}
.small {
  font-size: 12px;
}
.inline {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
}
.tiny {
  width: 56px;
}
.tag {
  margin-left: 6px;
  font-size: 11px;
  border: 1px solid var(--line);
  border-radius: 3px;
  padding: 0 5px;
}
.sessions,
.history {
  list-style: none;
  margin: 0;
  padding: 0;
  width: 100%;
}
.sessions li {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 8px;
  padding: 6px 0;
  border-top: 1px solid var(--line-2);
  font-size: 14px;
}
.history {
  max-height: 360px;
  overflow-y: auto;
}
.history li {
  display: grid;
  grid-template-columns: 96px minmax(0, 1fr);
  gap: 2px 8px;
  padding: 6px 0;
  border-top: 1px solid var(--line-2);
  font-size: 13px;
}
.history li > span:last-child {
  grid-column: 2;
  overflow-wrap: anywhere;
}
.danger-zone {
  border-color: var(--bad-text);
}
</style>
