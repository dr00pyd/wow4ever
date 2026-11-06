# Släppa Forever Scout för Mac

## Det enda du behöver skriva

Öppna **Terminal** på MacBooken och klistra in:

```bash
cd ~/wow4ever && git pull && bash mac/release.sh
```

Skriptet gör allt: låser upp nyckelringen, bygger, signerar, notariserar hos Apple, gör zip och DMG, kontrollerar
dem och skickar båda filerna till servern. Det tar 15–30 minuter, mest väntan på Apple. Låt Terminal vara.

Det frågar efter:
- **Serverns adress, bara första gången:** skriv serverns adress som `användare@ip`. Den sparas på Macen i `~/.config/forever-scout/release.conf`.
- **Nyckelringens lösenord.**
- **Serverns lösenord** när filerna skickas.
- **Första gången:** om Terminal får styra Finder. Svara **OK**; det ger DMG-fönstret vår bakgrund.

När det står **"Klart! Mac 1.0.9 ligger på servern"** skriver du det till Claude. Claude gör resten.

**Inte hemma?** Om skicket misslyckas skriver skriptet vad du gör. Kör sedan hemifrån utan att bygga om:

```bash
cd ~/wow4ever && bash mac/release.sh --bara-skicka
```

---

## Samma sak för hand, steg för steg

Behövs bara om skriptet krånglar.

## 1. Hämta den senaste koden

```bash
cd ~/wow4ever && git pull
```

Kontrollera vilken version som ska byggas. Raden ska visa `<Version>1.0.9</Version>`:

```bash
grep "<Version>" mac/ForeverScout.Mac/ForeverScout.Mac.csproj
```

## 2. Lås upp nyckelringen

Ange nyckelringens lösenord när Terminal frågar:

```bash
security unlock-keychain forever-scout.keychain-db
```

## 3. Bygg, signera, notarisera och packa

```bash
bash mac/build.sh --sign B776C4F6A6369A57A5F4A489C51A30A1765932C5 --notarize forever-scout --dmg
```

- Skriptet kör testerna, bygger appen för både Apple Silicon och Intel och signerar den.
- Det skickar appen till Apple och väntar, ofta 5–15 minuter. Låt Terminal vara.
- Sedan zippar det appen och gör en DMG.
- **Första gången** frågar macOS om Terminal får styra Finder. Svara **OK**. Det är det som ger DMG-fönstret vår bakgrund och ikon. Svarar du nej blir det en vanlig DMG, och den fungerar också.
- Om det frågar efter nyckelringens lösenord igen anger du samma lösenord.

Klart när det står:

```
Klart: …/mac/dist/Forever Scout.app
       …/mac/dist/ForeverScout-mac-1.0.9.zip
```

## 4. Kontrollera (valfritt men bra)

```bash
spctl -a -vv -t exec "mac/dist/Forever Scout.app"
xcrun stapler validate mac/dist/ForeverScout-1.0.9.dmg
```

Den första raden ska säga `source=Notarized Developer ID`. Den andra ska säga `The validate action worked!`.

Vill du se DMG:n öppnar du den:

```bash
open mac/dist/ForeverScout-1.0.9.dmg
```

## 5. Skicka filerna till servern

**Hemma, på samma nät som servern:**

```bash
scp mac/dist/ForeverScout-mac-1.0.9.zip mac/dist/ForeverScout-1.0.9.dmg <användare>@<server>:~/
```

Användare och adress till servern står i Claudes kopia av den här filen (guideinkorgSLAPP-MAC.md).

**Inte hemma:** kopiera de två filerna till Windows-servern, till `D:\webb\wowlan\guide\client\mac\`.

## 6. Säg till Claude

Skriv till exempel **"Mac 1.0.9 ligger på servern"**. Sedan gör Claude resten:

- kontrollerar filerna (hash, version, Apples notarisering)
- signerar med vår release-nyckel, som aldrig lämnar servern
- lägger ut zip-filen så att Mac-appen uppdaterar sig själv
- gör DMG:n till förstahandsvalet för Mac på nedladdningssidan

## Om något går fel

| Det står | Gör så här |
|---|---|
| `errSecInternalComponent` eller `no identity found` | Nyckelringen är låst. Kör steg 2 igen. |
| Notariseringen får `Invalid` | `xcrun notarytool log <id> --keychain-profile forever-scout`. Skicka utskriften till Claude. |
| `dotnet: command not found` | Starta en ny Terminal, eller installera .NET 10 SDK igen. |
| `git pull` klagar på lokala ändringar | `git stash && git pull` |
| `scp: Connection refused` eller timeout | Du är inte på hemmanätet. Använd alternativet "Inte hemma" i steg 5. |
