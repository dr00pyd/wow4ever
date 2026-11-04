#!/usr/bin/env node
// Signerar ett Forever Scout-släpp (Windows-klient och/eller addon) med ECDSA P-256 / SHA-256.
//
//   node scripts/scout-sign.mjs --genkey <katalog>
//       Skapar ett nyckelpar: <katalog>/scout-release-private.pem (hemlig, checka ALDRIG in) och
//       <katalog>/scout-release-public.pem. Skriver bara ut den publika nyckeln – klistra in den i
//       client/windows/src/ForeverScout.Core/UpdateKeys.cs.
//
//   node scripts/scout-sign.mjs --key <privat.pem> --version x.y.z [--exe <ForeverScout.exe>]
//       [--addon <ForeverScout-addon.zip>] [--addon-version x.y.z] [--out public/scout] [--block 0.9.0,0.9.1]
//       Kopierar filerna till <out> (ForeverScout.exe, ForeverScout-addon.zip) och skriver <out>/version.json.
//       En del som inte anges (t.ex. bara --addon) behålls från befintlig version.json med sin gamla filsignatur.
//
// Format (måste matcha client/windows/src/ForeverScout.Core/ReleaseVerifier.cs):
//   - Filsignatur (clientSig/addonSig) över UTF-8 "ForeverScout-file/v1\n<client|addon>\n<version>\n<sha256 hex>".
//   - signature över kanonisk JSON av exakt SIGNED_FIELDS i bokstavsordning (saknat = null), JSON.stringify.
//   - Signaturer: base64 av IEEE P1363 (r||s, 64 byte).
//   blockedAddons ingår inte i signaturen (servern kan lägga till via SCOUT_BLOCKED_ADDONS).
// Servern ska skicka de signerade fälten och "signature" oförändrade i GET /api/bidrag/version.

import { createHash, createPrivateKey, createPublicKey, generateKeyPairSync, sign, verify } from "node:crypto";
import { copyFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const SIGNED_FIELDS = ["addon", "addonFile", "addonSha256", "addonSig", "client", "clientFile", "clientSig", "issued", "sha256", "v"];
const VERSION = /^\d{1,4}(\.\d{1,6}){0,3}([-+][0-9A-Za-z.]{1,20})?$/;
const CLIENT_FILE = "ForeverScout.exe";
const ADDON_FILE = "ForeverScout-addon.zip";

function args(argv) {
  const out = {};
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (!a.startsWith("--")) fail(`Okänt argument: ${a}`);
    const k = a.slice(2);
    const v = argv[i + 1] && !argv[i + 1].startsWith("--") ? argv[++i] : true;
    out[k] = v;
  }
  return out;
}

function fail(msg) {
  console.error(`scout-sign: ${msg}`);
  process.exit(1);
}

export function canonical(obj) {
  const o = {};
  for (const k of SIGNED_FIELDS) o[k] = obj[k] === undefined ? null : obj[k];
  return JSON.stringify(o);
}

export function fileMessage(kind, version, sha256) {
  return Buffer.from(`ForeverScout-file/v1\n${kind}\n${version}\n${sha256.toLowerCase()}`, "utf8");
}

const signB64 = (key, data) => sign("sha256", data, { key, dsaEncoding: "ieee-p1363" }).toString("base64");
const verifyB64 = (key, data, sig) => verify("sha256", data, { key, dsaEncoding: "ieee-p1363" }, Buffer.from(sig, "base64"));
const sha256 = (buf) => createHash("sha256").update(buf).digest("hex");

function genkey(dir) {
  mkdirSync(dir, { recursive: true });
  const priv = join(dir, "scout-release-private.pem");
  if (existsSync(priv)) fail(`${priv} finns redan – skriver inte över en nyckel.`);
  const { publicKey, privateKey } = generateKeyPairSync("ec", { namedCurve: "prime256v1" });
  const pubPem = publicKey.export({ type: "spki", format: "pem" });
  writeFileSync(priv, privateKey.export({ type: "pkcs8", format: "pem" }), { mode: 0o600, flag: "wx" });
  writeFileSync(join(dir, "scout-release-public.pem"), pubPem, { flag: "wx" });
  process.stdout.write(pubPem);
}

function release(a) {
  if (typeof a.key !== "string") fail("--key <privat.pem> krävs");
  if (typeof a.version !== "string" || !VERSION.test(a.version)) fail("--version x.y.z krävs");
  if (!a.exe && !a.addon) fail("ange --exe och/eller --addon");
  const addonVersion = typeof a["addon-version"] === "string" ? a["addon-version"] : a.version;
  if (!VERSION.test(addonVersion)) fail("ogiltig --addon-version");

  const privateKey = createPrivateKey(readFileSync(a.key));
  if (privateKey.asymmetricKeyDetails?.namedCurve !== "prime256v1") fail("nyckeln är inte ECDSA P-256");
  const publicKey = createPublicKey(privateKey);

  const out = resolve(typeof a.out === "string" ? a.out : join(ROOT, "public", "scout"));
  mkdirSync(out, { recursive: true });
  const manifestPath = join(out, "version.json");
  const prev = existsSync(manifestPath) ? JSON.parse(readFileSync(manifestPath, "utf8")) : {};

  const m = { v: 1 };
  for (const k of SIGNED_FIELDS) if (k !== "v" && k !== "issued" && prev[k] !== undefined) m[k] = prev[k];

  if (a.exe) {
    if (typeof a.exe !== "string" || !existsSync(a.exe)) fail(`hittar inte --exe ${a.exe}`);
    const buf = readFileSync(a.exe);
    if (buf[0] !== 0x4d || buf[1] !== 0x5a) fail("--exe är inte en Windows-exe (MZ)");
    const h = sha256(buf);
    copyFileSync(a.exe, join(out, CLIENT_FILE));
    Object.assign(m, { client: a.version, clientFile: CLIENT_FILE, sha256: h, clientSig: signB64(privateKey, fileMessage("client", a.version, h)) });
  }
  if (a.addon) {
    if (typeof a.addon !== "string" || !existsSync(a.addon)) fail(`hittar inte --addon ${a.addon}`);
    const buf = readFileSync(a.addon);
    if (buf[0] !== 0x50 || buf[1] !== 0x4b) fail("--addon ska vara en zip (mappen ForeverScout/ med .toc/.lua)");
    const h = sha256(buf);
    copyFileSync(a.addon, join(out, ADDON_FILE));
    Object.assign(m, { addon: addonVersion, addonFile: ADDON_FILE, addonSha256: h, addonSig: signB64(privateKey, fileMessage("addon", addonVersion, h)) });
  }
  // Behållna delar måste fortfarande verifiera mot samma nyckel.
  for (const [kind, ver, sh, sig] of [["client", m.client, m.sha256, m.clientSig], ["addon", m.addon, m.addonSha256, m.addonSig]]) {
    if (ver && !verifyB64(publicKey, fileMessage(kind, ver, sh), sig)) fail(`befintlig ${kind}-signatur stämmer inte med nyckeln – signera om med --${kind === "client" ? "exe" : "addon"}`);
  }
  m.issued = new Date().toISOString();

  const blocked = new Set(Array.isArray(prev.blockedAddons) ? prev.blockedAddons : []);
  if (typeof a.block === "string") for (const b of a.block.split(",").map((s) => s.trim()).filter(Boolean)) {
    if (!VERSION.test(b)) fail(`ogiltig version i --block: ${b}`);
    blocked.add(b);
  }

  const signature = signB64(privateKey, Buffer.from(canonical(m), "utf8"));
  if (!verifyB64(publicKey, Buffer.from(canonical(m), "utf8"), signature)) fail("självkontrollen av signaturen misslyckades");
  const manifest = { ...m, blockedAddons: [...blocked], signature };
  writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n");
  console.log(`Skrev ${manifestPath}`);
  if (a.exe) console.log(`  klient ${m.client}  sha256 ${m.sha256}`);
  if (a.addon) console.log(`  addon  ${m.addon}  sha256 ${m.addonSha256}`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const a = args(process.argv.slice(2));
  if (a.genkey) {
    if (typeof a.genkey !== "string") fail("--genkey <katalog>");
    genkey(a.genkey);
  } else release(a);
}
