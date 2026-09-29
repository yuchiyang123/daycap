import { api } from '../api/http'

/** 每晚通知（§9.4）的瀏覽器端：註冊 service worker、訂閱、退訂。 */
export const pushSupported = () => 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window

/** iPhone 要先「加入主畫面」再從主畫面打開，才能收通知。 */
export const isStandalone = () =>
  window.matchMedia('(display-mode: standalone)').matches || (navigator as unknown as { standalone?: boolean }).standalone === true

export async function registerServiceWorker() {
  if (!('serviceWorker' in navigator)) return null
  try {
    return await navigator.serviceWorker.register('/sw.js')
  } catch {
    return null
  }
}

function urlBase64ToUint8Array(base64: string) {
  const padding = '='.repeat((4 - (base64.length % 4)) % 4)
  const raw = atob((base64 + padding).replace(/-/g, '+').replace(/_/g, '/'))
  return Uint8Array.from(raw, (c) => c.charCodeAt(0))
}

export async function currentSubscription(): Promise<PushSubscription | null> {
  if (!pushSupported()) return null
  const reg = await navigator.serviceWorker.getRegistration()
  return (await reg?.pushManager.getSubscription()) ?? null
}

export async function subscribePush(): Promise<void> {
  const permission = await Notification.requestPermission()
  if (permission !== 'granted') throw new Error('沒有允許通知。要在瀏覽器或系統設定裡打開這個網站的通知權限。')
  const reg = (await navigator.serviceWorker.getRegistration()) ?? (await registerServiceWorker())
  if (!reg) throw new Error('這個瀏覽器不能註冊 service worker。')
  await navigator.serviceWorker.ready
  const { publicKey } = await api<{ publicKey: string }>('/api/push/key')
  const sub =
    (await reg.pushManager.getSubscription()) ??
    (await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: urlBase64ToUint8Array(publicKey) }))
  await api<void>('/api/push/subscribe', { method: 'POST', body: sub.toJSON() })
}

export async function unsubscribePush(): Promise<void> {
  const sub = await currentSubscription()
  if (!sub) return
  await api<void>('/api/push/unsubscribe', { method: 'POST', body: { endpoint: sub.endpoint } })
  await sub.unsubscribe()
}
