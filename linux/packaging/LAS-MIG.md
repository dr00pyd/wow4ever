# Forever Scout för Linux

Laddar upp det addonen **ForeverScout** har samlat in till Forever-guiden (guide.wow4ever.se).
*English below.*

## Kom igång
1. Packa upp mappen `ForeverScout` var du vill i din hemmapp.
2. Starta `ForeverScout` i mappen (dubbelklicka, eller `./ForeverScout` i en terminal).
3. Vill du ha appen i programmenyn: kör `./install.sh` (valfritt, tas bort med `./install.sh --remove`).

Första gången väljer du namn (eller loggar in med webbläsaren) och bekräftar spelkatalogen. Spelet körs genom Wine,
så katalogen ligger i ett Wine-prefix, till exempel
`~/Games/battlenet/drive_c/Program Files (x86)/World of Warcraft/_forever_` (Lutris). Hittar appen den inte själv
väljer du den i Inställningar.

Starta om WoW efter att addonen installerats. Data laddas upp när spelet har sparat (/reload, utloggning, avslut).

## Bra att veta
- Inget installeras i systemet och inget kräver root. Avinstallera genom att ta bort mappen och `~/.config/ForeverScout`.
- Appen ligger i systemfältet. Visar ditt skrivbord inga fältikoner (GNOME utan tillägg)? Starta appen en gång till,
  så kommer fönstret fram.
- Appen uppdaterar inte sig själv på Linux än. Den säger till när en ny version finns; addonen uppdateras automatiskt.
- Appen läser bara addonens egna filer i spelkatalogen. Exakt vilka står på guide.wow4ever.se/bidra/integritet.

---

## English
1. Unpack the `ForeverScout` folder anywhere in your home folder.
2. Start `ForeverScout` in the folder (double-click, or `./ForeverScout` in a terminal).
3. Optional: run `./install.sh` to add the app to your application menu (`./install.sh --remove` undoes it).

On first start you choose a name (or sign in with your browser) and confirm the game folder. The game runs through
Wine, so the folder is inside a Wine prefix, for example
`~/Games/battlenet/drive_c/Program Files (x86)/World of Warcraft/_forever_` (Lutris). If the app cannot find it,
choose it under Settings.

Restart WoW after the addon is installed. Data is uploaded when the game has saved (/reload, logout, exit).

- Nothing is installed system-wide and nothing needs root. Uninstall by deleting the folder and `~/.config/ForeverScout`.
- The app lives in the system tray. If your desktop shows no tray icons (GNOME without an extension), start the app
  once more and the window comes back.
- The app does not update itself on Linux yet. It tells you when a new version exists; the addon updates automatically.
- The app only reads the addon's own files in the game folder. The exact list is at guide.wow4ever.se/en/contribute/privacy.
