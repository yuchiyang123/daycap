import { reactive } from 'vue'

/**
 * App 鎖（§21.3）：打開或離開一段時間回來時，要用這台裝置的 passkey（iPhone 是 Face ID）解鎖。
 * 只擋畫面（規格也這樣說）：驗證在這台裝置上做，passkey 的 id 存在這台裝置；伺服器端的保護靠登入與裝置撤銷。
 */
interface LockConfig {
  credId: string
  minutes: number
}

const KEY = 'daycap.applock'
let hiddenAt: number | null = null

export const lockState = reactive({ locked: false })

function read(): LockConfig | null {
  try {
    const raw = localStorage.getItem(KEY)
    return raw ? (JSON.parse(raw) as LockConfig) : null
  } catch {
    return null
  }
}
function write(cfg: LockConfig | null) {
  try {
    if (cfg) localStorage.setItem(KEY, JSON.stringify(cfg))
    else localStorage.removeItem(KEY)
  } catch {
    /* 存不了就等於沒開 */
  }
}

export const lockConfig = () => read()

export async function lockSupported(): Promise<boolean> {
  try {
    return !!window.PublicKeyCredential && (await PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable())
  } catch {
    return false
  }
}

const toB64 = (buf: ArrayBuffer) => btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
function fromB64(s: string): ArrayBuffer {
  const pad = '='.repeat((4 - (s.length % 4)) % 4)
  return Uint8Array.from(atob(s.replace(/-/g, '+').replace(/_/g, '/') + pad), (c) => c.charCodeAt(0)).buffer
}
const random = (n: number) => crypto.getRandomValues(new Uint8Array(n))

export async function enableLock(userName: string, minutes: number) {
  const cred = (await navigator.credentials.create({
    publicKey: {
      challenge: random(32),
      rp: { name: '日額' },
      user: { id: random(16), name: userName || 'daycap', displayName: userName || '日額' },
      pubKeyCredParams: [
        { type: 'public-key', alg: -7 },
        { type: 'public-key', alg: -257 },
      ],
      authenticatorSelection: { authenticatorAttachment: 'platform', userVerification: 'required', residentKey: 'discouraged' },
      timeout: 60_000,
    },
  })) as PublicKeyCredential | null
  if (!cred) throw new Error('沒有建立成功。')
  write({ credId: toB64(cred.rawId), minutes })
}

export function setLockMinutes(minutes: number) {
  const cfg = read()
  if (cfg) write({ ...cfg, minutes })
}

export function disableLock() {
  write(null)
  lockState.locked = false
}

export async function unlock(): Promise<boolean> {
  const cfg = read()
  if (!cfg) {
    lockState.locked = false
    return true
  }
  try {
    const res = await navigator.credentials.get({
      publicKey: {
        challenge: random(32),
        allowCredentials: [{ type: 'public-key', id: fromB64(cfg.credId) }],
        userVerification: 'required',
        timeout: 60_000,
      },
    })
    if (res) lockState.locked = false
    return !!res
  } catch {
    return false
  }
}

/** 啟動時上鎖；切到背景超過設定分鐘數再回來也上鎖。 */
export function initAppLock() {
  if (read()) lockState.locked = true
  document.addEventListener('visibilitychange', () => {
    const cfg = read()
    if (!cfg) return
    if (document.visibilityState === 'hidden') hiddenAt = Date.now()
    else if (hiddenAt !== null && Date.now() - hiddenAt >= cfg.minutes * 60_000) lockState.locked = true
  })
}
