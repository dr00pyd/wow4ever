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

**Kontroll efter öppning (1.0.7).** Reglerna kontrolleras först på sökvägen. Sedan öppnas filen via ett handtag, och
handtagets verkliga sökväg (`GetFinalPathNameByHandle`, efter alla junctions och symlänkar) måste ligga på exakt samma
plats i det tillåtna området. Läsning sker ur samma handtag. Skrivning går till en temporärfil via handtag och döps om
via handtaget (`NtSetInformationFile`, bara nytt filnamn i samma katalog), och radering sker via handtaget. Byts en
katalog ut mot en junction mellan kontrollen och användningen nekas åtkomsten. Katalogoperationerna (skapa, radera,
flytta – bara addonens och självuppdateringens fasta namn) kontrolleras via handtag direkt före respektive efter.
`SafeFsTests` (L11_*) byter ut kataloger mot junctions just i det fönstret.

**Filer under den valda WoW-katalogen** (t.ex. `…\World of Warcraft\_forever_`):
| Sökväg | Åtkomst |
|---|---|
| `Interface\AddOns\ForeverScout\**` (+ `ForeverScout.new`/`.old` under bytet) | läsa/skriva (installera addonen) |
| `WTF\Account\*\SavedVariables\ForeverScout.lua` | läsa (det som laddas upp) |
| `WTF\Account\*\SavedVariables\Auctionator.lua`, `…\Auc-Stat-Simple.lua` | läsa, **bara** när "Dela auktionspriser" är på (av som standard). Filen skickas aldrig, bara föremål, realm/fraktion, dag, lägsta pris per styck och antal |
| `Cache\ADB\*\DBCache.bin` | läsa (hotfix-cachen) |
| `Cache\WDB\*\creaturecache.wdb` | läsa (varelsecachen, 1.0.14 – inga andra filer i `Cache\WDB`) |
| `Interface`, `Interface\AddOns`, `WTF`, `WTF\Account`, `WTF\Account\*`, `…\SavedVariables`, `Cache`, `Cache\ADB`, `Cache\ADB\*` | bara existens och kataloglistning, för att hitta filerna ovan |

Andra filer i WoW-katalogen nekas, till exempel andra addons SavedVariables (de två auktionsfilerna ovan när delningen är av, och Auctioneers `Auc-ScanData.lua` med säljarnamn alltid), `Config.wtf`, `config-cache.wtf` och `Wow.exe`.

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
- `POST /api/bidrag/enhet`, `POST /api/bidrag/enhet/status` (bara när du väljer "Logga in med webbläsaren", från 1.0.8:
  `{ platform: "windows", version }` och sedan den hemliga `deviceCode` var `interval`:e sekund – ingen nyckel, inget
  om datorn)
- `POST /api/bidrag/ladda-upp`, `POST /api/bidrag/hotfix`
- `POST /api/bidrag/auktioner` (bara när "Dela auktionspriser" är på)
- `GET /api/bidrag/jag`, `GET /api/bidrag/version`
- `GET /scout/<signerad fil>`

Rubrikerna är bara `User-Agent: ForeverScout/<version>`, `Authorization: Bearer <nyckel>`, `Content-Type` och
`Content-Encoding: gzip`. Inga användarnamn, datornamn, sökvägar eller kontokataloger skickas.

**Kroppen** är filens exakta byte, gzip-komprimerade. Innan något skickas parsas filen. Den måste innehålla **exakt en**
global, `ForeverScoutDB`, annars vägras den. Den måste också gå att parsa helt, annars väntar appen på att spelet
skriver klart. Fönstret **Visa vad som skickas** (kugghjulet och fältmenyn) visar exakt innehåll, sha256 och om filen
redan är skickad. Varje anrop loggas i `%APPDATA%\ForeverScout\uploads.log` med tid, typ, storlek, sha256 och svar.
Knappen **Öppna logg** finns i Inställningar.

**Auktionspriser (opt-in).** Kryssrutan *Dela auktionspriser (Auctionator/Auctioneer)* i Inställningar är av som standard. När den är på läser appen auktionsaddonens prisfil i samma SavedVariables-katalog. Själva filen skickas aldrig, bara JSON-rader med föremål, realm, fraktion, dag, lägsta utköpspris per styck i koppar och antal. Säljarnamn, karaktärer, inköpslistor, inställningar, säljhistorik och klockslag skickas inte. Bara dagar från förra uppladdningen och framåt skickas, högst 30 dagar bakåt. *Visa vad som skickas* visar raderna, och `uploads.log` loggar varje anrop (`auktioner`). Format och källor står i `docs/SCOUT.md`, avsnittet "Auktionspriser".

## Vad den gör
- WoW-katalogen hittas via registrets `InstallPath` (föräldern och `_forever_` testas) eller standardsökvägar.
  Annars väljer du själv (Inställningar → WoW-katalog). Kataloger som börjar eller slutar med `_` och innehåller
  `Interface` eller `WTF` räknas som kandidater, och `_forever_` sorteras först. **Från 1.0.7** används en automatiskt
  hittad katalog först när du bekräftat den (en gång, frågan visar sökvägen). Svarar du Nej föreslås den inte igen och
  du väljer själv. Inställningar från 1.0.6 och äldre (där katalogen kan ha hittats automatiskt) bekräftas också en gång.
- Addonen installeras i `<spel>\Interface\AddOns\ForeverScout`. Den bäddas in i exe:n vid bygget och tas från
  `client/addon/ForeverScout/**` som den ser ut då. Nyare addonversioner hämtas bara som **signerade** släpp (se
  "Signerade uppdateringar") och bara om de är **nyare** än den installerade (eller om ingen addon finns). Spärrlistan
  `blockedAddons` och serverns `422 addon-blocked` är inte signerade: från 1.0.7 stoppar de bara uppladdningen av den
  spärrade versionen och visar ett meddelande, och de leder aldrig till att en äldre addon installeras.
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
  - 1.0.14: `Cache\WDB\<locale>\creaturecache.wdb` (varelsecachen – NPC:ers namn, titel, typ och modell, inga
    personuppgifter) skickas gzip-komprimerad till `/api/bidrag/varelser`, bara när rutan "Skicka varelsecachen" är på
    (standard) och dess sha256 har ändrats. Servern sparar bara varelse → modell och namnet, inte filen.
- **Kontonamnet** (`WTF\Account\<namn>`) skickas aldrig och visas inte i UI, logg eller felmeddelanden.
- Tabellen räknas lokalt med en egen Lua-tabellparser (`LuaParser.cs`) som bara godtar literaler. Den kör aldrig kod.
- Ingen admin (`asInvoker`).

## Filer
| Sökväg | Innehåll |
|---|---|
| `src/ForeverScout.Core/` | `SafeFs` (enda fil-/registeråtkomsten), Lua-parser, statistik, katalogsökning, filbevakning (`SyncCoordinator`), API-klient, uppladdning/kö, logg och "visa vad som skickas", addoninstallation, signaturkontroll, självuppdatering. Byggs för `net10.0-windows` (den här appen) och `net10.0` (macOS-appen i `client/mac`, se dess README); Windows-vägen i koden är oförändrad |
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
- **Onboarding** (`FirstRunForm`): "Slå dig ner vid elden." **Logga in med webbläsaren** (guldknappen, 1.0.8) är den
  primära handlingen. Under den, som alternativ: välj ett visningsnamn, eller koppla med kod från sajten. Rutan till
  höger förklarar vilken data som delas och vad som gäller för integriteten. Om namnet är upptaget visas en knapp som
  hoppar till kodfältet.
- **Logga in med webbläsaren** (`BrowserLinkForm`, flödet i `ForeverScout.Core/DeviceLink.cs`, kontraktet 2b):
  appen hämtar en kod, öppnar `https://guide.wow4ever.se/bidra/koppla?kod=…` i webbläsaren och visar koden
  (`XXXX-XXXX`) så att du kan jämföra med sidan, plus "Väntar på godkännande i webbläsaren …", "Öppna webbläsaren
  igen" och Avbryt. Godkänner du på webben sparas nyckeln precis som efter koppling med kod (`ContributorStore.Save`:
  DPAPI-filen, id och namn i `settings.json`). Sidan öppnas bara om adressen börjar exakt med
  `https://guide.wow4ever.se/` (i Debug även loopback-basadressen); allt annat avvisas. Samma dialog nås från
  kugghjuls- och fältmenyn och från Inställningar (där byter den kopplingen på den här datorn).
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
  en statusrad, Logga in med webbläsaren …, Pausa automatisk uppladdning, Sök efter uppdateringar, Forever-guiden,
  Inställningar och Avsluta.
- Fältikonen har tre lägen: elden, elden med en amber ring som snurrar under uppladdning, och elden med en röd prick
  vid fel. Verktygstipset visar poäng och senaste status.
- All text finns på svenska och engelska (`ForeverScout.Core/Strings.cs`). Core-fel bär koder (`ErrorCodes`) som
  översätts där. Testerna kräver sv och en för varje nyckel och varje felkod.

## Minne (1.0.11)
Appen ligger i fältet i dagar, så det som syns i Aktivitetshanteraren är arbetsmängden i fältläget. 1.0.10 läste
varje fil (SavedVariables, `DBCache.bin`, auktionsfilerna) i sin helhet via en `MemoryStream` för att räkna ut
sha256 – även när filen var oförändrad – och GC:n lämnade sällan tillbaka minnet. Med en hotfix-cache på 8 MB och en
Auctionator-fil på 19 MB låg 1.0.10 på ~150 MB i fältet. Åtgärder:
- **Strömmad hash** (`SafeFs.Sha256Hex`, 64 KB i taget): en oförändrad fil känns igen utan att läsas in. Hela filen
  läses bara när den har ändrats, och då direkt in i en array av rätt storlek (`SafeFs.ReadAllBytes`).
- **`MemoryTrim`** (`Support.cs`): komprimerande GC + `SetProcessWorkingSetSize(-1,-1)` efter starten, efter en
  uppladdning och en versionskontroll när fönstret är dolt, och när fönstret stängs till fältet. Fönstret disposas då
  och byggs upp igen vid nästa öppning. I fältet lämnas oanvända sidor tillbaka högst en gång per minut.
- **NLS i stället för ICU** (`System.Globalization.UseNls`, csproj): samma sv/en-formatering, icu.dll laddas inte.
- Provat men inte med: `System.GC.ConserveMemory` (ingen mätbar vinst), ReadyToRun (+3 MB och +13 MB fil).

Mät med `Get-Process ForeverScout` (WorkingSet64) eller prestandaräknaren `\Process(ForeverScout)\Working Set -
Private` – den senare är kolumnen **Minne** i Aktivitetshanteraren. Golvet för en tom WinForms-app med samma
publiceringsinställningar är ~34 MB arbetsmängd i fältet utan trimning; efter trimning ligger Forever Scout under det.

## Fönstrets livscykel och oväntade fel (1.0.11)
- **Stäng avslutar.** X → Stäng (eller "Fråga inte igen" = Stäng) köade tidigare `Exit` på fönstret som just stängdes
  (`this.BeginInvoke`). WinForms kastar köade anrop när fönstret disposas, så `Exit` kördes aldrig: appen levde kvar
  utan fönster och nästa tick kraschade i `ShowStats` (tabellen töms av Dispose → `ArgumentOutOfRangeException`).
  Samma kod finns i 1.0.3–1.0.10, så felet är inte nytt i 1.0.11. Nu köas `Exit` via appens egen kontroll (`ScoutApp.OnUi`).
- `ScoutApp` når fönstret bara via `Live` (aldrig ett rivet fönster); `_form` nollas när fönstret disposas, hur det än
  sker. Dialoger får bara ett levande, synligt fönster som ägare, och alla modala dialoger går via `ScoutApp.Modal`:
  medan en dialog är öppen göms och rivs fönstret inte (det minimeras bara som vanligt).
- `HideToTray` körs inte igen inifrån sitt eget handtagsbyte, och `ShowFromTray` återställer ett minimerat fönster
  före `ShowInTaskbar` – annars oändlig rekursion (stack overflow) om fönstret öppnades medan det var minimerat.
- **CrashGuard:** ett oväntat undantag på UI-tråden (tick, händelse, async-fortsättning) visar inte .NET:s feldialog
  och avslutar inte appen. Det skrivs i `uploads.log` (`error`: typ, HResult och metodnamn – aldrig meddelandet, som
  kan innehålla sökvägar) och som en rad i lägerjournalen (samma fel högst en gång per minut). Fel på andra trådar
  loggas (`crash`) innan processen avslutas.

## Stresstest (bara Debug)
`--stress[=minuter]` kör en slumpad sekvens mot den riktiga appen: öppna, minimera, stäng till fältet (med och utan
frågan), öppna igen direkt, F5/Ladda upp, Sök efter uppdatering, Inställningar, Visa vad som skickas, fältmenyn,
storleksändring – med korta tick-intervall. Dialoger stängs automatiskt (aldrig Avsluta, inloggning eller länkar).
Flaggan finns inte i Release-byggen. Skriptet bygger en isolerad datakatalog, en falsk WoW-mapp och en död API-adress,
ändrar filerna under körningen och mäter minnet var 10:e sekund:
```powershell
dotnet build src\ForeverScout -c Debug
powershell -ExecutionPolicy Bypass -File tools\stress.ps1 -Minutes 10           # memory.csv, summary.txt, data\stress.log
powershell -ExecutionPolicy Bypass -File tools\stress.ps1 -Scenario close-exit  # X → Stäng ska avsluta appen
powershell -ExecutionPolicy Bypass -File tools\stress.ps1 -Screenshots          # samma miljö, --screenshots
```

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
     stämma (ingen reserv). **Från 1.0.7 krävs den**: en versionsinfo med bara `signature` godtas inte. Sedan väljer
     de fil efter variant: full uppdateras från `clientFile`, lite från `liteFile`. Installationsprogrammet (`setup`)
     används bara på nedladdningssidan.
   - `issued` (signerad tidsstämpel) får inte gå bakåt (från 1.0.7): klienten sparar det högsta värdet den godtagit i
     `settings.json` (`ReleaseIssued`) och avvisar äldre versionsinfo, så att en gammal men korrekt signerad fil inte
     kan spelas upp igen. scout-sign.mjs sätter alltid ett nytt `issued`.
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
5. **Automatisk uppdatering (från 1.0.10, även Mac):** versionsinfon hämtas vid start och sedan var 3:e timme. En nyare
   signerad klient installeras utan fråga, men aldrig mitt i en uppladdning (då väntar den en minut). Efter omstarten
   visas bara bubblan "Uppdaterad till …". Fönstret öppnas bara om det var öppet eller om användaren själv valde
   "Sök efter uppdateringar", så att inget hoppar upp över spelet (`Settings.QuietUpdate`). Efter 3 misslyckade
   försök i rad (`UpdateFailures`) slutar appen försöka själv och erbjuder i stället uppdateringen (bubbla och meny).
   Klienter ≤ 1.0.9 frågar bara vid start och måste uppdateras en sista gång själva.

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
  UAC. Avböjer man visas länken. **Från 1.0.7** startas den hämtade filen bara om `Get-AuthenticodeSignature` ger
  `Valid` och både certifikatets ägare och utfärdare har `O=Microsoft Corporation` (i dag "CN=.NET" utfärdat av
  "Microsoft Code Signing PCA 2024"). Filen hålls öppen med skrivskydd från kontrollen tills installationen är klar, så
  att den inte kan bytas ut däremellan. Annars visas ett meddelande och Microsofts nedladdningssida öppnas i stället.
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
