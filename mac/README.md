# Forever Scout – macOS-klient

Menyradsappen för macOS. Den gör samma sak som Windows-klienten (`client/windows/README.md`): installerar addonen
**ForeverScout**, bevakar `ForeverScout.lua` och laddar upp den (plus `DBCache.bin`) när spelet har sparat, och visar
vad bidraget ledde till. Kontrakt: `docs/briefs/SCOUT-KONTRAKT.md`.

## Teknikval: Avalonia 12.1.4 + samma ForeverScout.Core
- **Avalonia UI 12.1.4** (senaste stabila som var minst två veckor gammal 2026-11-05; 12.1.5 kom 2026-11-02).
  Menyradsikon via `TrayIcon` (NSStatusItem, mallbild), inget Dock-ikon (`LSUIElement` + `ShowInDock = false`).
  Gränssnittet är skrivet i C# utan XAML.
- **ForeverScout.Core delas med Windows**. Den ligger kvar i `client/windows/src/ForeverScout.Core` och bygger nu för
  `net10.0-windows` (Windows-appen, oförändrad) **och** `net10.0` (Mac-appen). All logik och säkerhet – SafeFs,
  Lua-parsern, uppladdningskön, signaturkontrollen, addoninstallationen och texterna (sv/en) – finns alltså bara på ett
  ställe. Alternativet, SwiftUI, hade krävt en andra implementation av allt detta.
- Avalonias byggstatistik (`Avalonia.BuildServices`) är bortkopplad i csproj (`ExcludeAssets="all"`), och `build.sh`
  sätter `AVALONIA_TELEMETRY_OPTOUT=1`.

## Filer
| Sökväg | Innehåll |
|---|---|
| `ForeverScout.Mac/Program.cs` | Start, en instans (`UnixInstance`), nyckeln ur nyckelringen |
| `ForeverScout.Mac/App.cs` | Avalonia-appen (mörkt Fluent-tema), onboarding vid första starten |
| `ForeverScout.Mac/ScoutController.cs` | Menyraden, filbevakning, uppladdning, uppdateringar – motsvarar `ScoutApp.cs` på Windows |
| `ForeverScout.Mac/MainWindow.cs` | Dashboard: tre kort, tabellen, Mitt bidrag, lägerjournalen, statusraden, stäng-frågan |
| `ForeverScout.Mac/OnboardingWindow.cs` | "Slå dig ner vid elden." – logga in med webbläsaren, namn eller kod |
| `ForeverScout.Mac/BrowserLinkWindow.cs` | Logga in med webbläsaren: koden, väntan på godkännande, Avbryt (Core: `DeviceLink`) |
| `ForeverScout.Mac/SettingsWindow.cs`, `PayloadWindow.cs`, `Dialogs.cs` | Inställningar, "Visa vad som skickas", dialoger |
| `ForeverScout.Mac/Look.cs` | Lägereld-tokens (samma värden som `Theme` i Windows-klientens `Support.cs`) |
| `ForeverScout.Mac/KeychainStore.cs` | Nyckeln i nyckelringen (Security.framework via P/Invoke) |
| `ForeverScout.Mac/Assets/` | Menyradsikoner (mall + färg), logotyp, appikon 256 – från `make-icons.mjs` |
| `assets/AppIcon.icns`, `assets/AppIcon.iconset/` | Appikonen (macOS rutnät 824/1024) – från `make-icons.mjs` |
| `packaging/Info.plist` | Mall (`LSUIElement`, `se.wow4ever.foreverscout`, macOS 14+) |
| `packaging/entitlements.plist` | Hardened runtime: bara `com.apple.security.cs.allow-jit` |
| `packaging/launcher.c` | Universell startare (arm64 + x86_64) som väljer rätt arkitekturs app |
| `build.sh` | Bygger, signerar, notariserar, staplar, zippar och (sist) signerar med vår nyckel |
| `tests/` | xUnit: Mac-reglerna, signature3, en instans, plus Windows-klientens plattformsoberoende Core-tester |

## Vad appen läser och skriver på macOS
All fil-, register- och processåtkomst går fortfarande genom `SafeFs.cs` (samma statiska vakt, `StaticGuardTests`, som
nu också granskar `client/mac/ForeverScout.Mac`). På macOS gäller:

| Sökväg | Åtkomst |
|---|---|
| `<spelkatalog>/Interface/AddOns/ForeverScout/**` (+ `.new`/`.old` under bytet) | läsa/skriva |
| `<spelkatalog>/WTF/Account/*/SavedVariables/ForeverScout.lua` | läsa |
| `<spelkatalog>/WTF/Account/*/SavedVariables/Auctionator.lua`, `…/Auc-Stat-Simple.lua` | läsa, **bara** när "Dela auktionspriser" är på (av som standard). Filen skickas aldrig, bara föremål, realm/fraktion, dag, lägsta pris och antal (se Windows-README och `docs/SCOUT.md`) |
| `<spelkatalog>/Cache/ADB/*/DBCache.bin` | läsa |
| `Interface`, `WTF/Account/*`, `…/SavedVariables`, `Cache/ADB/*` | bara existens och listning |
| `~/Library/Application Support/ForeverScout/**` (katalogen 0700) | `settings.json`, `uploads.log`, `instance.lock`, `instance.sock` |
| `~/Library/LaunchAgents/se.wow4ever.foreverscout.plist` | bara när du kryssar i "Starta vid inloggning" (av som standard) |
| det egna `Forever Scout.app` (som helhet) och `.ForeverScout-update/` bredvid det | bara under självuppdateringen |

- Spelkatalogen hittas i `/Applications/World of Warcraft/_forever_` eller `~/Applications/World of Warcraft/_forever_`
  (samma struktur som på Windows: `_retail_`, `_classic_` … och `_forever_` under `World of Warcraft`, med `Interface`
  och `WTF` direkt i den). Annars väljer du själv i Inställningar. Källor: CurseForge och Mobalytics guider för
  Forever-addons (`World of Warcraft/_forever_/Interface/AddOns`); betan låg i en egen katalog
  (`_classic_beta_`/`_forever_beta_` enligt olika källor) – sådana rankas efter `_forever_` men hittas också.
  **Från 1.0.7** används en automatiskt hittad katalog först när du bekräftat den en gång (texten `mac.gamedir.confirm`).
  Inställningar från 1.0.6 bekräftas också en gång.
- Sökvägar normaliseras som rena strängar (`/` som avgränsare). `..`, relativa sökvägar, `//…`, bakstreck, jokertecken,
  NUL och resursgrenar (`fil/..namedfork/rsrc`) nekas. **Symlänkar** under de tillåtna områdena nekas (`LinkTarget`
  och `ReparsePoint`). Själva valet av spelkatalog följs en gång (t.ex. `/var` → `/private/var`).
- **Efter öppning (1.0.7):** symlänkskontrollen görs om när filen är öppnad (läsning, tillägg, låset) och efter
  namnbytet vid skrivning. Windows jämför handtagets verkliga sökväg (`GetFinalPathNameByHandle`); motsvarigheten på
  macOS, `fcntl(F_GETPATH)`, används inte: `fcntl` är variadisk, och på Apple arm64 hamnar variadiska argument på
  stacken, så ett vanligt P/Invoke-anrop skickar bufferten fel och kan skriva i godtyckligt minne. Det kräver en
  signatur per arkitektur eller en liten C-shim och måste provas på en Mac innan det slås på.
- **Skiftläge:** reglerna jämförs skiftlägesokänsligt, precis som på Windows (APFS är oftast skiftlägesokänsligt). På en
  skiftlägeskänslig volym betyder det bara att `foreverscout.lua` också räknas som addonens fil.
- **Nyckeln** ligger i login-nyckelringen (tjänst `se.wow4ever.foreverscout`, konto `contributor-key`). Den skrivs med
  Security.framework direkt, inte med kommandot `security`, så den syns aldrig i en processlista, och posten litar bara
  på appens egen kodsignatur. Koppla från raderar den.
- **En instans:** ett flock-lås på `instance.lock` (släpps automatiskt om processen dör) och en Unix domain socket,
  `instance.sock`, i samma 0700-katalog. Startar du appen igen tar den körande fram sitt fönster; en nyare version tar
  över (samma regel som pipen på Windows, `Takeover.Decide`). Inga nätverksportar.
- **Verktyg som körs:** bara `/usr/bin/ditto` (packa upp uppdateringen) och `/usr/bin/codesign` (kontrollera den),
  med fasta sökvägar och utan skal. Länkar öppnas med `open` (bara https mot guide.wow4ever.se).
- **Nätverk:** samma som Windows – bara `https://guide.wow4ever.se`, samma anrop och rubriker (inloggningen med
  webbläsaren skickar `platform: "mac"`).

## Gränssnittet
- **Menyraden:** lägereldens siluett som mallbild (följer ljust/mörkt läge). Under uppladdning en färgad eld med
  amberring som snurrar, vid fel en färgad eld med röd prick. Menyn: Öppna Forever Scout, Ladda upp nu, statusrad,
  Logga in med webbläsaren …, Pausa automatisk uppladdning (bock), Visa vad som skickas, Sök efter uppdateringar,
  Forever-guiden, Inställningar …, Om Forever Scout, Avsluta.
- **Första starten** (`OnboardingWindow`): **Logga in med webbläsaren** (guldknappen, 1.0.9) först; välj visningsnamn
  och koppla med kod finns kvar som alternativ under den.
- **Logga in med webbläsaren** (`BrowserLinkWindow`, samma Core-flöde som Windows, `DeviceLink`, kontraktet 2b): appen
  öppnar `https://guide.wow4ever.se/bidra/koppla?kod=…` med `open`, visar koden (`XXXX-XXXX`), "Väntar på godkännande
  i webbläsaren …" och Avbryt. Godkänt = nyckeln sparas i nyckelringen precis som efter koppling med kod
  (`ContributorStore.Save`). Sidan öppnas bara om adressen börjar exakt med `https://guide.wow4ever.se/` (i Debug även
  loopback-basadressen). Nås också från kugghjulsmenyn, menyraden och Inställningar (där byter den kopplingen).
  Fönstrets höjd följer innehållet (`Look.StyleWindow(..., height: null)`).
- **Fönstret** har samma innehåll som på Windows (kort, tabell, Mitt bidrag, lägerjournal, statusrad, F5 laddar upp)
  och kugghjulsmenyn med länkarna.
- **Stäng** (röda knappen eller ⌘W) ställer samma fråga: Stäng, Göm i menyraden eller Avbryt, plus "Fråga inte igen".
  Svaret sparas och kan ändras i Inställningar. Avsluta i menyn frågar inte. Första gången fönstret göms visas en gång
  att appen finns kvar i menyraden.
- **Texter:** samma `Strings.cs` (sv/en). Där macOS behöver annan formulering finns nyckeln `mac.<nyckel>`
  (t.ex. "Starta vid inloggning (i menyraden)", "Göm i menyraden", sökvägar med `/Applications`), som Mac-appen tar först.
- **Aviseringar:** macOS-notiser används inte (de kräver ett separat Objective-C-gränssnitt). Det som på Windows blir en
  ballong syns i stället i lägerjournalen och statusraden.

## Uppdateringar
`public/scout/version.json` får fyra nya fält och en tredje signatur. Windows påverkas inte:
- `mac`, `macFile` (`ForeverScout-mac.zip`), `macSha256`, `macSig` – zip:en med det **färdiga** paketet: kodsignerat
  (Developer ID, hardened runtime), notariserat och staplat. Filsignaturen gäller
  `ForeverScout-file/v1\nmac\n<version>\n<sha256>`.
- `signature3` gäller den kanoniska JSON:en av **alla** fält (signature2:s fält plus mac-fälten).
- `signature` och `signature2` beräknas exakt som förut över exakt samma fält. Windows-klienterna läser aldrig
  `signature3` eller mac-fälten. Bevis i `tests/MacReleaseTests.cs`: den ordagranna 1.0.6- och 1.0.2-koden
  (`tests/Compat/`) godtar en version.json med mac-fält och får samma exe, och ändrade mac-fält stoppar bara macOS.
- macOS-klienten hämtar addonen och spärrlistan precis som Windows (signature2), men själva appen bara med signature3
  (`ReleaseVerifier.VerifyMac`, ingen reserv).
- Från 1.0.7 gäller samma regler som på Windows: signature2 krävs, `issued` får inte gå bakåt (`ReleaseIssued` i
  settings.json) och en spärrad addon leder aldrig till att en äldre addon installeras.

Självuppdateringen (`MacBundleUpdater` i Core), när du väljer "Sök efter uppdateringar":
1. Versionsinfon verifieras med signature3; zip:en laddas ner och kontrolleras mot `macSha256` och `macSig` (vår
   ECDSA P-256-nyckel, samma som för Windows).
2. Zip:en packas upp med `ditto` i `.ForeverScout-update/new/` bredvid appen. Är den körande appen Developer ID-signerad
   måste den nya ha giltig kodsignatur med **samma Team ID och paket-id**
   (`codesign --verify --deep --strict -R='identifier "se.wow4ever.foreverscout" and anchor apple generic and certificate leaf[subject.OU] = "<TEAMID>"'`).
3. "Version x är nedladdad … Starta om nu?" – Ja: det körande paketet döps om till `.ForeverScout-update/old.app` och
   det nya flyttas in på samma plats (omförsök och återställning som på Windows). Den nya startas med `--restarted`,
   den gamla avslutar sig när den nya lever, och nästa start städar `.ForeverScout-update`.
4. Fel loggas med steg (`download`, `stage`, `swap-*`, `restart`) i `uploads.log` och lägerjournalen; efter två
   misslyckanden erbjuds nedladdningssidan.

Begränsningar: appen måste ligga där ditt konto får skriva (`/Applications` för en administratör, annars
`~/Applications`). Körs den direkt ur Hämtade filer (Gatekeepers "App Translocation") säger den att du ska flytta den
till Program först.

## Bygga och testa (fungerar även på Windows)
```bash
dotnet test client/mac/tests                                    # Mac-tester + Core (net10.0). Mac-specifika [MacFact] hoppas över utanför macOS
dotnet build client/mac/ForeverScout.Mac
dotnet publish client/mac/ForeverScout.Mac -c Release -r osx-arm64 -o client/mac/dist/publish-osx-arm64   # korskompilering
node client/windows/tools/make-icons.mjs --mac-only             # ikonerna (rör inte Windows-filerna)
```
På Windows testas: Mac-reglerna som rena funktioner, signature3 mot ordagrann 1.0.6/1.0.2-kod, en-instans-låset och
socketen (Windows 10+ har AF_UNIX) och Windows-klientens plattformsoberoende tester. På en Mac körs dessutom de riktiga
filsystemstesterna: symlänkar, 0700, skiftläge, `ditto` och paketbytet.

## Steg för steg på Macen (signering, notarisering, släpp)
Du behöver: en Mac med macOS 14 eller senare, ditt Apple Developer-konto (Account Holder eller Admin), och den privata
släppnyckeln `scout-release-private.pem` (samma som för Windows-släppen – den finns aldrig i git).

1. **Verktyg**
   ```bash
   xcode-select --install                 # Xcode Command Line Tools: codesign, notarytool, stapler, clang, iconutil
   # .NET SDK 10: https://dotnet.microsoft.com/download/dotnet/10.0 (macOS-installeraren, Arm64 eller x64)
   # Node.js 20+ (för scripts/scout-sign.mjs): https://nodejs.org eller `brew install node`
   dotnet --version && node --version && xcrun notarytool --version
   ```
2. **Developer ID Application-certifikat** (signerar appar som distribueras utanför App Store)
   - Enklast: Xcode → Settings → Accounts → ditt Apple-ID → *Manage Certificates…* → **+** → *Developer ID Application*.
   - Eller: developer.apple.com → Certificates, IDs & Profiles → Certificates → **+** → *Developer ID Application*,
     med en CSR från Nyckelhanterare (Certifikatassistent → Begär ett certifikat från en certifikatutfärdare). Dubbelklicka
     den nedladdade `.cer` så hamnar den i login-nyckelringen.
   - Kontrollera: `security find-identity -v -p codesigning` → raden
     `"Developer ID Application: <Namn> (<TEAMID>)"`. Den texten är `--sign` nedan.
3. **Inloggning för notarytool** (sparas i nyckelringen som en profil – välj ett av sätten)
   - *Appspecifikt lösenord:* account.apple.com → Inloggning och säkerhet → Appspecifika lösenord → skapa. Sedan:
     ```bash
     xcrun notarytool store-credentials forever-scout --apple-id "<ditt Apple-ID>" --team-id <TEAMID> --password "<appspecifikt lösenord>"
     ```
   - *API-nyckel:* App Store Connect → Användare och åtkomst → Integrationer → Team Keys → skapa (rollen Developer),
     ladda ner `AuthKey_<KEYID>.p8`. Sedan:
     ```bash
     xcrun notarytool store-credentials forever-scout --key ~/secure/AuthKey_<KEYID>.p8 --key-id <KEYID> --issuer <ISSUER-UUID>
     ```
4. **Hämta koden och den senaste version.json** (viktigt: scout-sign.mjs behåller Windows-fälten ur den befintliga filen)
   ```bash
   git clone <repo> && cd guide && git checkout scout && git pull
   ```
5. **Provbygge utan signering** (körs bara på din egen Mac):
   ```bash
   bash client/mac/build.sh --arch arm64
   open "client/mac/dist/Forever Scout.app"
   ```
   Kontrollera: lägerelden i menyraden (ljust och mörkt läge), menyn, onboarding (namn och kod), att
   `/Applications/World of Warcraft/_forever_` hittas, att addonen installeras, att en `/reload` i spelet ger en
   uppladdning, "Visa vad som skickas", stäng-frågan, Inställningar → "Starta vid inloggning" (skapar
   `~/Library/LaunchAgents/se.wow4ever.foreverscout.plist`), och att en andra start bara visar fönstret.
6. **Släpp** (universell app, signerad, notariserad, staplad, zippad, sedan vår signatur):
   ```bash
   bash client/mac/build.sh \
     --sign "Developer ID Application: <Namn> (<TEAMID>)" \
     --notarize forever-scout \
     --dmg \
     --release-key ~/secure/scout-release-private.pem
   ```
   Skriptet kör testerna, `dotnet publish -r osx-arm64` och `osx-x64`, bygger paketet (Info.plist, ikon, startaren),
   signerar varje fil inifrån och ut med `--options runtime --timestamp --entitlements packaging/entitlements.plist`,
   skickar till `xcrun notarytool submit --keychain-profile forever-scout --wait`, kör `xcrun stapler staple`, zippar
   med `ditto -c -k --keepParent` och kör **sist** `node scripts/scout-sign.mjs --key … --version <version> --mac <zip>`.
   scout-sign vägrar en zip utan `_CodeSignature` eller utan staplad biljett (`Contents/CodeResources`).
7. **Kontrollera**
   ```bash
   spctl -a -vv -t exec "client/mac/dist/Forever Scout.app"     # → source=Notarized Developer ID
   codesign -dv --verbose=2 "client/mac/dist/Forever Scout.app"  # → TeamIdentifier=<TEAMID>, Runtime Version
   git diff public/scout/version.json                             # mac-fälten + signature3; signature/signature2 nya men samma fältset
   ```
8. **Publicera** (som för Windows, se `CLAUDE.md` → Drift): checka in `public/scout/version.json`, kopiera
   `public/scout/ForeverScout-mac.zip` (ignoreras av git, ~100 MB) till serverns `~/guide/public/scout/`, säkerhetskopiera
   `~/guide/db` och driftsätt. Nedladdningssidans Mac-kort visar nedladdningsknappen först när `macFile` och `signature3` finns
   och filens SHA-256 stämmer – annars "Kommer snart". `.dmg`-filen (valfri) kan läggas upp separat för den som föredrar den.

## Felsökning
- **Appen kraschar direkt efter signering med hardened runtime:** kör `log stream --predicate 'process == "ForeverScout"'`.
  Gäller det JIT/minne, lägg till `com.apple.security.cs.allow-unsigned-executable-memory` i `packaging/entitlements.plist`
  (behövs bara om W^X stängs av). Vägrar den ladda en dylib, kontrollera att alla filer i `Contents/MacOS` är signerade
  (`codesign --verify --deep --strict`), och först i sista hand `com.apple.security.cs.disable-library-validation`.
- **Notariseringen avvisas:** `xcrun notarytool log <submission-id> --keychain-profile forever-scout`.
- **"Forever Scout kan inte öppnas":** paketet är inte notariserat eller biljetten inte staplad – `spctl -a -vv`.
- **Nyckelringen frågar om lov vid varje start:** händer bara med osignerade/ad hoc-byggen (signaturen ändras per bygge).
- **macOS säger "Forever Scout lade till objekt som kan köras i bakgrunden":** det är LaunchAgent-filen för "Starta vid
  inloggning". Den syns under Systeminställningar → Allmänt → Inloggningsobjekt.
- **.NET-stöd:** .NET 10 stöds officiellt på macOS 15 och senare (Apple Silicon och Intel); `LSMinimumSystemVersion` är
  14.0 eftersom körtiden fungerar där, men utan Microsofts stöd.

## Ändringar i delad kod (för macOS)
- `ForeverScout.Core.csproj`: `TargetFrameworks` `net10.0-windows;net10.0`, `InternalsVisibleTo ForeverScout.Mac.Tests`.
- `SafeFs.cs`: Unix-vägar bakom `SafeFs.Unix` (= `!OperatingSystem.IsWindows()`); Windows kör samma kod som förut.
  Nytt: `NormalizeUnix`, `RealPathUnix`, `OwnBundle`/`BundleOf`, `BundleUpdateDir`, `LaunchAgentPath`/`LaunchAgentPlist`,
  `TryLockFile`, `MacExtractZip`, `MacCodesignTeam`, `MacVerifyCodeSignature`. Registret läses/skrivs bara på Windows.
- `ReleaseVerifier.cs`: `ReleaseKind.Mac`, `SignedFields3`, `VerifyMac` (signature3), `SignedRelease.Mac/MacFile/HasSignature3`.
  `Verify` beter sig exakt som förut.
- `ApiClient.cs`: `MacVersionAsync` (ny metod). `UnixInstance.cs`, `MacBundleUpdater.cs`: nya, bara macOS.
- 1.0.8/1.0.9: `ApiClient.DeviceStartAsync`/`DeviceStatusAsync`, `ApiException.RetryAfter`, `DeviceLink.cs`
  (`DeviceLink`, `ContributorStore`) och `link.*`-texterna – delade med Windows.
- `Errors.cs`/`Strings.cs`: tre nya felkoder och `mac.*`-texter (tillägg). `Takeover.cs`, `AppBehavior.cs`:
  plattformsattribut, `ReadRequest` internal.
- `scripts/scout-sign.mjs`: `--mac`, `--mac-version`, `--allow-unnotarized`, `signature3`.
- `client/windows/tools/make-icons.mjs`: macOS-ikoner, `--mac-only`.
- `client/windows/tests/SafeFsTests.cs`: den statiska vakten granskar också Mac-appen; `Socket` bara i `UnixInstance.cs`
  och bara `AddressFamily.Unix`; SafeFs kör bara `/usr/bin/ditto` och `/usr/bin/codesign`.
- Webben: `lib/scout/release.ts` (`signed3`, `mac`, `macFile`), ett Mac-kort på nedladdningssidan och tre texter i
  `lib/i18n/{sv,en}.ts`.
