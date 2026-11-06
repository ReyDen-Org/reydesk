// ReyDesk brand exports (adapted from ReyConnect/scripts/generate-brand.mjs), reusing the approved family raster masters and algorithms.
import { readFileSync, writeFileSync, mkdirSync, copyFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createHash } from 'node:crypto';
import { label, dilate } from './lib/cc.mjs';
const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const REYDEN = join(ROOT, '..', 'ReyDen');
const { sharp } = await import(pathToFileURL(join(REYDEN, 'family/tools/deps.mjs')));
const { launch } = await import(pathToFileURL(join(REYDEN, 'family/tools/pw.mjs')));
const ACCENT = { base:'#FFB020', hover:'#FFC04D', pressed:'#E69A10', text:'#FFB020', ink:'#080B11', onAccent:'#080B11', warning:'#FF8A3D' }; // amber-yellow, owner-approved 2026-11-05 (final); ink text on fill, never white. Closeness to ReyConnect amber accepted by the owner, so no family-distance threshold here
const BG = '#080B11';
const wr=(p,b)=>{mkdirSync(dirname(p),{recursive:true});writeFileSync(p,b)};
const hexRGB=(h)=>[1,3,5].map(i=>parseInt(h.slice(i,i+2),16));
async function load(file) {
  const { data, info } = await sharp(file).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  const W = info.width, H = info.height, a = new Uint8Array(W * H); for (let i = 0; i < W * H; i++) a[i] = data[i * 4 + 3];
  const { lab, comps } = label(a, W, H, 40); const max = Math.max(...comps.map((c) => c.area));
  const keepIds = new Set(comps.filter((c) => c.area >= 0.01 * max).map((c) => c.id));
  let keep = new Uint8Array(W * H); for (let i = 0; i < W * H; i++) keep[i] = keepIds.has(lab[i]) ? 1 : 0;
  keep = dilate(keep, W, H, 3);
  for (let i = 0; i < W * H; i++) if (!keep[i]) data[i * 4 + 3] = 0;
  const kc = comps.filter((c) => keepIds.has(c.id));
  const x0 = Math.min(...kc.map((c) => c.x0)), y0 = Math.min(...kc.map((c) => c.y0)), x1 = Math.max(...kc.map((c) => c.x1)), y1 = Math.max(...kc.map((c) => c.y1));
  const bw = x1 - x0 + 1, bh = y1 - y0 + 1, pad = Math.round(bh * 0.18);
  const r = await sharp(data, { raw: { width: W, height: H, channels: 4 } }).extract({ left: x0, top: y0, width: bw, height: bh })
    .extend({ top: pad, bottom: pad, left: pad, right: pad, background: { r: 0, g: 0, b: 0, alpha: 0 } }).raw().toBuffer({ resolveWithObject: true });
  return { px: r.data, W: r.info.width, H: r.info.height, capH: bh, removed: comps.length - kc.length, comps: kc.length };
}
function midMask(m) {
  const a = new Uint8Array(m.W * m.H); for (let i = 0; i < m.W * m.H; i++) a[i] = m.px[i * 4 + 3];
  const { lab, comps } = label(a, m.W, m.H, 40); const groups = [];
  for (const c of [...comps].sort((p, q) => p.x0 - q.x0)) {
    const g = groups.find((g) => Math.min(g[0].x1, c.x1) - Math.max(g[0].x0, c.x0) >= 0.7 * (c.x1 - c.x0));
    if (g) g.push(c); else groups.push([c]);
  }
  const eGroups = groups.filter((g) => g.length === 3);
  const ids = new Set(eGroups.map((g) => g.sort((p, q) => p.y0 - q.y0)[1].id));
  const mask = new Uint8Array(m.W * m.H); for (let i = 0; i < m.W * m.H; i++) mask[i] = ids.has(lab[i]) ? 1 : 0;
  return { mask: dilate(mask, m.W, m.H, 3), n: eGroups.length };
}
const clone = (m) => ({ ...m, px: Buffer.from(m.px) });
// Luminosity-preserving tint of the middle E bars: shading comes from the chrome, hue from the accent (dark -> deep accent, highlights keep an accent tint),
// plus a tight soft glow (sigma 4 px = about 8 px reach, 35% opacity) drawn under the letters.
async function recolor(m, mid, hex) {
  const o = clone(m), [r, g, b] = hexRGB(hex), lerp = (p, q, t) => p + (q - p) * t;
  const glow = Buffer.alloc(m.W * m.H * 4), lum = (i) => (0.2126 * o.px[i * 4] + 0.7152 * o.px[i * 4 + 1] + 0.0722 * o.px[i * 4 + 2]) / 255, ls = [];
  for (let i = 0; i < m.W * m.H; i += 3) if (mid[i] && o.px[i * 4 + 3] > 200) ls.push(lum(i));
  ls.sort((p, q) => p - q); const p5 = ls[Math.floor(ls.length * 0.05)], p95 = ls[Math.floor(ls.length * 0.95)]; // stretch the bar's own shading range
  for (let i = 0; i < m.W * m.H; i++) if (mid[i] && o.px[i * 4 + 3]) {
    const L = Math.min(1, Math.max(0, (lum(i) - p5) / (p95 - p5 || 1)));
    const lo = [r * 0.4, g * 0.4, b * 0.4], mc = [r, g, b], hi = [lerp(r, 255, 0.35), lerp(g, 255, 0.35), lerp(b, 255, 0.35)];
    const t = Math.min(1, L / 0.6), u = Math.max(0, (L - 0.6) / 0.4);
    for (let c = 0; c < 3; c++) o.px[i * 4 + c] = Math.round(L < 0.6 ? lerp(lo[c], mc[c], t) : lerp(mc[c], hi[c], u));
    glow[i * 4] = r; glow[i * 4 + 1] = g; glow[i * 4 + 2] = b; glow[i * 4 + 3] = 255;
  }
  const blurred = await sharp(glow, { raw: { width: m.W, height: m.H, channels: 4 } }).blur(4).raw().toBuffer();
  const out = Buffer.from(o.px);
  for (let i = 0; i < m.W * m.H; i++) { // over-composite letters on the glow (glow alpha * 0.35)
    const ga = (blurred[i * 4 + 3] / 255) * 0.35, la = out[i * 4 + 3] / 255, oa = la + ga * (1 - la); if (oa <= 0) continue;
    for (let c = 0; c < 3; c++) out[i * 4 + c] = Math.round((out[i * 4 + c] * la + blurred[i * 4 + c] * ga * (1 - la)) / oa);
    out[i * 4 + 3] = Math.round(oa * 255);
  }
  return { ...m, px: out };
}
function flat(m, hex, mid, midHex) {
  const o = clone(m), c = hexRGB(hex), mc = midHex && hexRGB(midHex);
  for (let i = 0; i < m.W * m.H; i++) { const k = mc && mid[i] ? mc : c; o.px[i * 4] = k[0]; o.px[i * 4 + 1] = k[1]; o.px[i * 4 + 2] = k[2]; }
  return o;
}
const toSharp = (m) => sharp(m.px, { raw: { width: m.W, height: m.H, channels: 4 } });

async function exportSizes(m, dir, name, { modern = true } = {}) {
  for (const w of [600, 1200, 2400]) {
    const s = () => toSharp(m).resize({ width: w, kernel: 'lanczos3' });
    wr(join(dir, `${name}-${w}.png`), await s().png({ compressionLevel: 9 }).toBuffer());
    if (modern) { wr(join(dir, `${name}-${w}.webp`), await s().webp({ quality: 92, alphaQuality: 100 }).toBuffer()); wr(join(dir, `${name}-${w}.avif`), await s().avif({ quality: 75, effort: 4 }).toBuffer()); }
  }
  for (const k of [1, 2]) {
    const w = Math.round(m.W * (28 * k) / m.capH), s = () => toSharp(m).resize({ width: w, kernel: 'lanczos3' });
    wr(join(dir, `${name}-nav@${k}x.png`), await s().png().toBuffer());
    if (modern) { wr(join(dir, `${name}-nav@${k}x.webp`), await s().webp({ quality: 92, alphaQuality: 100 }).toBuffer()); wr(join(dir, `${name}-nav@${k}x.avif`), await s().avif({ quality: 75, effort: 4 }).toBuffer()); }
  }
}


const source=join(ROOT,'brand','source');
const masterBuf=readFileSync(join(source,'reyden-r-master.png'));
const mix=(a,b,t)=>'#'+hexRGB(a).map((v,i)=>Math.round(v+(hexRGB(b)[i]-v)*t).toString(16).padStart(2,'0')).join('');
async function inkGlyph(buf,hex){const {data,info}=await sharp(buf).ensureAlpha().raw().toBuffer({resolveWithObject:true}),k=hexRGB(hex);for(let i=0;i<info.width*info.height;i++){data[i*4]=k[0];data[i*4+1]=k[1];data[i*4+2]=k[2]}return sharp(data,{raw:{width:info.width,height:info.height,channels:4}}).png().toBuffer()}
const tileSvg=(size,rx=Math.round(size*.224))=>`<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${ACCENT.hover}"/><stop offset="1" stop-color="${ACCENT.pressed}"/></linearGradient></defs><rect width="${size}" height="${size}" rx="${rx}" fill="url(#g)"/></svg>`;
async function tile(size,{round=true,frac=.62,sharpen=false}={}) {
 const rx=round?Math.round(size*.224):0;
 const c1=ACCENT.hover,c2=ACCENT.pressed;
 const svg=`<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${c1}"/><stop offset="1" stop-color="${c2}"/></linearGradient></defs><rect width="${size}" height="${size}" rx="${rx}" fill="url(#g)"/></svg>`;
 let r=sharp(masterBuf).resize({width:Math.round(size*frac),kernel:'lanczos3'});if(sharpen)r=r.sharpen();
 const rb=await inkGlyph(await r.png().toBuffer(),ACCENT.ink),m=await sharp(rb).metadata();
 return sharp(Buffer.from(svg)).composite([{input:rb,left:Math.round((size-m.width)/2),top:Math.round((size-m.height)/2)}]).png({compressionLevel:9}).toBuffer();
}
function icoFrom(bufs,sizes){const head=Buffer.alloc(6+16*sizes.length);head.writeUInt16LE(1,2);head.writeUInt16LE(sizes.length,4);let off=head.length;sizes.forEach((s,i)=>{const o=6+16*i;head[o]=s===256?0:s;head[o+1]=s===256?0:s;head.writeUInt16LE(1,o+4);head.writeUInt16LE(32,o+6);head.writeUInt32LE(bufs[i].length,o+8);head.writeUInt32LE(off,o+12);off+=bufs[i].length});return Buffer.concat([head,...bufs])}
const sizes=[16,24,32,48,64,128,256];const buffers=[];
for(const size of [16,24,32,48,64,128,180,192,256,512,1024]){
 const options=size===16?{frac:.70,sharpen:true}:size===32?{frac:.66,sharpen:true}:size===48?{frac:.64,sharpen:true}:{};
 const b=await tile(size,options);wr(join(ROOT,'brand',`icon-${size}.png`),b);if(sizes.includes(size))buffers.push(b);
}
wr(join(ROOT,'brand','favicon.ico'),icoFrom(buffers,sizes));
wr(join(ROOT,'brand','maskable-512.png'),await tile(512,{round:false,frac:.52}));
const chrome=await load(join(source,'reyden-wordmark-chrome-master.png'));
const {mask:mid,n:nE}=midMask(chrome);if(nE!==2)throw new Error('Chrome master E-bar detection must equal two');
const recoloured=await recolor(chrome,mid,ACCENT.text);
const pad=Math.round(chrome.capH*.18);
const wm=await toSharp(recoloured).extract({left:pad,top:pad,width:recoloured.W-2*pad,height:recoloured.H-2*pad}).png().toBuffer();
wr(join(ROOT,'brand','wordmark-desk.png'),wm);
const browser=await launch();try{
 const font=readFileSync(join(REYDEN,'public/fonts/Inter-700.woff2')).toString('base64');
 const html=`<style>@font-face{font-family:Inter;src:url(data:font/woff2;base64,${font});font-weight:700}*{box-sizing:border-box}html,body{margin:0;background:transparent}#x{display:inline-flex;align-items:center;padding:4px 0}img{height:36px;display:block}.rule{width:1px;height:40px;background:rgba(255,255,255,.24);margin:0 14px 0 16px}.name{font:700 26px/1 Inter;letter-spacing:.2em;color:${ACCENT.text}}</style><div id="x"><img src="data:image/png;base64,${wm.toString('base64')}"><span class="rule"></span><span class="name">DESK</span></div>`;
 const page=await browser.newPage({viewport:{width:1600,height:200}});await page.setContent(html);await page.evaluate(()=>document.fonts.ready);await page.evaluate(()=>Promise.all([...document.images].map(i=>i.decode())));const width=(await page.locator('#x').boundingBox()).width;await page.close();
 for(const target of [1200,2400]){const p=await browser.newPage({viewport:{width:1600,height:200},deviceScaleFactor:target/width});await p.setContent(html);await p.evaluate(()=>document.fonts.ready);await p.evaluate(()=>Promise.all([...document.images].map(i=>i.decode())));wr(join(ROOT,'brand',`reyden-desk-lockup-${target}.png`),await p.locator('#x').screenshot({omitBackground:true}));await p.close()}
}finally{await browser.close()}
const linear=h=>hexRGB(h).map(v=>{v/=255;return v<=.04045?v/12.92:((v+.055)/1.055)**2.4});
const lum=h=>linear(h).reduce((s,v,i)=>s+v*[.2126,.7152,.0722][i],0);const contrast=(a,b)=>(Math.max(lum(a),lum(b))+.05)/(Math.min(lum(a),lum(b))+.05);
const lab=h=>{const r=linear(h),xyz=[[.4124564,.3575761,.1804375],[.2126729,.7151522,.0721750],[.0193339,.1191920,.9503041]].map((row,j)=>row.reduce((s,v,i)=>s+v*r[i],0)/[.95047,1,1.08883][j]);const f=xyz.map(t=>t>.008856?Math.cbrt(t):7.787*t+16/116);return[116*f[1]-16,500*(f[0]-f[1]),200*(f[1]-f[2])]};
const dE76=(a,b)=>Math.hypot(...lab(a).map((v,i)=>v-lab(b)[i]));
function dE00(h1,h2){ // CIEDE2000 (Sharma et al.)
 const [L1,a1,b1]=lab(h1),[L2,a2,b2]=lab(h2),rad=Math.PI/180,deg=180/Math.PI;
 const C1=Math.hypot(a1,b1),C2=Math.hypot(a2,b2),Cb=(C1+C2)/2,G=.5*(1-Math.sqrt(Cb**7/(Cb**7+25**7)));
 const ap1=(1+G)*a1,ap2=(1+G)*a2,Cp1=Math.hypot(ap1,b1),Cp2=Math.hypot(ap2,b2);
 const hp=(b,a)=>{const h=Math.atan2(b,a)*deg;return h<0?h+360:h};const hp1=hp(b1,ap1),hp2=hp(b2,ap2);
 const dL=L2-L1,dC=Cp2-Cp1;let dh=0;if(Cp1*Cp2){dh=hp2-hp1;if(dh>180)dh-=360;else if(dh<-180)dh+=360}
 const dH=2*Math.sqrt(Cp1*Cp2)*Math.sin(dh*rad/2),Lb=(L1+L2)/2,Cpb=(Cp1+Cp2)/2;
 let hb=hp1+hp2;if(Cp1*Cp2){if(Math.abs(hp1-hp2)>180)hb+=hp1+hp2<360?360:-360;hb/=2}
 const T=1-.17*Math.cos((hb-30)*rad)+.24*Math.cos(2*hb*rad)+.32*Math.cos((3*hb+6)*rad)-.20*Math.cos((4*hb-63)*rad);
 const dth=30*Math.exp(-(((hb-275)/25)**2)),Rc=2*Math.sqrt(Cpb**7/(Cpb**7+25**7)),Sl=1+.015*(Lb-50)**2/Math.sqrt(20+(Lb-50)**2),Sc=1+.045*Cpb,Sh=1+.015*Cpb*T,Rt=-Math.sin(2*dth*rad)*Rc;
 return Math.sqrt((dL/Sl)**2+(dC/Sc)**2+(dH/Sh)**2+Rt*(dC/Sc)*(dH/Sh));
}
const r1=(v)=>Math.round(v*10)/10;
const registry=JSON.parse(readFileSync(join(REYDEN,'family/products.json'),'utf8'));
const deltaE=registry.products.map(p=>({id:p.id,text:p.accent.text,base:p.accent.base,
 textVsText:{cie76:r1(dE76(ACCENT.text,p.accent.text)),de2000:r1(dE00(ACCENT.text,p.accent.text))},
 textVsFill:{cie76:r1(dE76(ACCENT.text,p.accent.base)),de2000:r1(dE00(ACCENT.text,p.accent.base))},
 fillVsText:{cie76:r1(dE76(ACCENT.base,p.accent.text)),de2000:r1(dE00(ACCENT.base,p.accent.text))},
 fillVsFill:{cie76:r1(dE76(ACCENT.base,p.accent.base)),de2000:r1(dE00(ACCENT.base,p.accent.base))}}));
const C=(a,b)=>Math.round(contrast(a,b)*100)/100;
const DANGER='#FF5A5F',SUCCESS='#3DDC84';
const checks={accentTextOnBg:C(ACCENT.text,BG),accentTextOnPanel:C(ACCENT.text,'#141A24'),accentTextOnSurface:C(ACCENT.text,'#0E131B'),inkOnFill:C(ACCENT.ink,ACCENT.base),inkOnFillHover:C(ACCENT.ink,ACCENT.hover),inkOnFillPressed:C(ACCENT.ink,ACCENT.pressed),fillVsBg:C(ACCENT.base,BG),warningOnBg:C(ACCENT.warning,BG),warningOnPanel:C(ACCENT.warning,'#141A24'),successOnBg:C(SUCCESS,BG),dangerOnBg:C(DANGER,BG),dangerOnPanel:C(DANGER,'#141A24')};
const pair=(a,b)=>({cie76:r1(dE76(a,b)),de2000:r1(dE00(a,b))});
const statusDeltaE={accentVsDanger:pair(ACCENT.text,DANGER),accentVsWarning:pair(ACCENT.text,ACCENT.warning),accentVsSuccess:pair(ACCENT.text,SUCCESS),warningVsDanger:pair(ACCENT.warning,DANGER)};
if(statusDeltaE.accentVsDanger.de2000<20||statusDeltaE.accentVsWarning.de2000<15||statusDeltaE.accentVsSuccess.de2000<40)throw new Error('Status colours must stay distinct from the accent');
if(checks.accentTextOnBg<4.5||checks.inkOnFill<4.5||checks.inkOnFillPressed<4.5||checks.dangerOnPanel<4.5)throw new Error('Brand contrast must meet 4.5');

// ---- Platform exports (Windows Assets, Android res) ----
const WIN=join(ROOT,'Windows/ReyDesk.Windows/Assets'),AND=join(ROOT,'Android/app/src/main/res');
const tileOpts=(s)=>s<=20?{frac:.70,sharpen:true}:s===32?{frac:.66,sharpen:true}:(s>=40&&s<=48)?{frac:.64,sharpen:true}:{};
const rect=(w,h,hex)=>sharp({create:{width:w,height:h,channels:4,background:hex}}).png();
async function onBg(w,h,tileSize,hex=BG){const t=await tile(tileSize,tileOpts(tileSize));return rect(w,h,hex).composite([{input:t,left:Math.round((w-tileSize)/2),top:Math.round((h-tileSize)/2)}]).png({compressionLevel:9}).toBuffer()}
const savePng=(p,b)=>wr(p,b);
// Small-size tile (<=32 px): the master R is a thin stroke that turns to mush at 16 px, so it is drawn at 8x, thickened, then downsampled.
// opts.dot = status colour: dot with an ink ring in the bottom-end corner, glyph shifted up-start so the two never overlap.
async function boldTile(size,{dot=null}={}){
 const K=8,S=size*K,frac=dot?.64:.72,gw=Math.round(S*frac);
 const g=await sharp(masterBuf).resize({width:gw,kernel:'lanczos3'}).ensureAlpha().raw().toBuffer({resolveWithObject:true});
 const W=g.info.width,H=g.info.height,a=new Uint8Array(W*H);for(let i=0;i<W*H;i++)a[i]=g.data[i*4+3]>100?1:0;
 const m=dilate(a,W,H,Math.max(2,Math.round(S*(dot?.028:.026))));
 const px=Buffer.alloc(W*H*4),k=hexRGB(ACCENT.ink);for(let i=0;i<W*H;i++)if(m[i]){px[i*4]=k[0];px[i*4+1]=k[1];px[i*4+2]=k[2];px[i*4+3]=255}
 const gp=await sharp(px,{raw:{width:W,height:H,channels:4}}).png().toBuffer();
 const off=dot?Math.round(S*.04):0;
 const base=await sharp(Buffer.from(tileSvg(S))).composite([{input:gp,left:Math.round((S-W)/2)-off,top:Math.round((S-H)/2)-off}]).png().toBuffer();
 let img=sharp(base);
 if(dot){const d=S*.42,ow=Math.max(K*1.2,S*.07),cx=S-d/2-S*.02,cy=S-d/2-S*.02;
  const svg=`<svg xmlns="http://www.w3.org/2000/svg" width="${S}" height="${S}"><circle cx="${cx}" cy="${cy}" r="${d/2}" fill="${ACCENT.ink}"/><circle cx="${cx}" cy="${cy}" r="${d/2-ow}" fill="${dot}"/></svg>`;
  img=sharp(await img.composite([{input:Buffer.from(svg)}]).png().toBuffer());}
 return img.resize(size,size,{kernel:'lanczos3'}).png({compressionLevel:9}).toBuffer();
}
// Windows
const icoSizes=[16,20,24,32,40,48,64,256];
wr(join(WIN,'AppIcon.ico'),icoFrom(await Promise.all(icoSizes.map(s=>s<=32?boldTile(s):tile(s,tileOpts(s)))),icoSizes));
for(const [n,s] of [['Square44x44Logo.scale-200.png',88],['Square44x44Logo.targetsize-24_altform-unplated.png',24],['Square44x44Logo.targetsize-48_altform-lightunplated.png',48],['Square150x150Logo.scale-200.png',300],['StoreLogo.png',50],['LockScreenLogo.scale-200.png',48]])savePng(join(WIN,n),await tile(s,tileOpts(s)));
savePng(join(WIN,'Wide310x150Logo.scale-200.png'),await onBg(620,300,200));
savePng(join(WIN,'SplashScreen.scale-200.png'),await onBg(1240,600,240));
copyFileSync(join(ROOT,'brand/reyden-desk-lockup-1200.png'),join(WIN,'Logo-Horizontal-Dark.png'));
// Tray state icons: bold tile + bottom-end status dot (42% of size, ink ring). Sizes cover 100/125/150/200% DPI (SM_CXSMICON 16/20/24/32); amber tile reads on light and dark taskbars.
const trayStates={Online:'#3DDC84',Connecting:ACCENT.warning,Error:'#FF5A5F',Offline:'#8590A2'};
const traySizes=[16,20,24,32];
for(const [name,color] of Object.entries(trayStates))wr(join(WIN,`Tray-${name}.ico`),icoFrom(await Promise.all(traySizes.map(s=>boldTile(s,{dot:color}))),traySizes));
// Android
const glyphMeta=await sharp(masterBuf).metadata();
async function glyph(canvas,box,colour=ACCENT.ink){ // R glyph contained in box x box, centered on a transparent canvas x canvas
 const k=Math.min(box/glyphMeta.width,box/glyphMeta.height),w=Math.max(1,Math.round(glyphMeta.width*k)),h=Math.max(1,Math.round(glyphMeta.height*k));
 let g=sharp(masterBuf).resize({width:w,height:h,kernel:'lanczos3'}).ensureAlpha();
 g=sharp(await inkGlyph(await g.png().toBuffer(),colour));
 const gb=await g.png().toBuffer();
 return sharp({create:{width:canvas,height:canvas,channels:4,background:{r:0,g:0,b:0,alpha:0}}}).composite([{input:gb,left:Math.round((canvas-w)/2),top:Math.round((canvas-h)/2)}]).png({compressionLevel:9}).toBuffer();
}
const dens={mdpi:1,hdpi:1.5,xhdpi:2,xxhdpi:3,xxxhdpi:4};
for(const [d,k] of Object.entries(dens)){
 const dir=join(AND,`mipmap-${d}`),s=Math.round(48*k);
 const t=await tile(s,tileOpts(s));
 wr(join(dir,'ic_launcher.png'),t);
 wr(join(dir,'ic_launcher_round.png'),await sharp(t).composite([{input:Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg" width="${s}" height="${s}"><circle cx="${s/2}" cy="${s/2}" r="${s/2}"/></svg>`),blend:'dest-in'}]).png().toBuffer());
 wr(join(dir,'ic_launcher_foreground.png'),await glyph(Math.round(108*k),Math.round(66*k)));
}
wr(join(AND,'drawable-nodpi/ic_launcher_monochrome.png'),await glyph(432,264,'#FFFFFF'));
wr(join(AND,'drawable-nodpi/reydesk_tile.png'),await tile(192));
wr(join(AND,'drawable-nodpi/logo_horizontal_dark.png'),await sharp(join(ROOT,'brand/reyden-desk-lockup-600.png')).toBuffer().catch(()=>sharp(join(ROOT,'brand/reyden-desk-lockup-1200.png')).resize({width:600,kernel:'lanczos3'}).png().toBuffer()));
const c1=ACCENT.hover,c2=ACCENT.pressed;
wr(join(AND,'drawable/reydesk_icon_background.xml'),`<?xml version="1.0" encoding="utf-8"?>\n<shape xmlns:android="http://schemas.android.com/apk/res/android" android:shape="rectangle">\n    <gradient android:type="linear" android:angle="315" android:startColor="@color/icon_background_start" android:endColor="@color/icon_background_end" />\n</shape>\n`);
const adaptive=`<?xml version="1.0" encoding="utf-8"?>\n<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">\n    <background android:drawable="@drawable/reydesk_icon_background" />\n    <foreground android:drawable="@mipmap/ic_launcher_foreground" />\n    <monochrome android:drawable="@drawable/ic_launcher_monochrome" />\n</adaptive-icon>\n`;
wr(join(AND,'mipmap-anydpi-v26/ic_launcher.xml'),adaptive);wr(join(AND,'mipmap-anydpi-v26/ic_launcher_round.xml'),adaptive);

const meta={product:'ReyDesk',platform:'native-windows+android',accent:ACCENT,tileGradient:[c1,c2],masters:Object.fromEntries(['reyden-r-master.png','reyden-wordmark-chrome-master.png'].map(n=>[n,createHash('sha256').update(readFileSync(join(source,n))).digest('hex')])),source:'ReyDen/family/tools/{make_kit,wordmark_master,cc}.mjs; masters copied unchanged from ReyConnect',generation:'node scripts/generate-brand.mjs',contrast:checks,statusDeltaE,deltaE:deltaE,icoSizes:sizes,generatedAt:new Date().toISOString()};
wr(join(ROOT,'brand/product.json'),JSON.stringify(meta,null,2)+'\n');console.log(JSON.stringify({contrast:checks,statusDeltaE,deltaE},null,1));
