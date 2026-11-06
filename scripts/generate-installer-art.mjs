// Installer wizard art from the brand assets: ink background, amber R tile, lockup. Writes 24-bit BMPs to Installer/art/ (Inno needs BMP).
// Run after generate-brand.mjs: node scripts/generate-installer-art.mjs
import { writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const REYDEN = join(ROOT, '..', 'ReyDen');
const { sharp } = await import(pathToFileURL(join(REYDEN, 'family/tools/deps.mjs')));
const INK = '#080B11', AMBER = '#FFB020', OUT = join(ROOT, 'Installer', 'art');
mkdirSync(OUT, { recursive: true });
function bmp(raw, w, h) { // raw RGB, top-down -> 24-bit bottom-up BMP
  const stride = Math.ceil(w * 3 / 4) * 4, size = 54 + stride * h, b = Buffer.alloc(size);
  b.write('BM'); b.writeUInt32LE(size, 2); b.writeUInt32LE(54, 10); b.writeUInt32LE(40, 14); b.writeInt32LE(w, 18); b.writeInt32LE(h, 22);
  b.writeUInt16LE(1, 26); b.writeUInt16LE(24, 28); b.writeUInt32LE(stride * h, 34); b.writeInt32LE(2835, 38); b.writeInt32LE(2835, 42);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const s = (y * w + x) * 3, d = 54 + (h - 1 - y) * stride + x * 3;
    b[d] = raw[s + 2]; b[d + 1] = raw[s + 1]; b[d + 2] = raw[s];
  }
  return b;
}
async function save(name, w, h, svg, layers) {
  const base = await sharp(Buffer.from(svg)).resize(w, h).png().toBuffer();
  const img = sharp(base).composite(layers);
  const { data } = await img.removeAlpha().raw().toBuffer({ resolveWithObject: true });
  writeFileSync(join(OUT, name), bmp(data, w, h));
}
const tileSrc = join(ROOT, 'brand', 'icon-256.png'), lockup = join(ROOT, 'brand', 'reyden-desk-lockup-1200.png');
// Left panel: base 164x314 at 100%, shipped at 100/150/200% (Inno picks by DPI).
for (const [k, s] of [['', 1], ['@150', 1.5], ['@200', 2]]) {
  const w = Math.round(164 * s), h = Math.round(314 * s), t = Math.round(88 * s), lw = Math.round(128 * s);
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}"><defs><radialGradient id="g" cx="0.5" cy="1.05" r="0.9"><stop offset="0" stop-color="${AMBER}" stop-opacity="0.34"/><stop offset="1" stop-color="${AMBER}" stop-opacity="0"/></radialGradient></defs><rect width="${w}" height="${h}" fill="${INK}"/><rect width="${w}" height="${h}" fill="url(#g)"/><rect x="${Math.round(w * .18)}" y="${Math.round(h * .62)}" width="${Math.round(w * .64)}" height="${Math.max(1, Math.round(s))}" fill="${AMBER}" opacity="0.5"/></svg>`;
  const tile = await sharp(tileSrc).resize(t, t).png().toBuffer();
  const lk = await sharp(lockup).resize({ width: lw }).png().toBuffer(), lm = await sharp(lk).metadata();
  await save(`wizard${k}.bmp`, w, h, svg, [
    { input: tile, left: Math.round((w - t) / 2), top: Math.round(h * .2) },
    { input: lk, left: Math.round((w - lw) / 2), top: Math.round(h * .62 + 16 * s) }]);
}
// Small header image: 55x55 at 100%, 150%, 200%.
for (const [k, s] of [['', 1], ['@150', 1.5], ['@200', 2]]) {
  const n = Math.round(55 * s), t = Math.round(40 * s);
  const tile = await sharp(tileSrc).resize(t, t).png().toBuffer();
  await save(`small${k}.bmp`, n, n, `<svg xmlns="http://www.w3.org/2000/svg" width="${n}" height="${n}"><rect width="${n}" height="${n}" fill="${INK}"/></svg>`, [{ input: tile, left: Math.round((n - t) / 2), top: Math.round((n - t) / 2) }]);
}
console.log('installer art written to Installer/art');
