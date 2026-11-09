# Forever Scout 1.0.11 (Windows) – förberett, inte släppt

Grenen `scout-mem` (worktree `D:\webb\forever-scout-client`) ovanpå `scout`. Inget är signerat, publicerat eller
driftsatt. Osignerad testexe: `client\windows\dist\mem\ForeverScout-1.0.11.exe` (Release, full, gitignorerad).

## Ändringslogg för användarna

> **Forever Scout 1.0.11**
> - Använder mycket mindre minne, särskilt när den ligger i bakgrunden.
> - "Stäng" i frågan när du klickar på krysset stänger nu Forever Scout på riktigt. Tidigare kunde den ligga kvar och
>   efter en stund visa ett felmeddelande.
> - Ett oväntat fel stänger inte längre Forever Scout med en felruta. Det skrivs i loggen och appen fortsätter.
> - Fönstret öppnas som vanligt även om du minimerar och öppnar det snabbt efter varandra.

(Engelska, om det behövs: "Uses much less memory, especially in the background. 'Close' in the close prompt now
really closes Forever Scout. An unexpected error no longer closes the app with an error box – it is logged and the
app keeps running. The window opens normally even when you minimise and reopen it quickly.")

## Vad som ändrats (för granskning)
| Commit | Innehåll |
|---|---|
| `56a71a9` | Globalisering via Windows NLS i stället för ICU |
| `66939e9`, `ef01501` | MemoryTrim: GC + EmptyWorkingSet efter start/synk/versionskontroll, fönstret släpps i fältet |
| `d5b83ba` | Strömmad SHA-256: oförändrade filer läses inte in |
| `32ea182` | Defensivt: `Live` i stället för `_form`, ShowStats avstår utan rader |
| se `git log scout..scout-mem` | Grundorsaken (Stäng → Exit kördes aldrig), dialoger/fönsterlivscykel, rekursionen vid öppning av minimerat fönster, CrashGuard, stresstest |

Grundorsak: README → "Fönstrets livscykel och oväntade fel (1.0.11)" (X → Stäng köade `Exit` på fönstret som
stängdes; anropet kastades, appen levde kvar och nästa tick kraschade i `ShowStats`).

## Verifierat här (2026-11-09)
- `dotnet test tests`: 332 gröna (nya: `WindowLifecycleTests`, `CrashReportTests`).
- `tools\stress.ps1 -Scenario close-exit` / `close-exit-remembered`: ef01501 lever kvar och kastar
  `ArgumentOutOfRangeException` i `ShowStats ← RefreshStats ← OnTick` (exakt användarens krasch); 32ea182 lever kvar
  utan fönster (Stäng avslutar inte); 1.0.11 nu avslutar inom 3 s med kod 0.
- `tools\stress.ps1 -Minutes 12 -Seed 1011`: 720 s, 3 473 åtgärder, 203 fönster skapade och rivna, 0 undantag,
  1 levande fönster efter GC, exit 0. Minne per 2 min (arbetsmängd/privat): golvet 2–33 MB, topparna 120–156 MB när
  fönstret är öppet och 12 MB Auctionator parsas – ingen växt över tiden; handtag ~500, GDI/USER oförändrade.
  ef01501 och 32ea182 dör i samma test inom 40–60 s (stack overflow, minimerat fönster öppnas igen).
- `--screenshots`: alla tio vyer identiska med `scout` utom klockslaget i lägerjournalen.
- Release-exe:n saknar `--stress` (`StressTest` finns inte i filen).

## Släppa (huvudtråden eller användaren kör – i den här ordningen)
Allt nedan görs i guide-repot `D:\webb\wowlan\guide` (grenen `scout`), eftersom det är därifrån sajten driftsätts
och `ForeverScout-ops\oss\export.sh` exporterar. Nyckeln ligger i `C:\Users\Administrator\ForeverScout-signing\`
(lämnar aldrig servern, skrivs aldrig ut).

```powershell
# 0. Ta in 1.0.11 och tagga (för att kunna rulla tillbaka)
cd D:\webb\wowlan\guide
git status                                  # ska vara rent
git tag scout-pre-1.0.11                    # läget före
git merge --ff-only scout-mem               # scout-mem ligger ovanpå scout
cd client\windows
dotnet test tests                           # ska vara grönt

# 1. Bygg de tre filerna (full, lite, installationsprogram)
dotnet publish src\ForeverScout -c Release -p:ScoutVariant=full -o dist\full
dotnet publish src\ForeverScout -c Release -p:ScoutVariant=lite -o dist\lite
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\ForeverScout.iss   # → dist\setup\ForeverScout-Setup-1.0.11.exe
& "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" -Scan -ScanType 3 -File dist\full\ForeverScout.exe
& "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" -Scan -ScanType 3 -File dist\lite\ForeverScout.exe

# 2. Signera släppet (skriver public\scout\version.json + kopierar filerna dit; addon och Mac behålls som de är)
cd D:\webb\wowlan\guide
node scripts/scout-sign.mjs --key C:\Users\Administrator\ForeverScout-signing\scout-release-private.pem --version 1.0.11 `
  --exe client\windows\dist\full\ForeverScout.exe --exe-lite client\windows\dist\lite\ForeverScout.exe `
  --setup client\windows\dist\setup\ForeverScout-Setup-1.0.11.exe

# 3. Committa (version.json, lite-exe och setup är incheckade; full-exe:n är gitignorerad) och tagga
git add public/scout/version.json public/scout/ForeverScout-lite.exe public/scout/ForeverScout-Setup.exe
git commit -m "Klient 1.0.11: mindre minne, Stäng avslutar, fel loggas i stället för felruta"
git tag live-<nästa nummer>
```

4. **Driftsätt guiden som vanligt** (guidens `CLAUDE.md`: `git archive HEAD` → pscp → `docker compose up -d --build`,
   säkerhetskopiera `~/guide/db` först). Full-exe:n följer inte med tarballen:
   - säkerhetskopiera den gamla på .47: `cp ~/ForeverScout.exe ~/ForeverScout-1.0.10-backup.exe`
   - kopiera upp `public\scout\ForeverScout.exe` till `~/ForeverScout.exe` på .47 (pscp), och efter uppackningen som
     vanligt: `cp ~/ForeverScout.exe ~/ForeverScout-mac.zip ~/ForeverScout-mac.dmg public/scout/`
   - kontrollera: `https://guide.wow4ever.se/api/bidrag/version` visar `client`/`lite` 1.0.11, och nedladdningssidan
     visar 1.0.11.
5. **GitHub** (bara när du vill): `bash C:/Users/Administrator/ForeverScout-ops/oss/publish.sh "Forever Scout 1.0.11"`
   (exporterar från `D:\webb\wowlan\guide`, skannar med gitleaks och pushar bara om allt är rent).

**Rulla tillbaka:** koden: `git reset --hard scout-pre-1.0.11` i guide-repot (före steg 3) eller en revert-commit
(efter). Nedladdningen: en gammal `version.json` räcker inte – klienter som sett 1.0.11:s nyare `issued` avvisar den
(L10). Signera i stället om 1.0.10-filerna med nytt `issued`: hämta lite-exe och setup ur förra live-taggen
(`git show <tagg>:public/scout/ForeverScout-lite.exe > …`), full-exe:n från `~/ForeverScout-1.0.10-backup.exe`, och
kör `scout-sign.mjs --version 1.0.10 …` som i steg 2. Klienter som redan har 1.0.11 stannar där (ingen nedgradering).

**Klienter som får uppdateringen:** 1.0.10 hämtar den själv inom 3 timmar (tyst, aldrig mitt i en uppladdning, inget
fönster över spelet). 1.0.9 och äldre får den bara via "Sök efter uppdateringar" eller bubblan.

**Mac:** Core ändrades (strömmad SHA-256 i `SafeFs`, ny `CrashReport`, en ny text). Windows-släppet kräver inget av
Mac-appen (version.json behåller Mac-fälten), men innan en Mac-1.0.11 byggs måste Mac-appen byggas och testas på
Macen (`bash mac/release.sh`) – det går inte att göra här.

## Checklista för användarens test (före signering)
Kör `client\windows\dist\mem\ForeverScout-1.0.11.exe` (osignerad; SmartScreen kan fråga). Avsluta den gamla först
(fältmenyn → Avsluta) eller låt den nya ta över.

- [ ] Starten: bara ikonen i fältet, inget fönster. Dubbelklick på ikonen öppnar fönstret med dina siffror.
- [ ] Minimera → fönstret försvinner till fältet. Öppna igen → allt ser ut som förut. Gör det några gånger snabbt.
- [ ] X → frågan visas. **Minimera till fältet** → fönstret göms. X igen → **Stäng** → ikonen försvinner och
      processen är borta i Aktivitetshanteraren (inget felmeddelande efter 15–30 s).
- [ ] Starta igen. X med "Fråga inte igen" + Stäng → appen avslutas direkt. (Ändra tillbaka i Inställningar om du vill.)
- [ ] I spelet: `/reload` → uppladdningen syns i lägerjournalen, siffrorna i tabellen uppdateras.
- [ ] F5 och knappen **Ladda upp** fungerar; "Inget nytt att ladda upp" när inget ändrats.
- [ ] Kugghjulet → Sök efter uppdateringar → "Du har den senaste versionen" (eller erbjudande om nyare).
- [ ] Inställningar och Visa vad som skickas öppnas och stängs. Minimera huvudfönstret medan Inställningar är öppet
      (Win+M) → inget försvinner, återställ från aktivitetsfältet.
- [ ] Minne: Aktivitetshanteraren → Forever Scout, kolumnen Minne. Efter någon minut i fältet bör den ligga klart
      under 1.0.10 (förut ~60–85 MB). Öppna fönstret → stiger; stäng till fältet → sjunker igen inom någon sekund.
- [ ] Låt den ligga i fältet över natten / en speldag: ingen felruta, minnet växer inte.
- [ ] `%APPDATA%\ForeverScout\uploads.log`: inga rader med `error` eller `crash` (om det finns: skicka raden).
