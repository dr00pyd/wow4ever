# ForeverScout (addon)

ForeverScout samlar in data om WoW: Forever-världen medan du spelar: var NPC:er står, vad tränare lär ut, vad försäljare säljer, quests, malmådror och örter, loot och flygpunkter. Datan laddas upp till [guide.wow4ever.se](https://guide.wow4ever.se/bidra) och visas i Forever-guiden. När minst två spelare har rapporterat samma sak räknas den som bekräftad.

- Spel: WoW: Forever (klient 1.60.x, `## Interface: 16001`)
- Version: 1.0.2
- Filformat: `docs/briefs/SCOUT-KONTRAKT.md` §1 (`ForeverScoutDB`, `v = 1`)

## Installation

**Med ForeverScout-klienten (rekommenderas):** klienten installerar och uppdaterar addonen själv och laddar upp datan så fort spelet sparat filen (vid /reload, karaktärsbyte, utloggning och avslut).

**Manuellt:**
1. Kopiera mappen `ForeverScout` till `World of Warcraft\_forever_\Interface\AddOns\`, så att du får `...\AddOns\ForeverScout\ForeverScout.toc`.
2. Starta spelet och kontrollera att **ForeverScout** är ikryssad i addonlistan på karaktärsvalsskärmen.
3. Spela som vanligt. Datan sparas när du loggar ut eller skriver `/reload`, i
   `World of Warcraft\_forever_\WTF\Account\<KONTO>\SavedVariables\ForeverScout.lua`.
4. Ladda upp filen med klienten eller dra och släpp den på guide.wow4ever.se/bidra.

Mappen innehåller två TOC-filer. Forever läser `ForeverScout_Forever.toc` och `ForeverScout.toc` är reserv. Båda är likadana.

## Kommandon

| Kommando | Vad det gör |
|---|---|
| `/scout` | Visar status: antal poster per typ och den senaste posten |
| `/scout help` | Visar hjälpen |
| `/scout char on` / `off` | Slår på eller av karaktärsdata för **den här** karaktären (av som standard). `off` tar bort den ur filen |
| `/scout lang sv` / `en` / `auto` | Väljer språk för addonens meddelanden. `auto` följer spelets språk, och eftersom det inte finns någon svensk WoW-klient blir det engelska |
| `/scout reset` | Tömmer insamlad data. Du måste bekräfta med `/scout reset confirm` |
| `/scout sync` | Sparar nu (laddar om gränssnittet med /reload) så att klienten laddar upp direkt |
| `/scout debug` | Visar antal fel som har fångats under sessionen (för felrapporter) |

Addonen skriver inget i chatten om du inte själv skriver ett kommando. Fel fångas och visas aldrig som felrutor.

## Vad som sparas

Positioner sparas som kart-id (UiMapID) plus x/y i procent och tas när ett fönster öppnas, alltså när du står vid NPC:n eller objektet. Ingen position sparas i instanser, under flygtur eller i fordon.

| Typ | Innehåll |
|---|---|
| NPC:er | creature-id, namn, undertitel (t.ex. "Mage Trainer"), nivå, reaktion mot dig (vänlig/neutral/fientlig), din fraktion, roller (tränare, försäljare, quest, gossip, flyg, värdshusvärd, reparation) och upp till 50 positioner |
| Tränare | typ (class, profession, pet, weapon, riding) och tjänster: spell-id, namn, rank, kostnad, nivåkrav och yrkeskrav. Alla tjänster läses, även de som ditt filter döljer, och sedan återställs filtret |
| Försäljare | föremål-id, pris, antal per köp, begränsat lager och alternativ valuta (föremål eller currency + antal) |
| Quests | quest-id, titel, nivå, givare och avslutare (NPC- eller objekt-id), position vid givaren, mål och belöningar (föremål, valbara föremål, pengar, XP) |
| Objekt | gameobject-id, namn (t.ex. "Copper Vein") och upp till 50 positioner |
| Loot | källa (NPC, objekt eller föremål) med id, föremål med antal, och pengar. Varje källa räknas en gång per session. Fiske sparas inte. Högst 2 000 poster, och de äldsta tas bort först |
| Flygpunkter | nod-id, namn och position (den flygpunkt du står vid) |
| Huvud | formatversion, addonversion, spelets build, spelets språk, ett slumpat installations-id och `seq` (en räknare som ökar för varje ny post, så att klienten vet vad som redan är uppladdat) |

**Valfritt (`/scout char on`):** klass, ras, nivå, yrken med färdighetsnivå och inlärda recept (spell-id). Det är avstängt som standard, och du väljer per karaktär.

## Vad som INTE sparas (integritet)

- Inga karaktärsnamn, ingen realm, inget kontonamn, ingen guild och inga namn på andra spelare.
- Inga fullständiga GUID:er. Bara creature-, objekt- eller föremåls-id:t (fält 6) sparas. Addonen försöker inte ta reda på server, layer eller shard.
- Ingen chatt, ingen combat log, inga positioner i instanser och ingen spårning av var du rör dig. En position sparas bara när du själv öppnar ett fönster.
- Installations-id:t är ett slumptal, skapat en gång. Det är inte kopplat till ditt Battle.net-konto.
- Addonen läser inga andra addons data och skickar ingenting själv. Det enda den gör är att skriva sin egen SavedVariables-fil.

Kontonamnet syns i sökvägen till filen (`WTF\Account\<KONTO>\`). Klienten skickar aldrig sökvägen, bara filens innehåll.

## Teknik och begränsningar

- **API:** Forever bygger på Mainline-klienten (spelvarianten "Camelot") och har Midnights regler för *secret values*. Varje värde från spelet kontrolleras med `issecretvalue()` innan det jämförs eller sparas, och varje API-anrop sker i `pcall` med funktionsdetektering.
- **Prestanda:** addonen använder ingen `OnUpdate`. Arbete görs bara när du interagerar med något, och omläsningar av försäljare och tränare väntar en kort stund (C_Timer) så att de inte körs för ofta. Samma position sparas inte igen inom en timme (0,5 kart-% tolerans), och listor som inte har ändrats ger ingen ny `seq`.
- **Tränartjänsternas spell-id** läses via `C_TooltipInfo.GetTrainerService`. Om det saknas används `GetTrainerServiceItemLink`.
- **Objektnamn** tas från målet för den senaste besvärjelse du kastade (t.ex. Mining på "Copper Vein") inom 15 sekunder före lootfönstret. Saknas ett sådant namn sparas bara id:t.
- **Quests:** `minLvl` finns inte i Mainline-API:t och lämnas tomt. Nivån läses ur questloggen när du accepterar questen.
- **Vapen- och ridtränare** känns bara igen på den engelska undertiteln. Annars blir de `class`.
