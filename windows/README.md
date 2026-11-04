# Forever Scout – Windows-klient

Skrivbordsklienten till [Forever-guiden](https://guide.wow4ever.se). Den installerar addonen **ForeverScout**, bevakar
addonens SavedVariables-fil och laddar upp den (plus hotfix-cachen `DBCache.bin`) **varje gång spelet har sparat
filen**: vid `/reload`, karaktärsbyte, utloggning och avslut. I spelet sparar `/scout sync` direkt. Klienten motsvarar
Wowhead Client, men visar vad ditt bidrag ledde till. Kontrakt: `docs/briefs/SCOUT-KONTRAKT.md` (§2 API, §4 klient).

Klienten ska bli öppen källkod (planen är gratis kodsignering via SignPath Foundation). Därför finns inga hemligheter
i koden. Nyckeln skapas av servern vid registrering och sparas bara lokalt.

## Vad appen läser
Det här är hela listan. Den gäller tekniskt, inte bara som ett löfte: **all** fil- och registeråtkomst går genom
`src/ForeverScout.Core/SafeFs.cs`. Allt utanför listan ger ett undantag. Testet `StaticGuardTests` misslyckas om någon
annan källfil använder `File.`, `Directory.`, `FileStream`, `StreamReader(sökväg)`, `FileInfo`, `DriveInfo`,
`FileSystemWatcher`, `Registry.`, `Environment.GetFolderPath`, `Process.GetProcess*`, WMI eller `HttpClient`
(HttpClient får bara användas i `ApiClient.cs`). `SafeFsTests` täcker `..`, symlänkar/junctions, andra enheter,
UNC-/enhetssökvägar, alternativa dataströmmar och andra filer i WoW-katalogen.

**Filer under den valda WoW-katalogen** (t.ex. `…\World of Warcraft\_forever_`):
| Sökväg | Åtkomst |
|---|---|
| `Interface\AddOns\ForeverScout\**` (+ `ForeverScout.new`/`.old` under bytet) | läsa/skriva (installera addonen) |
| `WTF\Account\*\SavedVariables\ForeverScout.lua` | läsa (det som laddas upp) |
| `Cache\ADB\*\DBCache.bin` | läsa (hotfix-cachen) |
| `Interface`, `Interface\AddOns`, `WTF`, `WTF\Account`, `WTF\Account\*`, `…\SavedVariables`, `Cache`, `Cache\ADB`, `Cache\ADB\*` | bara existens och kataloglistning, för att hitta filerna ovan |

Andra filer i WoW-katalogen nekas, till exempel andra addons SavedVariables, `Config.wtf`, `config-cache.wtf` och `Wow.exe`.

**När WoW-katalogen ska hittas** gäller bara existens, inget innehåll: standardsökvägarna
(`Program Files (x86)\World of Warcraft`, `Program Files\World of Warcraft`, `<enhet>:\World of Warcraft`,
`<enhet>:\Games\World of Warcraft` m.fl.), registrets `InstallPath` och det du själv väljer. Där tittar appen på
rotens underkataloger (namn), `<under>\Interface`, `<under>\WTF` och om `<rot>\.build.info` finns.
**Battle.net.config och product.db läses inte** (Battle.net.config innehåller sparade e-postadresser).

**Appens egna filer:** `%APPDATA%\ForeverScout\**`:
- `settings.json`: inställningar. Filerna nycklas med en SHA-256 av sökvägen, aldrig med kontonamnet.
- `key.bin`: nyckeln, krypterad med DPAPI.
- `uploads.log`: logg över varje uppladdning.

Dessutom rör appen den egna exe:n och `ForeverScout.new.exe`/`.old.exe` bredvid den, för självuppdatering.

**Register:**
- Läser bara värdet `InstallPath` under `HKLM\SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft`.
- Läser och skriver bara värdet `ForeverScout` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, och bara när
  du kryssar i "Starta med Windows" (av som standard).

**Processer:** listas inte. Appen läser inget minne och injicerar inget. Uppladdning startas av att filen ändras.

**Nätverk:** bara `https://guide.wow4ever.se`. I Release-byggen kan värden inte bytas: `FOREVERSCOUT_BASEURL` och
`FOREVERSCOUT_DATADIR` fungerar bara i Debug-byggen, för test. Den enda trafiken är:
- `POST /api/bidrag/registrera`, `POST /api/bidrag/koppla` (bara vid första start)
- `POST /api/bidrag/ladda-upp`, `POST /api/bidrag/hotfix`
- `GET /api/bidrag/jag`, `GET /api/bidrag/version`
- `GET /scout/<signerad fil>`

Rubrikerna är bara `User-Agent: ForeverScout/<version>`, `Authorization: Bearer <nyckel>`, `Content-Type` och
`Content-Encoding: gzip`. Inga användarnamn, datornamn, sökvägar eller kontokataloger skickas.

**Kroppen** är filens exakta byte, gzip-komprimerade. Innan något skickas parsas filen. Den måste innehålla **exakt en**
global, `ForeverScoutDB`, annars vägras den. Den måste också gå att parsa helt, annars väntar appen på att spelet
skriver klart. Fönstret **Visa vad som skickas** (kugghjulet och fältmenyn) visar exakt innehåll, sha256 och om filen
redan är skickad. Varje anrop loggas i `%APPDATA%\ForeverScout\uploads.log` med tid, typ, storlek, sha256 och svar.
Knappen **Öppna logg** finns i Inställningar.

## Vad den gör
- WoW-katalogen hittas via registrets `InstallPath` (föräldern och `_forever_` testas) eller standardsökvägar.
  Annars väljer du själv (Inställningar → WoW-katalog). Kataloger som börjar eller slutar med `_` och innehåller
  `Interface` eller `WTF` räknas som kandidater, och `_forever_` sorteras först.
- Addonen installeras i `<spel>\Interface\AddOns\ForeverScout`. Den bäddas in i exe:n vid bygget och tas från
  `client/addon/ForeverScout/**` som den ser ut då. Nyare addonversioner hämtas bara som **signerade** släpp (se
  "Signerade uppdateringar"). Det sker automatiskt när den installerade versionen finns i `blockedAddons`, när servern
  svarar `422 addon-blocked` och när ett nyare signerat släpp finns. Filer med en spärrad addonversion laddas inte upp.
- **Uppladdning när spelet sparat** (`SyncCoordinator`). `WTF\Account\*\SavedVariables\ForeverScout.lua` bevakas med
  FileSystemWatcher. Som reserv pollas filerna var 30:e sekund, eftersom FSW missar händelser på vissa diskar.
  Uppladdningen följer de här reglerna:
  - Debounce: appen väntar tills storlek och ändringstid legat stilla i 5 s.
  - Filen måste gå att parsa helt.
  - Den skickas bara om dess sha256 skiljer sig från förra lyckade uppladdningen för samma kontokatalog.
  - Aldrig två körningar samtidigt. En ändring under en körning ger exakt en körning till efteråt.
  - Knappen **Ladda upp** går samma väg, så inget skickas två gånger.
  - Vid nätfel, 5xx eller 429 görs nya försök med exponentiell backoff (30 s → 1 h). Då skickas alltid den senaste
    versionen av filen.
  - `Cache\ADB\<locale>\DBCache.bin` skickas bara om dess sha256 har ändrats.
- **Kontonamnet** (`WTF\Account\<namn>`) skickas aldrig och visas inte i UI, logg eller felmeddelanden.
- Tabellen räknas lokalt med en egen Lua-tabellparser (`LuaParser.cs`) som bara godtar literaler. Den kör aldrig kod.
- Ingen admin (`asInvoker`).

## Filer
| Sökväg | Innehåll |
|---|---|
| `src/ForeverScout.Core/` | `SafeFs` (enda fil-/registeråtkomsten), Lua-parser, statistik, katalogsökning, filbevakning (`SyncCoordinator`), API-klient, uppladdning/kö, logg och "visa vad som skickas", addoninstallation, signaturkontroll, självuppdatering |
| `src/ForeverScout/` | WinForms-gränssnitt (Lägereld-färger från `app/globals.css`), DPAPI-nyckel, systemfältsikon, `app.manifest`, `app.ico` |
| `tests/` | xUnit: parser, statistik (även mot `client/fixtures/*.lua`), katalogsökning, SafeFs (traversal, symlänkar, UNC, andra enheter), statisk vakt, signaturer, debounce/hash-skip/dirty/halvskriven fil, sv+en |
| `tools/make-icons.mjs` | Ikoner och logotyper ur `assets/Alliance Campfire Emblem.png` (sharp). Se "Ikoner" |
| `assets/` | Originallogotypen, `logo-full.png` (beskuren) och `logo-fire-256.png` |

Lokal data finns i `%APPDATA%\ForeverScout\`: `settings.json` och `key.bin` (DPAPI, CurrentUser). För test kan
katalogen flyttas med `FOREVERSCOUT_DATADIR`.

## Gränssnitt (UI V1)
Utformningen följer `inkorg/ui/BRIEF-UI-V1.md` och prototypen. Design tokens finns i `Theme` (`Support.cs`). De egenritade
kontrollerna ligger i `Ui.cs`: rundade paneler, guldknappar med synlig tangentbordsfokus, inmatningsfält med guldram
vid fokus och statuspiller.
- **Onboarding** (`FirstRunForm`): "Slå dig ner vid elden." Registrering med visningsnamn är den primära handlingen.
  Koppling med kod är tydligt separerad under den. Rutan till höger förklarar vilken data som delas och vad som gäller
  för integriteten. Här finns ingen inloggningsknapp. "Logga in – kommer snart" finns bara utgråad i kugghjuls- och
  fältmenyn. Om namnet är upptaget visas en knapp som hoppar till kodfältet.
- **Dashboard** (`MainForm`):
  - Tre översiktskort: insamlade poster, senaste uppladdning och poäng.
  - En tabell med kolumnerna Typ, Antal, Storlek, Status och Beskrivning. Status är ett av: ingen data hittad, väntar
    på spelsparning, redo att ladda upp, uppladdning pågår, uppladdad ✓ eller fel. "Uppladdad" visas först när
    servern har svarat OK.
  - "Mitt bidrag" visar poäng, plats, **bekräftade fynd** och **godtaget av guiden** var för sig. Båda kommer från
    `/api/bidrag/jag`. Bekräftade fynd visas som "–" tills servern skickar fältet `confirmed`.
  - Lägerjournalen visar de senaste händelserna, ibland med lite humor. Humorn ersätter aldrig ett fel.
  - Statusraden visar addon, uppladdning och nästa synk.
  - I smala fönster döljs beskrivningskolumnen och korten läggs under varandra, och innehållet går att rulla.
  - F5 laddar upp.

## Fönster och aktivitetsfält
- Vid start går appen direkt till fältet och visar inget fönster. Det gäller även med `--tray`, som autostart
  använder. Bara vid allra första starten, innan något är inställt, visas första-start-fönstret.
- Fönstret öppnas genom att dubbelklicka på ikonen eller välja **Öppna Forever Scout** i fältmenyn. Startar man
  programmet en gång till tar den körande instansen fram sitt fönster (mutex plus en namngiven händelse, se
  `SingleInstance`).
- **Minimera** gömmer fönstret från aktivitetsfältet. Första gången visas tipset "Forever Scout körs i bakgrunden".
- **X** frågar "Vill du verkligen stänga …?" med valen Stäng, Minimera till fältet och Avbryt, plus "Fråga inte
  igen". Svaret sparas och kan ändras i Inställningar. Avsluta i menyn stänger utan att fråga. Avstängning och
  utloggning i Windows och Aktivitetshanteraren stoppas aldrig (`CloseBehavior`).
- Fältmenyn har den mörka Lägereld-stilen (`DarkMenuRenderer`) och innehåller: Öppna (fetstil), Ladda upp nu,
  en statusrad, Logga in (utgråad), Pausa automatisk uppladdning, Sök efter uppdateringar, Forever-guiden,
  Inställningar och Avsluta.
- Fältikonen har tre lägen: elden, elden med en amber ring som snurrar under uppladdning, och elden med en röd prick
  vid fel. Verktygstipset visar poäng och senaste status.
- All text finns på svenska och engelska (`ForeverScout.Core/Strings.cs`). Core-fel bär koder (`ErrorCodes`) som
  översätts där. Testerna kräver sv och en för varje nyckel och varje felkod.

## Bygga och testa
Kräver .NET SDK 10.

```powershell
cd client\windows
dotnet test tests
dotnet run --project src\ForeverScout
```

## Signerade uppdateringar
Klienten installerar en ny exe eller addon-zip **bara** om den är signerad med projektets släppnyckel
(ECDSA P-256/SHA-256). Den publika nyckeln ligger i `src/ForeverScout.Core/UpdateKeys.cs`. Saknas signaturen eller
är den fel blir det ett fel, och det finns ingen reserv.

1. Engångsuppgift: `node scripts/scout-sign.mjs --genkey <säker katalog>`. Klistra in den utskrivna publika nyckeln
   i `UpdateKeys.cs` i stället för `__SCOUT_PUBLIC_KEY_PLACEHOLDER__`. Den privata nyckeln får aldrig hamna i git.
   **Release-bygget avbryts så länge platshållaren står kvar.** `-p:ScoutDevBuild=true` ger ett utvecklingsbygge
   som vägrar alla uppdateringar. Det får inte släppas.
2. Släpp: `node scripts/scout-sign.mjs --key <privat.pem> --version 1.0.1 --exe dist\ForeverScout.exe
   --addon ForeverScout-addon.zip [--addon-version 1.0.1] [--block 1.0.0]`. Kommandot skriver
   `public/scout/version.json`, `ForeverScout.exe` och `ForeverScout-addon.zip`. Zip-filen ska innehålla mappen
   `ForeverScout/`.
3. Klienten hämtar `GET /api/bidrag/version` och verifierar `signature` över den kanoniska JSON:en av fälten
   `addon, addonFile, addonSha256, addonSig, client, clientFile, clientSig, issued, sha256, v`. Fälten sorteras i
   bokstavsordning utan blanksteg, och ett saknat fält blir `null`. Sedan laddas filen ner från
   `/scout/<fil>` på samma värd och kontrolleras i två steg: först SHA-256 mot det signerade värdet, sedan
   filsignaturen över `ForeverScout-file/v1\n<client|addon>\n<version>\n<sha256>`.
   `clientUrl`, `addonUrl` och `blockedAddons` ingår inte i signaturen. `blockedAddons` används bara för att avgöra
   när en signerad addon ska hämtas.
4. En ny exe sparas som `ForeverScout.new.exe`. Vid omstart döps den körande filen om till `.old.exe` (det tillåter
   Windows), den nya flyttas in, och nästa start städar bort den gamla.

## Publicera
```powershell
dotnet publish src\ForeverScout -c Release -o dist
```
Det ger `dist\ForeverScout.exe`: self-contained single-file för win-x64 (ca 108 MB), med versionsinformation
(företag, produkt, beskrivning, 1.0.0). Med flit används **ingen** `EnableCompressionInSingleFile`, eftersom den
liknar en packer och ger AV-falsklarm. Av samma skäl används ingen UPX, ingen trimning, ingen obfuskering och ingen
självextrahering av native-filer. Self-contained valdes så att klienten fungerar utan att .NET är installerat.
Vill man ha en liten fil (ca 1 MB) kan man publicera framework-dependent med
`-p:SelfContained=false`. Då visar Windows en egen dialog med nedladdningslänk om .NET 10 Desktop Runtime saknas.

```powershell
& "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" -Scan -ScanType 3 -File dist\ForeverScout.exe
```

## Ikoner
`node client/windows/tools/make-icons.mjs` (kör från guide-roten; sharp finns i `node_modules`). Skriptet skapar:
- `app.ico` (16/20/24/32/48/64/256), tight beskuren kring lägerelden i en rundad ruta (#1c1714) med mjuk oval
  mask, något skärpt för små storlekar. Originalet innehåller Alliansens vapen och ett WoW-"W" (Blizzards
  varumärken). De får **aldrig** hamna i exe-ikonen. Beskärningen ligger väster om muggens vapen, och kanterna
  tonas bort.
- `Resources/logo-header.png` (hela logotypen, används bara inuti appen) och fältikonerna `tray-*.png`.
- `public/scout/logo-full.webp` (≤ 800 px) och `logo-fire-256.webp` till sajten.

## Signera exe:n (senare – inte utfört)
Klienten ska bli öppen källkod så att den kan få gratis kodsignering via SignPath Foundation. Signeringen görs då i
CI från en tagg. Med ett eget certifikat ser steget ut så här:

```powershell
signtool sign /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com `
  /n "Forever-guiden" dist\ForeverScout.exe
signtool verify /pa /v dist\ForeverScout.exe
```
Kör signtool **före** `scout-sign.mjs`, eftersom släppsignaturen gäller den färdiga exe:n. Utgivarnamnet ska vara
detsamma i alla versioner, eftersom SmartScreen-ryktet följer certifikatet. Skicka gärna in varje release till
https://www.microsoft.com/wdsi/filesubmission ("Software developer").

## Testa mot en lokal mock
1. Starta en mock av `/api/bidrag/*` (och `/scout/*`) på `127.0.0.1:3999`. Svarsformaten står i kontraktet §2.
2. `$env:FOREVERSCOUT_BASEURL="http://127.0.0.1:3999"; $env:FOREVERSCOUT_DATADIR="<tom katalog>"`
3. Bygg Debug (`dotnet build src\ForeverScout -c Debug`), eftersom Release ignorerar miljövariablerna. Starta sedan
   exe:n. Med `--screenshots` sparas `scout-client.png`, `scout-close-dialog.png`, `scout-payload.png` och
   `scout-tray-menu.png` i `<datakatalog>\screenshots`, och sedan avslutas programmet.
