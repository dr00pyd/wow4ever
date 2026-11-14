#!/usr/bin/env node
// Signerar ett Forever Scout-släpp (Windows-klient och/eller addon) med ECDSA P-256 / SHA-256.
//
//   node scripts/scout-sign.mjs --genkey <katalog>
//       Skapar ett nyckelpar: <katalog>/scout-release-private.pem (hemlig, checka ALDRIG in) och
//       <katalog>/scout-release-public.pem. Skriver bara ut den publika nyckeln – klistra in den i
//       client/windows/src/ForeverScout.Core/UpdateKeys.cs.
//
//   node scripts/scout-sign.mjs --key <privat.pem> --version x.y.z [--exe <full/ForeverScout.exe>]
//       [--exe-lite <lite/ForeverScout.exe>] [--lite-version x.y.z]
//       [--setup <ForeverScout-Setup-x.y.z.exe>] [--setup-version x.y.z]
//       [--addon <ForeverScout-addon.zip>] [--addon-version x.y.z] [--out public/scout] [--block 0.9.0,0.9.1]
//       [--mac <ForeverScout-mac.zip>] [--mac-version x.y.z] [--allow-unnotarized] [--dmg <ForeverScout-x.y.z.dmg>]
//       [--linux <ForeverScout-x86_64.AppImage>] [--linux-version x.y.z]
//       Kopierar filerna till <out> (ForeverScout.exe, ForeverScout-addon.zip, ForeverScout-mac.zip) och skriver
//       <out>/version.json. En del som inte anges (t.ex. bara --addon) behålls från befintlig version.json med sin
//       gamla filsignatur.
//
//   --mac: zip:en med det FÄRDIGA macOS-paketet – kodsignerat (Developer ID), notariserat och staplat
//       (client/mac/build.sh). Vår signatur läggs alltså ovanpå Apples. Skriptet kontrollerar att zip:en innehåller
//       *.app/Contents/Info.plist, _CodeSignature/CodeResources och den staplade biljetten Contents/CodeResources;
//       --allow-unnotarized hoppar över de två sista (bara för test, släpp aldrig en sådan fil).
//
// Format (måste matcha client/windows/src/ForeverScout.Core/ReleaseVerifier.cs):
//   - Filsignatur (clientSig/addonSig) över UTF-8 "ForeverScout-file/v1\n<client|addon>\n<version>\n<sha256 hex>".
//   - signature över kanonisk JSON av exakt SIGNED_FIELDS i bokstavsordning (saknat = null), JSON.stringify.
//     Den ÄNDRAS INTE i format: klienter 1.0.0–1.0.2 verifierar den och uppdaterar till full-exe:n (clientFile).
//   - signature2 (från 1.0.3) över SIGNED_FIELDS_2 = SIGNED_FIELDS + lite, liteFile, liteSha256, liteSig.
//     Nya klienter verifierar signature2 och väljer fil efter variant (full → clientFile, lite → liteFile).
//   - Lite-filens filsignatur: kind "lite". Installationsprogrammet (setup, setupFile, setupSha256, setupSig) ingår
//     också i signature2 (kind "setup") – det används inte för uppdateringar, bara för nedladdningssidan.
//   - signature5 (Linux-klienten och nedladdningssidan) över SIGNED_FIELDS_5 = SIGNED_FIELDS_4 + linux, linuxFile,
//     linuxSha256, linuxSig: AppImage med den fristående Linux-appen (client/linux), filsignatur kind "linux".
//     Skrivs alltid (saknat = null). Windows- och macOS-klienterna läser den inte; signature–signature4 är oförändrade.
//   - signature4 (bara nedladdningssidan) över SIGNED_FIELDS_4 = SIGNED_FIELDS_3 + dmg, dmgFile, dmgSha256, dmgSig:
//     DMG:n med samma notariserade app (client/mac/build.sh --dmg), filsignatur kind "dmg", version = --mac-version.
//     Inga klienter läser den – Mac-appen uppdaterar sig med zip:en (signature3).
//   - signature3 (macOS-klienten) över SIGNED_FIELDS_3 = SIGNED_FIELDS_2 + mac, macFile, macSha256, macSig. Skrivs
//     alltid (saknat = null). Windows-klienterna läser den inte; signature och signature2 är oförändrade.
//     Mac-filens filsignatur: kind "mac".
//   - Signaturer: base64 av IEEE P1363 (r||s, 64 byte).
//   blockedAddons ingår inte i signaturen (servern kan lägga till via SCOUT_BLOCKED_ADDONS).
// Servern ska skicka de signerade fälten och "signature" oförändrade i GET /api/bidrag/version.

import { createHash, createPrivateKey, createPublicKey, generateKeyPairSync, sign, verify } from "node:crypto";
import { copyFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const SIGNED_FIELDS = ["addon", "addonFile", "addonSha256", "addonSig", "client", "clientFile", "clientSig", "issued", "sha256", "v"];
const LITE_FIELDS = ["lite", "liteFile", "liteSha256", "liteSig"];
const SETUP_FIELDS = ["setup", "setupFile", "setupSha256", "setupSig"];
const SIGNED_FIELDS_2 = [...SIGNED_FIELDS, ...LITE_FIELDS, ...SETUP_FIELDS].sort();
const MAC_FIELDS = ["mac", "macFile", "macSha256", "macSig"];
const SIGNED_FIELDS_3 = [...SIGNED_FIELDS_2, ...MAC_FIELDS].sort();
const DMG_FIELDS = ["dmg", "dmgFile", "dmgSha256", "dmgSig"];
const SIGNED_FIELDS_4 = [...SIGNED_FIELDS_3, ...DMG_FIELDS].sort();
const LINUX_FIELDS = ["linux", "linuxFile", "linuxSha256", "linuxSig"];
const SIGNED_FIELDS_5 = [...SIGNED_FIELDS_4, ...LINUX_FIELDS].sort();
const VERSION = /^\d{1,4}(\.\d{1,6}){0,3}([-+][0-9A-Za-z.]{1,20})?$/;
const CLIENT_FILE = "ForeverScout.exe";
const LITE_FILE = "ForeverScout-lite.exe";
const SETUP_FILE = "ForeverScout-Setup.exe";
const ADDON_FILE = "ForeverScout-addon.zip";
const MAC_FILE = "ForeverScout-mac.zip";
const DMG_FILE = "ForeverScout-mac.dmg";
const LINUX_FILE = "ForeverScout-x86_64.AppImage";

/** Beta-kanalens filnamn: "ForeverScout-beta.exe", "ForeverScout-addon-beta.zip", "ForeverScout-beta-x86_64.AppImage". */
export function betaName(n) {
  if (n.endsWith(".AppImage")) return n.replace("-x86_64", "-beta-x86_64");
  const dot = n.lastIndexOf(".");
  return dot < 0 ? `${n}-beta` : `${n.slice(0, dot)}-beta${n.slice(dot)}`;
}

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

export function canonical(obj, fields = SIGNED_FIELDS) {
  const o = {};
  for (const k of fields) o[k] = obj[k] === undefined ? null : obj[k];
  return JSON.stringify(o);
}

export const canonical2 = (obj) => canonical(obj, SIGNED_FIELDS_2);
export const canonical3 = (obj) => canonical(obj, SIGNED_FIELDS_3);
export const canonical4 = (obj) => canonical(obj, SIGNED_FIELDS_4);
export const canonical5 = (obj) => canonical(obj, SIGNED_FIELDS_5);
export { SIGNED_FIELDS, SIGNED_FIELDS_2, SIGNED_FIELDS_3, SIGNED_FIELDS_4, SIGNED_FIELDS_5 };

/** Namnen på posterna i en zip (centralkatalogen; zip64 stöds inte – macOS-zip:en är långt under 4 GB). */
export function zipEntryNames(buf) {
  const min = Math.max(0, buf.length - 65557);
  let eocd = -1;
  for (let i = buf.length - 22; i >= min; i--) if (buf.readUInt32LE(i) === 0x06054b50) { eocd = i; break; }
  if (eocd < 0) throw new Error("hittar inte zip:ens slutpost");
  const count = buf.readUInt16LE(eocd + 10), size = buf.readUInt32LE(eocd + 12), offset = buf.readUInt32LE(eocd + 16);
  if (count === 0xffff || size === 0xffffffff || offset === 0xffffffff) throw new Error("zip64 stöds inte");
  const names = [];
  let p = offset;
  for (let k = 0; k < count; k++) {
    if (buf.readUInt32LE(p) !== 0x02014b50) throw new Error("trasig centralkatalog");
    const n = buf.readUInt16LE(p + 28), x = buf.readUInt16LE(p + 30), c = buf.readUInt16LE(p + 32);
    names.push(buf.subarray(p + 46, p + 46 + n).toString("utf8"));
    p += 46 + n + x + c;
  }
  return names;
}

/** Kontrollerar att zip:en är ett macOS-paket som är kodsignerat och (om inte allowUnnotarized) staplat. */
export function checkMacZip(buf, allowUnnotarized = false) {
  if (buf[0] !== 0x50 || buf[1] !== 0x4b) return "--mac ska vara en zip (ditto -c -k --keepParent Forever\\ Scout.app)";
  let names;
  try { names = zipEntryNames(buf); } catch (e) { return `--mac: ${e.message}`; }
  const apps = new Set(names.map((n) => n.match(/^([^/]+\.app)\//)?.[1]).filter(Boolean));
  if (apps.size !== 1) return "--mac ska innehålla exakt ett .app-paket i roten";
  const [app] = apps;
  const has = (rel) => names.includes(`${app}/${rel}`);
  if (!has("Contents/Info.plist")) return `--mac: ${app}/Contents/Info.plist saknas`;
  if (!has("Contents/MacOS/ForeverScout")) return `--mac: ${app}/Contents/MacOS/ForeverScout saknas`;
  if (allowUnnotarized) return null;
  if (!has("Contents/_CodeSignature/CodeResources")) return "--mac: paketet är inte kodsignerat (_CodeSignature saknas) – kör build.sh med signering";
  if (!has("Contents/CodeResources")) return "--mac: ingen staplad notariseringsbiljett (Contents/CodeResources) – kör xcrun stapler staple före zip";
  return null;
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
  if (!a.exe && !a["exe-lite"] && !a.setup && !a.addon && !a.mac && !a.dmg && !a.linux) fail("ange --exe, --exe-lite, --setup, --addon, --mac, --dmg och/eller --linux");
  const linuxVersion = typeof a["linux-version"] === "string" ? a["linux-version"] : a.version;
  if (!VERSION.test(linuxVersion)) fail("ogiltig --linux-version");
  const macVersion = typeof a["mac-version"] === "string" ? a["mac-version"] : a.version;
  if (!VERSION.test(macVersion)) fail("ogiltig --mac-version");
  const setupVersion = typeof a["setup-version"] === "string" ? a["setup-version"] : a.version;
  if (!VERSION.test(setupVersion)) fail("ogiltig --setup-version");
  const liteVersion = typeof a["lite-version"] === "string" ? a["lite-version"] : a.version;
  if (!VERSION.test(liteVersion)) fail("ogiltig --lite-version");
  const addonVersion = typeof a["addon-version"] === "string" ? a["addon-version"] : a.version;
  if (!VERSION.test(addonVersion)) fail("ogiltig --addon-version");

  const privateKey = createPrivateKey(readFileSync(a.key));
  if (privateKey.asymmetricKeyDetails?.namedCurve !== "prime256v1") fail("nyckeln är inte ECDSA P-256");
  const publicKey = createPublicKey(privateKey);

  const out = resolve(typeof a.out === "string" ? a.out : join(ROOT, "public", "scout"));
  mkdirSync(out, { recursive: true });
  // --channel beta: egen signerad version-beta.json och egna filnamn (ForeverScout-beta.exe …) bredvid de vanliga –
  // klienter med "Betaversioner" påslaget hämtar den (GET /api/bidrag/version?kanal=beta). Finns ingen beta-fil än
  // börjar den som en kopia av den vanliga, så att delar som inte anges (t.ex. Mac) pekar på de vanliga filerna.
  if (a.channel !== undefined && a.channel !== "beta") fail("--channel kan bara vara beta");
  const beta = a.channel === "beta";
  const fileName = (n) => (beta ? betaName(n) : n);
  const manifestPath = join(out, beta ? "version-beta.json" : "version.json");
  const stablePath = join(out, "version.json");
  const prev = existsSync(manifestPath)
    ? JSON.parse(readFileSync(manifestPath, "utf8"))
    : beta && existsSync(stablePath)
      ? JSON.parse(readFileSync(stablePath, "utf8"))
      : {};

  const m = { v: 1 };
  for (const k of SIGNED_FIELDS_5) if (k !== "v" && k !== "issued" && prev[k] !== undefined) m[k] = prev[k];

  if (a.exe) {
    if (typeof a.exe !== "string" || !existsSync(a.exe)) fail(`hittar inte --exe ${a.exe}`);
    const buf = readFileSync(a.exe);
    if (buf[0] !== 0x4d || buf[1] !== 0x5a) fail("--exe är inte en Windows-exe (MZ)");
    const h = sha256(buf);
    copyFileSync(a.exe, join(out, fileName(CLIENT_FILE)));
    Object.assign(m, { client: a.version, clientFile: fileName(CLIENT_FILE), sha256: h, clientSig: signB64(privateKey, fileMessage("client", a.version, h)) });
  }
  if (a["exe-lite"]) {
    const p = a["exe-lite"];
    if (typeof p !== "string" || !existsSync(p)) fail(`hittar inte --exe-lite ${p}`);
    const buf = readFileSync(p);
    if (buf[0] !== 0x4d || buf[1] !== 0x5a) fail("--exe-lite är inte en Windows-exe (MZ)");
    if (a.exe && sha256(buf) === sha256(readFileSync(a.exe))) fail("--exe och --exe-lite är samma fil");
    const h = sha256(buf);
    copyFileSync(p, join(out, fileName(LITE_FILE)));
    Object.assign(m, { lite: liteVersion, liteFile: fileName(LITE_FILE), liteSha256: h, liteSig: signB64(privateKey, fileMessage("lite", liteVersion, h)) });
  }
  if (a.setup) {
    if (typeof a.setup !== "string" || !existsSync(a.setup)) fail(`hittar inte --setup ${a.setup}`);
    const buf = readFileSync(a.setup);
    if (buf[0] !== 0x4d || buf[1] !== 0x5a) fail("--setup är inte en Windows-exe (MZ)");
    const h = sha256(buf);
    copyFileSync(a.setup, join(out, fileName(SETUP_FILE)));
    Object.assign(m, { setup: setupVersion, setupFile: fileName(SETUP_FILE), setupSha256: h, setupSig: signB64(privateKey, fileMessage("setup", setupVersion, h)) });
  }
  if (a.addon) {
    if (typeof a.addon !== "string" || !existsSync(a.addon)) fail(`hittar inte --addon ${a.addon}`);
    const buf = readFileSync(a.addon);
    if (buf[0] !== 0x50 || buf[1] !== 0x4b) fail("--addon ska vara en zip (mappen ForeverScout/ med .toc/.lua)");
    const h = sha256(buf);
    copyFileSync(a.addon, join(out, fileName(ADDON_FILE)));
    Object.assign(m, { addon: addonVersion, addonFile: fileName(ADDON_FILE), addonSha256: h, addonSig: signB64(privateKey, fileMessage("addon", addonVersion, h)) });
  }
  if (a.mac) {
    if (typeof a.mac !== "string" || !existsSync(a.mac)) fail(`hittar inte --mac ${a.mac}`);
    const buf = readFileSync(a.mac);
    const problem = checkMacZip(buf, a["allow-unnotarized"] === true);
    if (problem) fail(problem);
    const h = sha256(buf);
    copyFileSync(a.mac, join(out, fileName(MAC_FILE)));
    Object.assign(m, { mac: macVersion, macFile: fileName(MAC_FILE), macSha256: h, macSig: signB64(privateKey, fileMessage("mac", macVersion, h)) });
  }
  if (a.dmg) {
    if (typeof a.dmg !== "string" || !existsSync(a.dmg)) fail(`hittar inte --dmg ${a.dmg}`);
    const buf = readFileSync(a.dmg);
    // UDIF-avbilder slutar med ett 512 byte långt "koly"-block.
    if (buf.length < 4096 || buf.subarray(buf.length - 512, buf.length - 508).toString("latin1") !== "koly") fail("--dmg är inte en macOS-diskavbild (koly saknas)");
    const h = sha256(buf);
    copyFileSync(a.dmg, join(out, fileName(DMG_FILE)));
    Object.assign(m, { dmg: macVersion, dmgFile: fileName(DMG_FILE), dmgSha256: h, dmgSig: signB64(privateKey, fileMessage("dmg", macVersion, h)) });
  }
  if (a.linux) {
    if (typeof a.linux !== "string" || !existsSync(a.linux)) fail(`hittar inte --linux ${a.linux}`);
    const buf = readFileSync(a.linux);
    // En AppImage är en ELF-fil (startaren) följd av en squashfs-avbild ("hsqs") med appen.
    if (buf.length < 1024 * 1024 || buf.readUInt32BE(0) !== 0x7f454c46 || buf.indexOf(Buffer.from("hsqs")) < 0) fail("--linux ska vara en AppImage (client/linux/packaging/make-appimage.sh)");
    const h = sha256(buf);
    copyFileSync(a.linux, join(out, fileName(LINUX_FILE)));
    Object.assign(m, { linux: linuxVersion, linuxFile: fileName(LINUX_FILE), linuxSha256: h, linuxSig: signB64(privateKey, fileMessage("linux", linuxVersion, h)) });
  }
  // Behållna delar måste fortfarande verifiera mot samma nyckel.
  for (const [kind, ver, sh, sig] of [["client", m.client, m.sha256, m.clientSig], ["lite", m.lite, m.liteSha256, m.liteSig], ["setup", m.setup, m.setupSha256, m.setupSig], ["addon", m.addon, m.addonSha256, m.addonSig], ["mac", m.mac, m.macSha256, m.macSig], ["dmg", m.dmg, m.dmgSha256, m.dmgSig], ["linux", m.linux, m.linuxSha256, m.linuxSig]]) {
    if (ver && !verifyB64(publicKey, fileMessage(kind, ver, sh), sig)) fail(`befintlig ${kind}-signatur stämmer inte med nyckeln – signera om med --${kind === "client" ? "exe" : kind === "lite" ? "exe-lite" : kind === "setup" ? "setup" : kind === "mac" ? "mac" : kind === "dmg" ? "dmg" : kind === "linux" ? "linux" : "addon"}`);
  }
  m.issued = new Date().toISOString();

  const blocked = new Set(Array.isArray(prev.blockedAddons) ? prev.blockedAddons : []);
  if (typeof a.block === "string") for (const b of a.block.split(",").map((s) => s.trim()).filter(Boolean)) {
    if (!VERSION.test(b)) fail(`ogiltig version i --block: ${b}`);
    blocked.add(b);
  }

  // signature: oförändrat format (gamla fält) – för klienter 1.0.0–1.0.2. signature2: gamla + lite-fält.
  const signature = signB64(privateKey, Buffer.from(canonical(m), "utf8"));
  if (!verifyB64(publicKey, Buffer.from(canonical(m), "utf8"), signature)) fail("självkontrollen av signaturen misslyckades");
  const signature2 = signB64(privateKey, Buffer.from(canonical2(m), "utf8"));
  if (!verifyB64(publicKey, Buffer.from(canonical2(m), "utf8"), signature2)) fail("självkontrollen av signature2 misslyckades");
  // signature3: alla fält inklusive mac-fälten – bara macOS-klienten läser den.
  const signature3 = signB64(privateKey, Buffer.from(canonical3(m), "utf8"));
  if (!verifyB64(publicKey, Buffer.from(canonical3(m), "utf8"), signature3)) fail("självkontrollen av signature3 misslyckades");
  // signature4: dessutom DMG-fälten – bara nedladdningssidan.
  const signature4 = signB64(privateKey, Buffer.from(canonical4(m), "utf8"));
  if (!verifyB64(publicKey, Buffer.from(canonical4(m), "utf8"), signature4)) fail("självkontrollen av signature4 misslyckades");
  // signature5: dessutom linux-fälten – Linux-klienten och nedladdningssidan.
  const signature5 = signB64(privateKey, Buffer.from(canonical5(m), "utf8"));
  if (!verifyB64(publicKey, Buffer.from(canonical5(m), "utf8"), signature5)) fail("självkontrollen av signature5 misslyckades");
  const manifest = { ...m, blockedAddons: [...blocked], signature, signature2, signature3, signature4, signature5 };
  writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + "\n");
  console.log(`Skrev ${manifestPath}`);
  if (a.exe) console.log(`  klient ${m.client}  sha256 ${m.sha256}`);
  if (a["exe-lite"]) console.log(`  lite   ${m.lite}  sha256 ${m.liteSha256}`);
  if (a.setup) console.log(`  setup  ${m.setup}  sha256 ${m.setupSha256}`);
  if (a.addon) console.log(`  addon  ${m.addon}  sha256 ${m.addonSha256}`);
  if (a.mac) console.log(`  mac    ${m.mac}  sha256 ${m.macSha256}`);
  if (a.dmg) console.log(`  dmg    ${m.dmg}  sha256 ${m.dmgSha256}`);
  if (a.linux) console.log(`  linux  ${m.linux}  sha256 ${m.linuxSha256}`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const a = args(process.argv.slice(2));
  if (a.genkey) {
    if (typeof a.genkey !== "string") fail("--genkey <katalog>");
    genkey(a.genkey);
  } else release(a);
}
