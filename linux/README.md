# Forever Scout – Linux-klient

Samma app som macOS-klienten (`client/mac/README.md`): installerar addonen **ForeverScout**, bevakar
`ForeverScout.lua` och laddar upp den (plus `DBCache.bin`) när spelet har sparat, och visar vad bidraget ledde till.
Kontrakt: `docs/briefs/SCOUT-KONTRAKT.md`. Släpps som **en AppImage** (x64), så distributionen spelar ingen roll.

## Teknikval
- **Samma källkod som Mac-appen.** `ForeverScout.Linux.csproj` länkar in källfilerna i `client/mac/ForeverScout.Mac`
  (Avalonia 12.1.4, gränssnittet i C# utan XAML) och sätter konstanten `LINUX`. Egna filer här är bara `Program.cs`
  (start) och `KeychainStore.cs` (nyckelfilen). De få ställen där Linux skiljer sig står under `#if LINUX` i de delade
  filerna; Mac-bygget definierar inte konstanten och är oförändrat.
- **Samma ForeverScout.Core** (`client/windows/src/ForeverScout.Core`): SafeFs, Lua-parsern, uppladdningskön,
  signaturkontrollen, addoninstallationen och texterna. Linux-delarna väljs med `OperatingSystem.IsLinux()`.
- **AppImage, inte Flatpak.** Spelet ligger i ett Wine-prefix någonstans i hemmappen (Lutris, Steam, Bottles …). En
  Flatpak-sandlåda ser inte de mapparna utan att användaren öppnar dem en och en, och appen ska dessutom skriva
  addonen dit. En AppImage är en vanlig fil som körs som användaren, utan installation och utan root (Steam Deck har
  skrivskyddat system).
- **x64.** WoW körs genom Wine, som i praktiken bara finns för x64. `build.sh --arch arm64` bygger men släpps inte.

## Vad appen läser och skriver på Linux
All fil- och processåtkomst går genom `SafeFs.cs` (samma statiska vakt, `StaticGuardTests`, som nu också granskar
`client/linux/ForeverScout.Linux`). Reglerna i spelkatalogen är exakt de som gäller på Windows och macOS.

| Sökväg | Åtkomst |
|---|---|
| `<spelkatalog>/Interface/AddOns/ForeverScout/**` (+ `.new`/`.old` under bytet) | läsa/skriva |
| `<spelkatalog>/WTF/Account/*/SavedVariables/ForeverScout.lua` | läsa |
| `…/SavedVariables/Auctionator.lua`, `…/Auc-Stat-Simple.lua` | läsa, **bara** när "Dela auktionspriser" är på (av som standard) |
| `<spelkatalog>/Cache/ADB/*/DBCache.bin` | läsa |
| `Interface`, `WTF/Account/*`, `…/SavedVariables`, `Cache/ADB/*` | bara existens och listning |
| `~/.config/ForeverScout/**` (katalogen 0700; `$XDG_CONFIG_HOME` följs) | `settings.json`, `uploads.log`, `instance.lock`, `instance.sock`, `contributor-key` (0600) |
| `~/.config/autostart/se.wow4ever.foreverscout.desktop` | när "Starta vid inloggning" är på – från 1.0.12 förvalt på välkomstskärmen (går att kryssa ur), annars av tills du kryssar i den i Inställningar |

**Var spelet söks.** Spelet körs genom Wine, så det ligger i ett Wine-prefix:
`<prefix>/drive_c/Program Files (x86)/World of Warcraft/_forever_` (eller `Program Files`). Appen provar dessa prefix:

| Startare | Prefix |
|---|---|
| Lutris | `~/Games/battlenet`, `~/Games/battle.net`, `~/Games/blizzard-battlenet`, `~/Games/world-of-warcraft`, `~/Games/wow` |
| Steam/Proton | `<Steam>/steamapps/compatdata/*/pfx` för `~/.local/share/Steam`, `~/.steam/steam` och Flatpak-Steam |
| Bottles | `~/.var/app/com.usebottles.bottles/data/bottles/bottles/*`, `~/.local/share/bottles/bottles/*` |
| Faugus Launcher | `~/Faugus/*` |
| Wine | `~/.wine` |

Under Steam, Bottles och Faugus har varje prefix ett eget namn. Där listas **bara katalognamnen** i den kända
föräldern (högst 200, inga filer läses). En hittad katalog används först när användaren bekräftat den en gång
(`linux.gamedir.confirm`), precis som på macOS. Allt annat väljs i Inställningar.

**Nyckeln** ligger i `~/.config/ForeverScout/contributor-key` (0600 i en katalog med 0700). Linux har ingen
nyckelring som finns på alla skrivbord (Steam Deck i spelläge, fönsterhanterare utan Secret Service), och en nyckel
som inte går att läsa vid inloggning hade stoppat uppladdningen i tysthet.

## Skillnader mot macOS-appen
- **Fönstret visas vid start** (utom med `--tray`, som autostart använder). Alla skrivbord har inte fältikoner – GNOME
  kräver ett tillägg. Startas appen en gång till visar den körande instansen sitt fönster.
- **Ingen självuppdatering än.** Appen läser den signerade versionsinfon (linux-fälten, `signature5`) och säger till när
  en nyare version finns, med länk till nedladdningssidan. Addonen uppdateras automatiskt som på de andra plattformarna.
- **I en AppImage** körs binären från en tillfällig montering (`/tmp/.mount_…`). Autostart och omstart pekar därför på
  själva AppImage-filen (`$APPIMAGE`, `SafeFs.LinuxLaunchPath`).

## 1.0.12
Samma ändringar som macOS-appen (se "1.0.12" i `client/mac/README.md`): "Starta vid inloggning (rekommenderas)" förvald
på välkomstskärmen och en fråga en gång till befintliga användare som själva startar appen (aldrig med `--tray` eller
efter `--restarted`), ärlig status och statusrad, "Välj WoW-katalog" som huvudknapp utan spelkatalog (utan
mappsymbol – alla skrivbord har inte ett färgemoji-typsnitt), ingen dialog vid autostart (rad i lägerjournalen och
fältikonen med röd prick i stället), addonens fel ligger kvar efter nästa synk, och Enter väljer guldknappen.

## Bygga
```bash
client/linux/build.sh                 # tester + dotnet publish linux-x64 + dist/ForeverScout-linux-x64-<v>.tar.gz
```
Går att köra på Linux, macOS och Windows (Git Bash); kräver .NET SDK 10 och Python 3 (packar tar.gz med rätt
rättigheter även från NTFS). På Linux görs sedan AppImage-filen (kräver `squashfs-tools`):
```bash
curl -fsSLO https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-x86_64
client/linux/packaging/make-appimage.sh dist/ForeverScout-linux-x64-<v>.tar.gz runtime-x86_64 ForeverScout-x86_64.AppImage
```
En AppImage är AppImage-projektets startare följd av en squashfs-avbild med appen; skriptet sätter ihop dem och
skriver ut startarens SHA-256 (anteckna den i släppanteckningarna). Tar.gz:en fungerar också som den är: packa upp,
starta `ForeverScout/ForeverScout`, och `install.sh` lägger appen i programmenyn.

## Släppa
```bash
node scripts/scout-sign.mjs --key <privat.pem> --version <klientversionen i version.json> \
  --linux ForeverScout-x86_64.AppImage --linux-version <v>
```
Skriver `linux`, `linuxFile`, `linuxSha256`, `linuxSig` och `signature5` i `public/scout/version.json`; övriga delar
och signaturer behålls. AppImage-filen är gitignorerad (som exe:n och Mac-filerna) och kopieras separat vid deploy.
Nedladdningssidan visar Linux-kortet först när fälten och `signature5` finns och filens SHA-256 stämmer.

## Tester
`dotnet test client/mac/tests` – `LinuxTests.cs`: signature5 och linux-fälten (och att Windows och macOS inte
påverkas), autostart-filens citering, och på Linux (`[LinuxFact]`) nyckelfilens rättigheter och sökrötterna.
Provkört i en Debian-container med virtuell skärm: appen startar, ritar onboarding-fönstret och skapar
`~/.config/ForeverScout` med 0700. **Inte provkört** på ett riktigt skrivbord med WoW under Wine.

## Krav på användarens dator
x64, glibc-baserad distribution, X11 eller XWayland, fontconfig och ICU (finns på alla vanliga skrivbord). AppImage
behöver `fusermount` (fuse2 eller fuse3); utan det går filen att starta med `--appimage-extract-and-run`.
