// Shared: connected components on an alpha plane (8-connectivity) and a small dilation.
export function label(alpha, W, H, thr = 40) {
  const N = W * H, lab = new Int32Array(N), comps = [], stack = new Int32Array(N);
  for (let s = 0; s < N; s++) {
    if (lab[s] || alpha[s] <= thr) continue;
    const id = comps.length + 1; const c = { id, area: 0, x0: W, y0: H, x1: 0, y1: 0 }; let sp = 0; stack[sp++] = s; lab[s] = id;
    while (sp) {
      const p = stack[--sp], x = p % W, y = (p / W) | 0; c.area++;
      if (x < c.x0) c.x0 = x; if (x > c.x1) c.x1 = x; if (y < c.y0) c.y0 = y; if (y > c.y1) c.y1 = y;
      for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
        const nx = x + dx, ny = y + dy; if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
        const q = ny * W + nx; if (!lab[q] && alpha[q] > thr) { lab[q] = id; stack[sp++] = q; }
      }
    }
    comps.push(c);
  }
  return { lab, comps };
}
export function dilate(mask, W, H, r) {
  let m = mask;
  for (let it = 0; it < r; it++) {
    const o = m.slice();
    for (let y = 1; y < H - 1; y++) for (let x = 1; x < W - 1; x++) { const p = y * W + x; if (!m[p] && (m[p - 1] || m[p + 1] || m[p - W] || m[p + W])) o[p] = 1; }
    m = o;
  }
  return m;
}
