# Forever Scout – Windows-klient

Skrivbordsklienten till [Forever-guiden](https://guide.wow4ever.se). Den installerar addonen **ForeverScout**, bevakar
addonens SavedVariables-fil och laddar upp den (plus hotfix-cachen `DBCache.bin`) **varje gång spelet har sparat
filen**: vid `/reload`, karaktärsbyte, utloggning och avslut. I spelet sparar `/scout sync` direkt. Den visar också vad
ditt bidrag ledde till. Kontrakt: `docs/briefs/SCOUT-KONTRAKT.md` (§2 API, §4 klient).

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
| `installer/` | Inno Setup-skriptet och guidens bilder. Se "Installationsprogram" |
| `Directory.Build.props` | `ScoutVariant` (full/lite) och separata obj/bin per variant |
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
- **Övertagande (från 1.0.3):** startar en ny version, eller samma version från en annan exe (t.ex. lite i stället
  för full), medan en äldre kör, frågar den nya över en namngiven pipe. Pipen gäller bara samma användare och
  session; inga processer listas och inga filer läses. Den gamla avslutar sig då och den nya tar över. En äldre kopia
  någon annanstans får aldrig ta över från en nyare. Svarar ingen inom 3 s (1.0.0–1.0.2 kan inte lämna över) visas
  "En äldre Forever Scout körs fortfarande – avsluta den via ikonen vid klockan (Avsluta) och starta igen."
- **Autostart:** är "Starta med Windows" på skrivs Run-värdet om till den exe som körs vid varje start, så att det
  alltid pekar på den senaste filen.
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

1. Engångsuppgift: `node scripts/scout-sign.mjs --genkey <säker katalog>`. Klistra in den publika nyckeln i
   `UpdateKeys.cs`. Release-bygget avbryts så länge platshållaren står kvar. Den privata nyckeln får aldrig hamna i git.
2. Släpp (från 1.0.3):
   ```powershell
   node scripts/scout-sign.mjs --key <privat.pem> --version 1.0.3 `
     --exe client\windows\dist\full\ForeverScout.exe --exe-lite client\windows\dist\lite\ForeverScout.exe `
     --setup client\windows\dist\setup\ForeverScout-Setup-1.0.3.exe [--addon ForeverScout-addon.zip --addon-version 1.0.1]
   ```
   Kommandot skriver `public/scout/version.json` och filerna `ForeverScout.exe` (full), `ForeverScout-lite.exe`,
   `ForeverScout-Setup.exe` och `ForeverScout-addon.zip`. Delar som inte anges behålls från den förra filen.
3. **Två signaturer, och gamla klienter fungerar som förut:**
   - `signature` täcker exakt de gamla fälten `addon, addonFile, addonSha256, addonSig, client, clientFile,
     clientSig, issued, sha256, v`. Formatet är oförändrat. Klienter **1.0.0–1.0.2** verifierar den och uppdaterar
     till full-exe:n (`clientFile`).
   - `signature2` täcker de gamla fälten plus `lite, liteFile, liteSha256, liteSig` och
     `setup, setupFile, setupSha256, setupSig`. Klienter från 1.0.3 verifierar den, och om den finns måste den
     stämma (ingen reserv). Sedan väljer de fil efter variant: full uppdateras från `clientFile`, lite från
     `liteFile`. Installationsprogrammet (`setup`) används bara på nedladdningssidan.
   - För båda gäller kanonisk JSON med fälten i bokstavsordning, utan blanksteg och med saknat fält som `null`.
     Filsignaturen gäller `ForeverScout-file/v1\n<client|lite|setup|addon>\n<version>\n<sha256>`, så att en fil inte
     kan utges för en annan typ.
   - Bevis: `tests/VariantReleaseTests.cs` kör den **ordagranna** 1.0.2-koden (`tests/Compat/ReleaseVerifier102.cs`,
     ur git-taggen `live-8`) mot ett version.json i det nya formatet. Koden godtar den och laddar full-exe:n, och
     lite-exe:n kan inte smita in.
4. En ny exe sparas som `ForeverScout.new.exe`. Vid omstart döps den körande filen om till `.old.exe` och den nya
   flyttas in. Det fungerar också i installationsmappen, eftersom den ligger per användare.
   Från 1.0.5 tål självuppdateringen att filer är tillfälligt låsta:
   - **Låsta filer:** Defender och SmartScreen skannar ofta en nyss skriven 108 MB-exe. Därför görs varje flytt och
     radering om upp till 10 gånger under ungefär 15 s (`IoRetry`), och varje omförsök loggas.
   - **Rester:** gamla `.new.exe` och `.new.exe.tmp` från avbrutna försök tas bort först.
   - **Låst `.old.exe`:** om en gammal process fortfarande kör från den används `.old-<tidsstämpel>.exe`, och
     `CleanupOld` städar alla `.old*.exe`. SafeFs tillåter exakt det mönstret för exe:ns eget namn och inget annat.
   - **Rollback:** misslyckas det andra namnbytet flyttas den gamla exe:n tillbaka.
   - **Diagnostik:** vid fel skrivs steget, undantagstypen, HResult och bara filnamnet till `uploads.log` och
     lägerjournalen. Stegen är `download`, `stage`, `swap-delete`, `swap-move1`, `swap-move2` och `restart`.
     Dialogen förklarar felet i klartext, och efter två misslyckanden erbjuder den nedladdningssidan.
   - **Omstart:** den nya exe:n startas med `--restarted` och väntar på mutexen tills den gamla processen har
     avslutats.

## Publicera (två varianter av samma kod)
```powershell
dotnet publish src\ForeverScout -c Release -p:ScoutVariant=full -o dist\full   # fristående, ~108 MB
dotnet publish src\ForeverScout -c Release -p:ScoutVariant=lite -o dist\lite   # ramverksberoende, ~1,3 MB
```
- **full:** self-contained single-file för win-x64. Inget att installera.
- **lite:** framework-dependent single-file för win-x64. Kräver **.NET 10 Desktop Runtime**. Saknas den visar
  Windows (.NET:s apphost) standarddialogen "You must install or update .NET to run this application" med en
  nedladdningslänk. Det är kontrollerat med en .NET-installation utan Desktop Runtime. Ingen egen kod behövs.
- Båda: **ingen** `EnableCompressionInSingleFile`, UPX, packning, trimning eller obfuskering.
  `Directory.Build.props` ger varje variant egna `obj\<variant>` och `bin\<variant>`. Konstanten `SCOUT_LITE` styr
  `BuildInfo.Variant`, som visas under "Om Forever Scout".

```powershell
& "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" -Scan -ScanType 3 -File dist\lite\ForeverScout.exe
```

## Installationsprogram (Inno Setup 6)
Inno Setup 6.7 installeras per användare, utan admin, från den officiella releasen (jrsoftware.org →
github.com/jrsoftware/issrc, signerad av Pyrsys B.V.):
`innosetup-6.7.4.exe /VERYSILENT /CURRENTUSER /NOICONS`. Därefter:
```powershell
dotnet publish src\ForeverScout -c Release -p:ScoutVariant=lite -o dist\lite
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\ForeverScout.iss   # → dist\setup\ForeverScout-Setup-<version>.exe
```
Installationsprogrammet (`installer/ForeverScout.iss`):
- Installerar per användare utan admin (`PrivilegesRequired=lowest`) i `%LOCALAPPDATA%\Programs\Forever Scout\`,
  så att självuppdateringen fungerar utan UAC. AppId är fast, så nya versioner uppgraderar på plats och det blir en
  enda post under Appar.
- Genvägar: på skrivbordet (kryssruta, markerad som standard) och i Start-menyn. På sista sidan finns "Starta Forever
  Scout nu".
- Lägereld-utseende: mörk stil, `WizardBackColor #17151A` och bilder med logotyp och lägereld
  (`installer/wizard-*.png` från `make-icons.mjs`). Svenska eller engelska efter systemspråket.
- Innehåller **lite-exe:n**. Saknas .NET 10 Desktop Runtime (det kontrolleras i registret och under
  `Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\10.*`) får användaren först veta att Windows kommer att be
  om administratörsbehörighet. Sedan erbjuds det att hämta Microsofts officiella installationsprogram över HTTPS
  (`https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe` → builds.dotnet.microsoft.com) och köra det med
  UAC. Avböjer man visas länken.
- Uppgradering: `AppMutex=Local\ForeverScout.SingleInstance` och `CloseApplications=force` stänger en körande
  Forever Scout först.
- Avinstallation: tar bort exe, genvägar, självuppdateringens restfiler och autostartvärdet. Den frågar, med Nej som
  standard, om även `%APPDATA%\ForeverScout` ska tas bort.
- Testat tyst, utan fönster: installation av 1.0.2, uppgradering på plats till 1.0.3 (en post under Appar),
  avinstallation (allt borta, `%APPDATA%` kvar).
- Signeras senare, som exe:n.

## Ikoner
`node client/windows/tools/make-icons.mjs [--compare <gammal app.ico>]` (kör från guide-roten; sharp finns i
`node_modules`). Skriptet skapar:
- **`app.ico` (ikon v2), samma ikon för exe, installationsprogram, avinstallerare, genvägar och fönster.** Storlekarna är
  16/20/24/32/48/64/128/256.
  - Elden och vedträna lyfts ut med en färgnyckel. Ljust dis och grå stenar försvinner, mättade lågor och mörkröda
    vedträn blir kvar, och vita springor blir glöd. En mjuk äggformad mask tar bort yxan och muggen.
  - Allt läggs på en mörk rundad ruta med varm glöd underifrån och en tunn guldkant (#F0B844, 50 %).
  - 16–24 px är en förenklad, ritad lägereld i samma palett.
  - Originalet innehåller Alliansens vapen och ett WoW-"W" (Blizzards varumärken). De får **aldrig** hamna i ikonen.
  - `--compare` ritar `docs/review/design/scout-icon-v2.png`, som jämför gammal och ny ikon på ljust och mörkt
    skrivbord.
- `Resources/tray-*.png`: fältikonerna (32 px, samma målade eld), `Resources/logo-header.png` (hela logotypen,
  används bara inuti appen) och `installer/wizard-*.png`.
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
