#!/usr/bin/env node
// Genererar Forever Scouts ikoner och logotyper ur client/windows/assets/Alliance Campfire Emblem.png.
//   node client/windows/tools/make-icons.mjs
// Utdata:
//   client/windows/assets/logo-full.png        hela logotypen, beskuren (visas bara INUTI appen)
//   client/windows/src/ForeverScout/app.ico     exe-/fönster-/installer-/genvägsikon (ikon v2): 16/20/24/32/48/64/128/256
//   client/windows/installer/wizard-*.png       bilder till installationsguiden (Inno Setup)
//   client/windows/src/ForeverScout/Resources/  logo-header.png, tray-normal.png, tray-error.png, tray-upload-0..3.png
//   public/scout/logo-full.webp (≤ 800 px bred), public/scout/logo-fire-256.webp  (till sajten)
// Originalet innehåller Alliansens vapen och ett WoW-"W" (Blizzards varumärken). De får ALDRIG hamna i exe-ikonen.
// Ikon v2: bara elden och vedträna lyfts ut (färgnyckel: ljust dis och grå stenar bort, mättade lågor och mörkröda vedträn
// kvar, plus en mjuk äggformad mask som tar bort yxan och muggen med vapnet) och läggs på en mörk rundad ruta med tunn
// guldkant och varm glöd underifrån. 16–24 px får en förenklad, ritad variant i samma palett (lågan + två vedträn) så
// att den inte blir gröt. Fältikonen (32 px) byggs av samma målade eld – samma karaktär som tidigare.
//   node client/windows/tools/make-icons.mjs [--compare <gammal app.ico>]   (jämförelseark → docs/review/design/scout-icon-v2.png)
import sharp from "sharp";
import { mkdirSync, writeFileSync, existsSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const WIN = resolve(HERE, "..");
const GUIDE = resolve(WIN, "..", "..");
const SRC = join(WIN, "assets", "Alliance Campfire Emblem.png");
const RES = join(WIN, "src", "ForeverScout", "Resources");
const WEB = join(GUIDE, "public", "scout");
const BG = "#1c1714"; // Lägereld, mörk varm
const DESIGN = join(GUIDE, "docs", "review", "design");
const INST = join(WIN, "installer");
const argCompare = process.argv.indexOf("--compare");
const OLD_ICO = argCompare > 0 ? process.argv[argCompare + 1] : null;

if (!existsSync(SRC)) throw new Error(`Saknar ${SRC}`);
mkdirSync(RES, { recursive: true });
mkdirSync(WEB, { recursive: true });

// ---------- Hela logotypen (bara inuti appen och på sajten – aldrig som ikon) ----------
const full = await sharp(SRC).trim({ threshold: 4 }).png().toBuffer();
await sharp(full).toFile(join(WIN, "assets", "logo-full.png"));
await sharp(full).resize({ width: 360 }).png({ compressionLevel: 9 }).toFile(join(RES, "logo-header.png"));
await sharp(full).resize({ width: 800, withoutEnlargement: true }).webp({ quality: 86, alphaQuality: 90 }).toFile(join(WEB, "logo-full.webp"));

// Elden och vedträna (i originalets koordinater, 1536×1024), centrerat. Muggens vapen börjar vid x ≈ 865 och yxan
// ligger uppe till vänster – båda faller utanför masken nedan.
const CROP = { left: 600, top: 168, width: 290, height: 310 };
const smooth = (a, b, x) => { const t = Math.min(1, Math.max(0, (x - a) / (b - a))); return t * t * (3 - 2 * t); };

/** Elden + vedträn som frilagt lager (RGBA). */
async function fireLayer() {
  const { data, info } = await sharp(SRC).extract(CROP).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  const { width: w, height: h } = info;
  const cx = w * 0.5, cy = h * 0.64;
  for (let y = 0; y < h; y++)
    for (let x = 0; x < w; x++) {
      const i = (y * w + x) * 4;
      const r = data[i] / 255, g = data[i + 1] / 255, b = data[i + 2] / 255;
      const max = Math.max(r, g, b), min = Math.min(r, g, b);
      const sat = max === 0 ? 0 : (max - min) / max, v = max;
      // Färgnyckel: ljust dis (låg mättnad) och grå stenar bort; mättade lågor och mörkröda vedträn kvar.
      let key = Math.max(smooth(0.16, 0.34, sat), v < 0.42 && r > g ? 1 : 0);
      const inLogs = y > cy - h * 0.1 && Math.abs(x - cx) < w * 0.3;
      if (inLogs && v < 0.55) key = 1; // sprickor och skuggor mellan vedträna: helt täckande (inga grå halvgenomskinliga streck)
      else if (sat < 0.45 && v > 0.3 && v < 0.75 && Math.abs(r - g) < 0.18) key *= 0.15; // grå stenar
      // Lågornas ljusa kärna är omättad men ska vara kvar (annars blir det hål).
      const core = Math.hypot((x - cx) / (w * 0.22), (y - h * 0.52) / (h * 0.3));
      if (core < 1 && v > 0.72) key = Math.max(key, 1 - smooth(0.7, 1.0, core));
      // Ljusa, omättade fläckar mellan vedträna (vitglödande springor) blir gråa/gröna i små storlekar –
      // färga dem som glöd (varmt gul-orange) i stället för att låta dem vara halvgenomskinliga.
      if (y > cy - h * 0.12 && v > 0.62 && sat < 0.4) {
        data[i] = 255; data[i + 1] = Math.round(150 + 70 * v); data[i + 2] = Math.round(40 + 40 * v);
        key = 1;
      }
      // Äggformad mask: smal och hög uppåt (lågorna), bred och låg nedåt (vedträna), mjuk övergång utan söm.
      const widen = smooth(cy - h * 0.25, cy + h * 0.05, y);
      const d = Math.hypot((x - cx) / (w * (0.3 + 0.13 * widen)), (y - cy) / (y < cy ? h * 0.6 : h * 0.36));
      data[i + 3] = Math.round(data[i + 3] * key * (1 - smooth(0.58, 0.92, d)));
    }
  return sharp(data, { raw: { width: w, height: h, channels: 4 } }).png().toBuffer();
}

/** Mörk rundad ruta med varm glöd underifrån och tunn guldkant (#F0B844, låg opacitet). */
function tileBackground(size) {
  const r = (size * 0.22).toFixed(2), sw = Math.max(1, size / 64), o = (sw / 2).toFixed(2), wh = (size - sw).toFixed(2);
  return Buffer.from(`<svg width="${size}" height="${size}" xmlns="http://www.w3.org/2000/svg"><defs>
    <radialGradient id="glow" cx="50%" cy="74%" r="64%"><stop offset="0%" stop-color="#7a3a12"/><stop offset="48%" stop-color="#3a2015"/><stop offset="100%" stop-color="#141012"/></radialGradient></defs>
    <rect x="${o}" y="${o}" width="${wh}" height="${wh}" rx="${r}" fill="url(#glow)"/>
    <rect x="${o}" y="${o}" width="${wh}" height="${wh}" rx="${r}" fill="none" stroke="#F0B844" stroke-opacity="0.5" stroke-width="${sw}"/></svg>`);
}

/** Målad variant (≥ 32 px). */
async function paintedTile(size, fire) {
  const inner = Math.round(size * 0.88);
  const fw = Math.round((inner * CROP.width) / CROP.height);
  let f = sharp(fire).resize(fw, inner, { kernel: "lanczos3" });
  if (size <= 48) f = f.modulate({ saturation: 1.15 }).sharpen({ sigma: size <= 32 ? 0.7 : 0.5 });
  return sharp(tileBackground(size))
    .composite([{ input: await f.png().toBuffer(), left: Math.round((size - fw) / 2), top: Math.round(size * 0.05) }])
    .png().toBuffer();
}

/** Förenklad ritad variant (16–24 px): samma palett, tydliga former. */
function simpleTile(size) {
  const svg = `<svg width="${size}" height="${size}" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg"><defs>
    <radialGradient id="glow" cx="50%" cy="74%" r="62%"><stop offset="0%" stop-color="#7a3a12"/><stop offset="50%" stop-color="#3a2015"/><stop offset="100%" stop-color="#141012"/></radialGradient>
    <linearGradient id="fl" x1="0" y1="1" x2="0" y2="0"><stop offset="0%" stop-color="#e2561b"/><stop offset="55%" stop-color="#f59a2a"/><stop offset="100%" stop-color="#ffc35a"/></linearGradient>
    <linearGradient id="core" x1="0" y1="1" x2="0" y2="0"><stop offset="0%" stop-color="#fff2b8"/><stop offset="100%" stop-color="#ffd166"/></linearGradient></defs>
    <rect x="0.5" y="0.5" width="23" height="23" rx="5.3" fill="url(#glow)"/>
    <rect x="0.5" y="0.5" width="23" height="23" rx="5.3" fill="none" stroke="#F0B844" stroke-opacity="0.55" stroke-width="1"/>
    <path d="M12 3.2 C13.6 6 17.4 8.6 16.8 13.4 C16.4 16.4 14.4 18 12 18 C9.6 18 7.6 16.4 7.2 13.4 C6.8 10.4 8.8 9.2 9.6 7.2 C10.1 9 10.8 9.8 11.4 10 C11.2 7.6 11.2 5.2 12 3.2 Z" fill="url(#fl)"/>
    <path d="M12 9.6 C13 11.2 14.4 12.6 14.2 14.8 C14 16.4 13.1 17.2 12 17.2 C10.9 17.2 10 16.4 9.8 14.8 C9.7 13.4 10.7 12.6 11.2 11.4 C11.5 12.2 11.9 12.4 12.1 12.4 C11.9 11.4 11.8 10.6 12 9.6 Z" fill="url(#core)"/>
    <path d="M5.2 19.6 L18.4 16.6" stroke="#8a3f18" stroke-width="2.4" stroke-linecap="round"/>
    <path d="M5.6 16.6 L18.8 19.6" stroke="#6e2f12" stroke-width="2.4" stroke-linecap="round"/></svg>`;
  return sharp(Buffer.from(svg)).png().toBuffer();
}

async function fireTile(size, fire) {
  return size <= 24 ? simpleTile(size) : paintedTile(size, fire);
}

// Status-overlay för fältikonen (32 px).
function overlay(size, kind, frame = 0) {
  const s = size;
  if (kind === "error")
    return `<svg width="${s}" height="${s}"><circle cx="${s - 7}" cy="${s - 7}" r="6.5" fill="${BG}"/><circle cx="${s - 7}" cy="${s - 7}" r="5" fill="#f87171"/></svg>`;
  // Uppladdning: amber ring + en ljus gnista som vandrar ett kvarts varv per bildruta.
  const c = s / 2, rr = s / 2 - 1.6;
  const a0 = (-90 + frame * 90) * (Math.PI / 180), a1 = a0 + (70 * Math.PI) / 180;
  const p = (a) => `${(c + rr * Math.cos(a)).toFixed(2)} ${(c + rr * Math.sin(a)).toFixed(2)}`;
  return `<svg width="${s}" height="${s}"><circle cx="${c}" cy="${c}" r="${rr}" fill="none" stroke="#e9b44c" stroke-opacity="0.85" stroke-width="2.4"/>
    <path d="M ${p(a0)} A ${rr} ${rr} 0 0 1 ${p(a1)}" fill="none" stroke="#ffd27a" stroke-width="3.6" stroke-linecap="round"/></svg>`;
}

const sizes = [16, 20, 24, 32, 48, 64, 128, 256];
const fire = await fireLayer();
const tiles = {};
for (const s of sizes) tiles[s] = await fireTile(s, fire);

// ---------- app.ico (BMP-poster < 256, PNG för 256) ----------
async function dib(png, s) {
  const { data } = await sharp(png).ensureAlpha().raw().toBuffer({ resolveWithObject: true });
  const maskRow = Math.ceil(s / 32) * 4;
  const out = Buffer.alloc(40 + s * s * 4 + maskRow * s);
  out.writeUInt32LE(40, 0); out.writeInt32LE(s, 4); out.writeInt32LE(s * 2, 8);
  out.writeUInt16LE(1, 12); out.writeUInt16LE(32, 14); out.writeUInt32LE(s * s * 4, 20);
  let o = 40;
  for (let y = s - 1; y >= 0; y--)
    for (let x = 0; x < s; x++) {
      const i = (y * s + x) * 4;
      out[o++] = data[i + 2]; out[o++] = data[i + 1]; out[o++] = data[i]; out[o++] = data[i + 3];
    }
  return out;
}
const entries = [];
for (const s of sizes) entries.push({ s, data: s >= 256 ? tiles[s] : await dib(tiles[s], s) });
const header = Buffer.alloc(6 + 16 * entries.length);
header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(entries.length, 4);
let offset = header.length;
entries.forEach((e, k) => {
  const b = 6 + 16 * k;
  header[b] = e.s >= 256 ? 0 : e.s; header[b + 1] = e.s >= 256 ? 0 : e.s;
  header.writeUInt16LE(1, b + 4); header.writeUInt16LE(32, b + 6);
  header.writeUInt32LE(e.data.length, b + 8); header.writeUInt32LE(offset, b + 12);
  offset += e.data.length;
});
writeFileSync(join(WIN, "src", "ForeverScout", "app.ico"), Buffer.concat([header, ...entries.map((e) => e.data)]));

// ---------- Fältikoner (32 px; Windows skalar ner till 16/20/24) ----------
const t32 = tiles[32];
await sharp(t32).toFile(join(RES, "tray-normal.png"));
await sharp(t32).composite([{ input: Buffer.from(overlay(32, "error")) }]).png().toFile(join(RES, "tray-error.png"));
for (let f = 0; f < 4; f++)
  await sharp(t32).composite([{ input: Buffer.from(overlay(32, "upload", f)) }]).png().toFile(join(RES, `tray-upload-${f}.png`));

// ---------- Webb ----------
await sharp(tiles[256]).webp({ quality: 90, alphaQuality: 95 }).toFile(join(WEB, "logo-fire-256.webp"));
await sharp(tiles[256]).toFile(join(WIN, "assets", "logo-fire-256.png"));

// ---------- Installationsguiden (Inno Setup): Lägereld-bakgrund, logotyp och lägereld ----------
mkdirSync(INST, { recursive: true });
async function wizardImage(w, h) {
  const bg = Buffer.from(`<svg width="${w}" height="${h}" xmlns="http://www.w3.org/2000/svg"><defs>
    <radialGradient id="g" cx="50%" cy="82%" r="75%"><stop offset="0%" stop-color="#5a2a10"/><stop offset="45%" stop-color="#241a1a"/><stop offset="100%" stop-color="#17151A"/></radialGradient></defs>
    <rect width="${w}" height="${h}" fill="url(#g)"/><rect x="${w - 1}" y="0" width="1" height="${h}" fill="#42363A"/></svg>`);
  const logoW = Math.round(w * 0.86);
  const logo = await sharp(full).resize({ width: logoW }).png().toBuffer();
  const lh = (await sharp(logo).metadata()).height;
  const fs = Math.round(w * 0.62);
  const icon = await sharp(tiles[256]).resize(fs, fs).png().toBuffer();
  return sharp(bg).composite([
    { input: logo, left: Math.round((w - logoW) / 2), top: Math.round(h * 0.08) },
    { input: icon, left: Math.round((w - fs) / 2), top: Math.round(h * 0.08) + lh + Math.round(h * 0.08) },
  ]).png().toBuffer();
}
for (const [scale, suffix] of [[1, "100"], [1.5, "150"], [2, "200"]]) {
  await sharp(await wizardImage(Math.round(164 * scale), Math.round(314 * scale))).toFile(join(INST, `wizard-large-${suffix}.png`));
  const sm = Math.round(55 * scale);
  await sharp(tiles[256]).resize(sm, sm).png().toFile(join(INST, `wizard-small-${suffix}.png`));
}

// ---------- Jämförelseark gammal/ny (valfritt) ----------
function readIco(buf) {
  const n = buf.readUInt16LE(4), out = {};
  for (let k = 0; k < n; k++) {
    const b = 6 + 16 * k, size = buf[b] || 256, len = buf.readUInt32LE(b + 8), off = buf.readUInt32LE(b + 12);
    const d = buf.subarray(off, off + len);
    if (d[0] === 0x89) { out[size] = { png: d }; continue; }
    const w = d.readInt32LE(4), hdr = d.readUInt32LE(0), px = Buffer.alloc(w * w * 4);
    for (let y = 0; y < w; y++) for (let x = 0; x < w; x++) {
      const si = hdr + ((w - 1 - y) * w + x) * 4, di = (y * w + x) * 4;
      px[di] = d[si + 2]; px[di + 1] = d[si + 1]; px[di + 2] = d[si]; px[di + 3] = d[si + 3];
    }
    out[size] = { raw: px, w };
  }
  return out;
}
async function icoImage(e, size) {
  const img = e.png ? sharp(e.png) : sharp(e.raw, { raw: { width: e.w, height: e.w, channels: 4 } });
  return img.resize(size, size, { kernel: size <= 48 ? "nearest" : "lanczos3" }).png().toBuffer();
}
if (OLD_ICO) {
  const { readFileSync } = await import("node:fs");
  const old = readIco(readFileSync(OLD_ICO));
  const show = [256, 48, 32, 16];
  const pick = (ico, s) => ico[s] ?? ico[256];
  const cell = 280, pad = 16;
  const comps = [];
  const label = (text, x, y, color) => ({ input: Buffer.from(`<svg width="560" height="28" xmlns="http://www.w3.org/2000/svg"><text x="0" y="20" font-family="Segoe UI, Arial" font-size="18" fill="${color}">${text}</text></svg>`), left: x, top: y });
  const W = pad + show.length * (cell + pad), H = 2 * (2 * (cell / 2 + 60)) + 40;
  const rows = [["Gammal (v1)", old, true], ["Ny (v2)", null, false]];
  for (const [bgi, bg] of [["#f3f3f3", "#f3f3f3"], ["#1f2a36", "#1f2a36"]].entries()) {
    const top0 = bgi * (H / 2);
    comps.push({ input: Buffer.from(`<svg width="${W}" height="${H / 2}" xmlns="http://www.w3.org/2000/svg"><rect width="100%" height="100%" fill="${bg[1]}"/></svg>`), left: 0, top: top0 });
    const fg = bgi === 0 ? "#222" : "#eee";
    for (const [ri, [name, ico]] of rows.entries()) {
      const y = top0 + 10 + ri * (H / 4);
      comps.push(label(`${name} – ${bgi === 0 ? "ljust" : "mörkt"} skrivbord`, pad, y, fg));
      for (const [ci, s] of show.entries()) {
        const x = pad + ci * (cell + pad);
        // Visas i verklig storlek (256 nedskalad till 128 för att få plats) – små storlekar 1:1, plus en 4× förstoring.
        const real = ico ? await icoImage(pick(ico, s), Math.min(s, 128)) : await sharp(tiles[s] ?? tiles[256]).resize(Math.min(s, 128), Math.min(s, 128)).png().toBuffer();
        comps.push({ input: real, left: x, top: y + 34 });
        if (s <= 48) {
          const z = ico ? await icoImage(pick(ico, s), s * 4 > 128 ? 128 : s * 4) : await sharp(tiles[s]).resize(Math.min(s * 4, 128), Math.min(s * 4, 128), { kernel: "nearest" }).png().toBuffer();
          comps.push({ input: z, left: x + s + 16, top: y + 34 });
        }
        comps.push(label(`${s}px`, x, y + 34 + 132, fg));
      }
    }
  }
  mkdirSync(DESIGN, { recursive: true });
  await sharp({ create: { width: W, height: H, channels: 4, background: "#000" } }).composite(comps).png().toFile(join(DESIGN, "scout-icon-v2.png"));
  console.log("Jämförelseark: docs/review/design/scout-icon-v2.png");
}

console.log("Klart: app.ico, Resources/*.png, installer/wizard-*.png, assets/logo-full.png, public/scout/logo-*.webp");
