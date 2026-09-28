/** 軸刻度取整：0 / 500 / 1,000 這種乾淨的數字。 */
export function niceStep(range: number, targetTicks = 4): number {
  if (range <= 0) return 1
  const raw = range / targetTicks
  const mag = Math.pow(10, Math.floor(Math.log10(raw)))
  const norm = raw / mag
  const step = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 2.5 ? 2.5 : norm <= 5 ? 5 : 10
  return step * mag
}

export function niceDomain(min: number, max: number, targetTicks = 4): { min: number; max: number; ticks: number[] } {
  if (min === max) {
    max = min + 1
  }
  const step = niceStep(max - min, targetTicks)
  const lo = Math.floor(min / step) * step
  const hi = Math.ceil(max / step) * step
  const ticks: number[] = []
  for (let v = lo; v <= hi + step / 2; v += step) ticks.push(Math.round(v * 1000) / 1000)
  return { min: lo, max: hi, ticks }
}

export function linear(d0: number, d1: number, r0: number, r1: number) {
  const k = d1 === d0 ? 0 : (r1 - r0) / (d1 - d0)
  return (v: number) => r0 + (v - d0) * k
}
