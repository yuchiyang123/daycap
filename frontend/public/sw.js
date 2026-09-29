// 日額 service worker：目前只處理每晚通知（§9.4）。離線（§21.4）之後再加。
self.addEventListener('install', () => self.skipWaiting())
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()))

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
