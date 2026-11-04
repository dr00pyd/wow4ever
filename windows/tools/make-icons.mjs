#!/usr/bin/env node
// Genererar Forever Scouts ikoner och logotyper ur client/windows/assets/Alliance Campfire Emblem.png.
//   node client/windows/tools/make-icons.mjs
// Utdata:
//   client/windows/assets/logo-full.png        hela logotypen, beskuren (visas bara INUTI appen)
//   client/windows/src/ForeverScout/app.ico     exe-/fönsterikon: bara lägerelden (16/20/24/32/48/64/256)
//   client/windows/src/ForeverScout/Resources/  logo-header.png, tray-normal.png, tray-error.png, tray-upload-0..3.png
//   public/scout/logo-full.webp (≤ 800 px bred), public/scout/logo-fire-256.webp  (till sajten)
// Originalet innehåller Alliansens vapen och ett WoW-"W" (Blizzards varumärken). De får ALDRIG hamna i exe-ikonen:
// ikonen är en tight beskärning av elden med en mjuk oval mask som tonar bort yxan och muggen (med vapnet) i kanterna.
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

// Elden med stenar/vedträn (i originalets koordinater, 1536×1024). Muggens vapen börjar vid x ≈ 865 – håll dig väster om det.
const FIRE = { left: 612, top: 196, width: 246, height: 278 };

if (!existsSync(SRC)) throw new Error(`Saknar ${SRC}`);
mkdirSync(RES, { recursive: true });
mkdirSync(WEB, { recursive: true });

// ---------- Hela logotypen ----------
const full = await sharp(SRC).trim({ threshold: 4 }).png().toBuffer();
await sharp(full).toFile(join(WIN, "assets", "logo-full.png"));
await sharp(full).resize({ width: 360 }).png({ compressionLevel: 9 }).toFile(join(RES, "logo-header.png"));
await sharp(full).resize({ width: 800, withoutEnlargement: true }).webp({ quality: 86, alphaQuality: 90 }).toFile(join(WEB, "logo-full.webp"));

// ---------- Elden i en rundad mörk ruta ----------
async function fireTile(size) {
  const r = Math.round(size * 0.22);
  const inner = Math.round(size * 0.9);
  const w = Math.round(inner * (FIRE.width / FIRE.height));
  // Mjuk oval mask: elden syns, kanterna (yxa, mugg) tonas bort.
  const mask = Buffer.from(
    `<svg width="${w}" height="${inner}"><defs><radialGradient id="g" cx="50%" cy="56%" r="52%">
       <stop offset="55%" stop-color="#fff" stop-opacity="1"/><stop offset="100%" stop-color="#fff" stop-opacity="0"/></radialGradient></defs>
       <rect width="100%" height="100%" fill="url(#g)"/></svg>`,
  );
  let fire = sharp(SRC).extract(FIRE).resize(w, inner, { kernel: "lanczos3" });
  if (size <= 32) fire = fire.sharpen({ sigma: 0.6, m1: 0.8, m2: 1.6 });
  const fireBuf = await fire.png().toBuffer();
  // Originalets alfa gånger maskens alfa ("dest-in").
  const fireMasked = await sharp(fireBuf)
    .composite([{ input: await sharp(mask).resize(w, inner).png().toBuffer(), blend: "dest-in" }])
    .png().toBuffer();
  const bg = Buffer.from(`<svg width="${size}" height="${size}"><rect x="0" y="0" width="${size}" height="${size}" rx="${r}" ry="${r}" fill="${BG}"/></svg>`);
  return sharp(bg)
    .composite([{ input: fireMasked, left: Math.round((size - w) / 2), top: Math.round((size - inner) / 2) + Math.round(size * 0.02) }])
    .png()
    .toBuffer();
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

const sizes = [16, 20, 24, 32, 48, 64, 256];
const tiles = {};
for (const s of sizes) tiles[s] = await fireTile(s);

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

console.log("Klart: app.ico, Resources/*.png, assets/logo-full.png, public/scout/logo-*.webp");
