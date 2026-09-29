// 日額 service worker：每晚通知（§9.4）與離線使用（§21.4）。
const CACHE = 'daycap-v1'

self.addEventListener('install', () => self.skipWaiting())
self.addEventListener('activate', (event) =>
  event.waitUntil(
    caches
      .keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()),
  ),
)

// 離線（§21.4）：
// - 帶 hash 的 build 產物：先看快取（檔名一變就是新檔）
// - 頁面與「本期」資料：先走網路，失敗才用上次的快取，讓沒網路時也能打開、滑卡（滑卡本身在前端排隊）
// - 其他 API 一律不快取
self.addEventListener('fetch', (event) => {
  const req = event.request
  if (req.method !== 'GET') return
  const url = new URL(req.url)
  if (url.origin !== self.location.origin) return

  if (url.pathname.startsWith('/assets/')) {
    event.respondWith(
      caches.match(req).then(
        (hit) =>
          hit ||
          fetch(req).then((res) => {
            if (res.ok) caches.open(CACHE).then((c) => c.put(req, res.clone()))
            return res
          }),
      ),
    )
    return
  }

  const isPage = req.mode === 'navigate'
  const isPeriod = url.pathname === '/api/periods/current' || url.pathname === '/api/me'
  if (!isPage && !isPeriod) return

  const key = isPage ? '/index.html' : url.pathname
  event.respondWith(
    fetch(req)
      .then((res) => {
        if (res.ok) {
          const copy = res.clone()
          caches.open(CACHE).then((c) => c.put(key, copy))
        }
        return res
      })
      .catch(() => caches.match(key).then((hit) => hit || Response.error())),
  )
})

self.addEventListener('push', (event) => {
  let data = {}
  try {
    data = event.data ? event.data.json() : {}
  } catch {
    data = { body: event.data ? event.data.text() : '' }
  }
  event.waitUntil(
    self.registration.showNotification(data.title || '日額', {
      body: data.body || '',
      icon: '/icons/icon-192.png',
      badge: '/icons/icon-192.png',
      tag: 'nightly', // 一天一則：新的蓋掉舊的
      data: { url: data.url || '/' },
    }),
  )
})

// 點通知：已經開著就切過去，沒開就開新的，打開今天頁（卡片疊在最上面）
self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const url = (event.notification.data && event.notification.data.url) || '/'
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then((list) => {
      for (const c of list) {
        if ('focus' in c) {
          if ('navigate' in c) c.navigate(url)
          return c.focus()
        }
      }
      return self.clients.openWindow(url)
    }),
  )
})
