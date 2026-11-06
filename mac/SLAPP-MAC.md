# Släppa Forever Scout för Mac

Gör det här på MacBooken i **Terminal**. Kopiera ett block i taget. Allt tar ungefär 15–30 minuter, och nästan all tid går till att vänta på Apples notarisering.

Det du behövde ställa in en gång finns redan: Xcode-verktygen, .NET 10, nyckelringen `forever-scout.keychain-db` med ditt Developer ID och notarieprofilen `forever-scout`.

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
