# Forever Scout

**Svenska** · [English](#english)

Forever Scout samlar in speldata från WoW: Forever medan du spelar – var tränare, försäljare och questgivare står, vad de lär ut och säljer, malm och örter, loot och flygpunkter – och skickar den till [guide.wow4ever.se](https://guide.wow4ever.se/bidra). Där bekräftas uppgifterna när flera spelare rapporterat samma sak och visas på kartorna. Gratis, ingen reklam, öppen källkod.

| Mapp | Innehåll |
|---|---|
| `addon/` | WoW-addonen ForeverScout (loggar till SavedVariables) |
| `windows/` | Windows-appen (ikon i aktivitetsfältet, laddar upp automatiskt) |
| `fixtures/` | Exempel på addonens sparfil |
| `tools/scout-sign.mjs` | Hur släpp signeras (ECDSA P-256) – den privata nyckeln finns inte här |

## Vad appen läser – och inget annat
- I WoW-katalogen: `WTF\Account\*\SavedVariables\ForeverScout.lua`, `Cache\ADB\*\DBCache.bin` och mappen `Interface\AddOns\ForeverScout` (som den installerar).
- Sin egen mapp `%APPDATA%\ForeverScout` (inställningar, kö, logg).
- Ett registervärde: Blizzards `InstallPath` för World of Warcraft (för att hitta spelet). Om du kryssar i "Starta med Windows" skrivs dessutom värdet `ForeverScout` under `HKCU\…\Run`.
- Sin egen exe-fil (vid självuppdatering, bara om uppdateringen är signerad med vår nyckel).
- Nätverk: bara `https://guide.wow4ever.se`.

Den läser inte spelets minne, processlistan, Battle.net-inställningar eller några andra filer. All filåtkomst går genom en enda klass (`SafeFs`), och ett test underkänner bygget om någon annan kod försöker läsa filer, registret, processer eller nätet. Menyn **Visa vad som skickas** visar exakt vad som laddas upp, och varje uppladdning loggas lokalt. Addonen sparar inga karaktärs-, konto- eller realmnamn.

## Bygga själv
Se [`windows/README.md`](windows/README.md) (.NET 10) och [`addon/README.md`](addon/README.md). Uppdateringar installeras bara om de är signerade med vår nyckel; den publika nyckeln ligger i `windows/src/ForeverScout.Core/UpdateKeys.cs`.

## Säkerhet
Hittar du ett problem: använd **Rapportera fel** på [guide.wow4ever.se/bidra](https://guide.wow4ever.se/bidra#rapportera-fel). Skicka aldrig hemligheter i issues.

---

## English

Forever Scout collects game data from WoW: Forever while you play – where trainers, vendors and quest givers stand, what they teach and sell, ore and herbs, loot and flight paths – and sends it to [guide.wow4ever.se](https://guide.wow4ever.se/en/contribute). Data is confirmed once several players report the same thing and is then shown on the maps. Free, no ads, open source.

**What the app reads – and nothing else:** in the WoW folder `WTF\Account\*\SavedVariables\ForeverScout.lua`, `Cache\ADB\*\DBCache.bin` and `Interface\AddOns\ForeverScout` (which it installs); its own folder `%APPDATA%\ForeverScout`; one registry value (Blizzard's World of Warcraft `InstallPath`), plus `HKCU\…\Run\ForeverScout` only if you tick "Start with Windows"; its own exe file when applying a signed update; network only `https://guide.wow4ever.se`. It never reads game memory, the process list, Battle.net settings or any other files. All file access goes through one class (`SafeFs`) and a test fails the build if other code touches files, the registry, processes or the network. **Show what is sent** displays the exact upload; every upload is logged locally. The addon stores no character, account or realm names.

Build instructions: [`windows/README.md`](windows/README.md), [`addon/README.md`](addon/README.md). Licence: MIT.

World of Warcraft and Blizzard Entertainment are trademarks of Blizzard Entertainment, Inc. This project is not affiliated with Blizzard.
