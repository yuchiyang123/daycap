// 產生 PWA / iOS 主畫面圖示：深色方底 + 用矩形拼出的「日」字 + 一條藍色底線。
// 純 Node（zlib）手寫 PNG 編碼，不需要任何影像套件。執行：npm run icons
import { deflateSync } from 'node:zlib'
import { mkdirSync, writeFileSync } from 'node:fs'

const BG = [0x14, 0x14, 0x12]
const FG = [0xf4, 0xf3, 0xef]
const ACCENT = [0x39, 0x87, 0xe5]

function render(size) {
  const px = Buffer.alloc(size * size * 3)
  const fill = (x0, y0, x1, y1, c) => {
    for (let y = Math.max(0, Math.round(y0)); y < Math.min(size, Math.round(y1)); y++)
      for (let x = Math.max(0, Math.round(x0)); x < Math.min(size, Math.round(x1)); x++) {
        const i = (y * size + x) * 3
        px[i] = c[0]
        px[i + 1] = c[1]
        px[i + 2] = c[2]
      }
  }
  fill(0, 0, size, size, BG)
  const s = size
  const w = 0.36 * s
  const h = 0.46 * s
  const t = 0.062 * s
  const x0 = (s - w) / 2
  const y0 = 0.2 * s
  fill(x0, y0, x0 + w, y0 + t, FG) // 上
  fill(x0, y0 + h - t, x0 + w, y0 + h, FG) // 下
  fill(x0, y0, x0 + t, y0 + h, FG) // 左
  fill(x0 + w - t, y0, x0 + w, y0 + h, FG) // 右
  fill(x0, y0 + h / 2 - t / 2, x0 + w, y0 + h / 2 + t / 2, FG) // 中
  fill(x0, y0 + h + 0.07 * s, x0 + w, y0 + h + 0.07 * s + 0.045 * s, ACCENT)
  return px
}

const CRC = new Uint32Array(256).map((_, n) => {
  let c = n
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
  return c >>> 0
})
function crc32(buf) {
  let c = 0xffffffff
  for (const b of buf) c = CRC[(c ^ b) & 0xff] ^ (c >>> 8)
  return (c ^ 0xffffffff) >>> 0
}
function chunk(type, data) {
  const len = Buffer.alloc(4)
  len.writeUInt32BE(data.length)
  const td = Buffer.concat([Buffer.from(type, 'ascii'), data])
  const crc = Buffer.alloc(4)
  crc.writeUInt32BE(crc32(td))
  return Buffer.concat([len, td, crc])
}
function png(size) {
  const px = render(size)
  const raw = Buffer.alloc((size * 3 + 1) * size)
  for (let y = 0; y < size; y++) {
    raw[y * (size * 3 + 1)] = 0
    px.copy(raw, y * (size * 3 + 1) + 1, y * size * 3, (y + 1) * size * 3)
  }
  const ihdr = Buffer.alloc(13)
  ihdr.writeUInt32BE(size, 0)
  ihdr.writeUInt32BE(size, 4)
  ihdr[8] = 8 // bit depth
  ihdr[9] = 2 // RGB
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ])
}

mkdirSync('public/icons', { recursive: true })
for (const [name, size] of [['icon-32.png', 32], ['icon-192.png', 192], ['icon-512.png', 512], ['apple-touch-icon.png', 180]]) {
  writeFileSync(`public/icons/${name}`, png(size))
  console.log(name, size)
}
