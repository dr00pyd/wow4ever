#!/usr/bin/env node
// Bygger Forever Scout för Microsoft Store: ett MSIX-paket som laddas upp i Partner Center (Apps and games →
// Forever Scout → Packages). Microsoft signerar paketet efter certifieringen – vi signerar inget här.
//
//   node client/windows/tools/make-msix.mjs            # publicerar store-varianten och packar
//   node client/windows/tools/make-msix.mjs --no-build # packar om dist/store/layout som den är
//
// Utdata: client/windows/dist/store/ForeverScout-<version>.0.msix (+ layout/ med det som packats).
//
// Identiteten kommer från Partner Center (Product management → Product identity) och måste stämma exakt:
//   Name MickaelJonsson.ForeverScout, Publisher CN=003545AA-…, PublisherDisplayName Mickael Jonsson.
// Versionen är exe:ns (ForeverScout.csproj <Version>) + ".0" – Store kräver att fjärde siffran är 0.
//
// Store-varianten (-p:ScoutVariant=store, se Directory.Build.props): ingen egen exe-uppdatering (Store sköter den),
// autostart via startup task nedan (TaskId ForeverScoutStartup, av tills användaren slår på den i appen).
// runFullTrust: vanlig skrivbordsapp – läser WoW:s SavedVariables och hotfix-cache i spelkatalogen som användaren
// valt och installerar addonen där (motivering i Partner Center vid inskickningen).
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { createRequire } from "node:module";

const ROOT = path.resolve(import.meta.dirname, "..");
const REPO = path.resolve(ROOT, "..", "..");
const require = createRequire(path.join(REPO, "package.json"));
const sharp = require("sharp");

const IDENTITY = {
  name: "MickaelJonsson.ForeverScout",
  publisher: "CN=003545AA-C581-4850-B991-CC2C6852B430",
  publisherDisplayName: "Mickael Jonsson",
};

const csproj = fs.readFileSync(path.join(ROOT, "src", "ForeverScout", "ForeverScout.csproj"), "utf8");
const exeVersion = /<Version>(\d+\.\d+\.\d+)<\/Version>/.exec(csproj)?.[1];
if (!exeVersion) throw new Error("hittar inte <Version> i ForeverScout.csproj");
const version = `${exeVersion}.0`;

const OUT = path.join(ROOT, "dist", "store");
const LAYOUT = path.join(OUT, "layout");

// ------------------------------------------------------------------ 1. publicera store-varianten
if (!process.argv.includes("--no-build")) {
  fs.rmSync(LAYOUT, { recursive: true, force: true });
  execFileSync("dotnet", ["publish", path.join(ROOT, "src", "ForeverScout"), "-c", "Release", "-p:ScoutVariant=store", "-o", LAYOUT], {
    stdio: "inherit",
  });
}
if (!fs.existsSync(path.join(LAYOUT, "ForeverScout.exe"))) throw new Error("ForeverScout.exe saknas i layouten");
// Felsökningsfiler hör inte hemma i paketet
for (const f of fs.readdirSync(LAYOUT)) if (f.endsWith(".pdb")) fs.rmSync(path.join(LAYOUT, f));

// ------------------------------------------------------------------ 2. logotyper (bara elden – aldrig Blizzards märken)
const ASSETS = path.join(LAYOUT, "Assets");
fs.mkdirSync(ASSETS, { recursive: true });
const logo = path.join(ROOT, "assets", "logo-fire-256.png");
const square = (size, file) => sharp(logo).resize(size, size, { fit: "contain", background: { r: 0, g: 0, b: 0, alpha: 0 } }).png().toFile(path.join(ASSETS, file));
await square(50, "StoreLogo.png");
await square(44, "Square44x44Logo.png");
await square(44, "Square44x44Logo.targetsize-44_altform-unplated.png");
await square(150, "Square150x150Logo.png");
// bred ruta: elden centrerad på genomskinlig bakgrund
const mark = await sharp(logo).resize(150, 150).png().toBuffer();
await sharp({ create: { width: 310, height: 150, channels: 4, background: { r: 0, g: 0, b: 0, alpha: 0 } } })
  .composite([{ input: mark, left: 80, top: 0 }])
  .png()
  .toFile(path.join(ASSETS, "Wide310x150Logo.png"));

// ------------------------------------------------------------------ 3. AppxManifest.xml
const xml = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
const manifest = `<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap desktop rescap">
  <Identity Name="${xml(IDENTITY.name)}" Publisher="${xml(IDENTITY.publisher)}" Version="${version}" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>Forever Scout</DisplayName>
    <PublisherDisplayName>${xml(IDENTITY.publisherDisplayName)}</PublisherDisplayName>
    <Logo>Assets\\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Resources>
    <Resource Language="sv-SE" />
    <Resource Language="en-US" />
  </Resources>
  <Applications>
    <Application Id="ForeverScout" Executable="ForeverScout.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements
        DisplayName="Forever Scout"
        Description="Laddar upp addon-data från WoW Forever till Forever-guiden (guide.wow4ever.se)."
        BackgroundColor="transparent"
        Square150x150Logo="Assets\\Square150x150Logo.png"
        Square44x44Logo="Assets\\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Assets\\Wide310x150Logo.png" />
      </uap:VisualElements>
      <Extensions>
        <desktop:Extension Category="windows.startupTask" Executable="ForeverScout.exe" EntryPoint="Windows.FullTrustApplication">
          <desktop:StartupTask TaskId="ForeverScoutStartup" Enabled="false" DisplayName="Forever Scout" />
        </desktop:Extension>
      </Extensions>
    </Application>
  </Applications>
  <Capabilities>
    <Capability Name="internetClient" />
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
`;
fs.writeFileSync(path.join(LAYOUT, "AppxManifest.xml"), manifest);

// ------------------------------------------------------------------ 4. packa
const kits = "C:/Program Files (x86)/Windows Kits/10/bin";
const sdk = fs
  .readdirSync(kits)
  .filter((d) => /^10\.\d+\.\d+\.\d+$/.test(d) && fs.existsSync(path.join(kits, d, "x64", "makeappx.exe")))
  .sort((a, b) => a.localeCompare(b, undefined, { numeric: true }))
  .pop();
if (!sdk) throw new Error("makeappx.exe saknas – installera Windows SDK");
const msix = path.join(OUT, `ForeverScout-${version}.msix`);
fs.rmSync(msix, { force: true });
execFileSync(path.join(kits, sdk, "x64", "makeappx.exe"), ["pack", "/d", LAYOUT, "/p", msix, "/o"], { stdio: "inherit" });
const mb = (fs.statSync(msix).size / 1024 / 1024).toFixed(1);
console.log(`\n${path.relative(REPO, msix)}  (${mb} MB, version ${version}) – ladda upp under Packages i Partner Center.`);
