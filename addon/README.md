# ForeverScout (addon)

ForeverScout samlar in data om WoW: Forever-världen medan du spelar: var NPC:er står, vad tränare lär ut, vad försäljare säljer, quests, malmådror och örter, loot och flygpunkter. Datan laddas upp till [guide.wow4ever.se](https://guide.wow4ever.se/bidra) och visas i Forever-guiden. När minst två spelare har rapporterat samma sak räknas den som bekräftad.

- Spel: WoW: Forever (klient 1.60.x, `## Interface: 16001`)
- Version: 1.0.5
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
| NPC:er | creature-id, namn, undertitel (t.ex. "Mage Trainer"), nivå, reaktion mot dig (vänlig/neutral/fientlig), din fraktion, roller (tränare, försäljare, quest, gossip, flyg, värdshusvärd, reparation) och upp till 50 positioner. Från 1.0.5 även max-HP och max-mana per nivå (`hp`/`mp`) |
| Tränare | typ (class, profession, pet, weapon, riding) och tjänster: spell-id, namn, rank, kostnad, nivåkrav och yrkeskrav. Alla tjänster läses, även de som ditt filter döljer, och sedan återställs filtret |
| Försäljare | föremål-id, pris, antal per köp, begränsat lager och alternativ valuta. Från 1.0.5: valutan som en sorterad sträng (`"20558:3,c392:10"` = 3 st föremål 20558 och 10 currency 392), alla varor läses även om ditt filter döljer några (filtret återställs), och `rr` = försäljarens reaktion mot dig (6+ = honored eller bättre, priset kan ha ryktesrabatt) |
| Quests | quest-id, titel, nivå, givare och avslutare (NPC- eller objekt-id), position vid givaren, mål och belöningar (föremål, valbara föremål, pengar, XP). Från 1.0.5 även din nivå (`pl`) bredvid XP och pengar, eftersom de beror på den |
| Objekt | gameobject-id, namn (t.ex. "Copper Vein") och upp till 50 positioner. Från 1.0.5 även läsbara objekt (skyltar, böcker – bara platsen, aldrig texten) och objekt som interaktionstangenten pekar på (soft interact, utanför strid) |
| Loot | källa (`src`) med id, föremål med antal, och pengar. Högst 2 000 poster, och de äldsta tas bort först. Källorna:<br>`npc`, `object` och `item` är vanlig loot från en varelse, ett objekt eller en behållare.<br>`skin`, `mine` och `herb` är flåning, gruvdrift och örtplockning av en *varelse* (id = varelsens id). Malmådror och örter är objekt och förblir `object`.<br>`fish` är fiske (id = kartans uiMapID, plus position `m`/`x`/`y`).<br>Från 1.0.5: `pick` (fickstöld, id = varelsen), `dis` (förtrollningsupplösning, id = föremålet) och `boss` (loot vars källa spelet döljer i en instans, kopplad till ett vunnet encounter, id = encounter-id).<br>Varje (källa, typ) räknas en gång per session, så vanlig loot, flåning och fickstöld av samma kropp blir olika poster utan att den ena blockerar den andra.<br>Från 1.0.5 har varje post också `h` (en kort tagg för kroppen, se integritet), `grp` (du var i grupp), `pf` (din fraktion) och föremålens slumpsuffix (`s`). Loot sparas inte när latensen är över 300 ms |
| Flygpunkter | nod-id, namn och position (den flygpunkt du står vid). Från 1.0.5 även priset till varje nåbar flygpunkt (`c`) och flygmästarens reaktion mot dig (`rr`) |
| Kills (från 1.0.5) | varelser du (eller din grupp) taggat och som dog medan de var ditt mål: creature-id, kroppens tagg `h`, dagen, om liket hade loot (`loot`) och `grp`. Ger nämnaren för dropchansen. Högst 3 000, äldsta först bort |
| Mobs (från 1.0.4) | NPC:er du bara tittar på (mål eller muspekare, utanför strid): creature-id, namn, nivåintervall, reaktion mot dig, din fraktion, klassificering (normal, elit, sällsynt, sällsynt elit, världsboss) och varelsetyp (som tal). **Ungefärligt område** = *din* position när du såg den, aldrig mobens. Ett nytt område sparas bara om det ligger mer än 5 kart-% från ett sparat område på samma karta, högst 8 per mob, och tiden avrundas till dagen. Högst 3 000 mobs. Från 1.0.5 även **exakta platser** (`e`) när du står inom ~10 yards (interaktionsavstånd) från en mob som ingen annan taggat, utanför strid (högst 8 per mob, dagen utan klockslag), och max-HP/mana per nivå |
| Brevlådor (från 1.0.4) | position när du öppnar en brevlåda (och objektets id om spelet ger det). Högst 300, en per brevlåda |
| Roller (från 1.0.4) | auktionsförrättare, stallmästare och andeläkare läggs till som roller på NPC:n när du öppnar deras fönster. Från 1.0.5 även värdshusvärd (när du binder hemsten), stridsmästare, talangåterställning och vapenrocksförsäljare |
| Huvud | formatversion, addonversion, spelets build, spelets språk, ett slumpat installations-id och `seq` (en räknare som ökar för varje ny post, så att klienten vet vad som redan är uppladdat) |

**Valfritt (`/scout char on`):** klass, ras, nivå, yrken med färdighetsnivå och inlärda recept (spell-id). Det är avstängt som standard, och du väljer per karaktär.

## Vad som INTE sparas (integritet)

- Inga karaktärsnamn, ingen realm, inget kontonamn, ingen guild och inga namn på andra spelare.
- Inga fullständiga GUID:er. Bara creature-, objekt- eller föremåls-id:t (fält 6) sparas. Addonen försöker inte ta reda på server, layer eller shard.
- Från 1.0.5 får loot och kills en **tagg** (`h`, 8 hex-tecken): en 32-bitars hash av kroppens GUID med ett salt per version. Den används bara för att servern ska räkna samma lik en gång när flera i en grupp laddar upp det. En GUID innehåller långt mer än 32 bitar (server, instans, zon, spawn-id), så otaliga GUID:er ger samma tagg – den går inte att räkna tillbaka till GUID:en, och GUID:en sparas aldrig. Föremålets unika id i länken läses aldrig, bara slumpsuffixet.
- Ingen chatt, ingen combat log, inga positioner i instanser och ingen spårning av var du rör dig. En exakt position sparas bara när du själv öppnar ett fönster, står vid ett objekt eller (från 1.0.5) står precis bredvid en mob. Systemmeddelanden (`CHAT_MSG_SYSTEM`) jämförs från 1.0.5 bara med spelets egna berusningstexter ("You feel drunk" …), så att inga nivåer sparas medan du är berusad – inget ur meddelandena sparas. Bossnamnet från `ENCOUNTER_END` jämförs bara, sparas aldrig. Mobs får bara ett ungefärligt område per 5 kart-% (din position när du tittade på dem, dagen utan klockslag) – inte en rutt.
- Mål och muspekare: bara varelser som ingen spelare styr. Spelare, husdjur, väktare, totem och sällskapsdjur hoppas alltid över (deras namn kan spelare välja), liksom allt där spelet inte svarar säkert (secret values). Inget om mobs läses i strid; kill-räkningen (1.0.5) läser bara om ditt mål har dött och om taggen är din.
- Installations-id:t är ett slumptal, skapat en gång. Det är inte kopplat till ditt Battle.net-konto.
- Addonen läser inga andra addons data och skickar ingenting själv. Det enda den gör är att skriva sin egen SavedVariables-fil.

Kontonamnet syns i sökvägen till filen (`WTF\Account\<KONTO>\`). Klienten skickar aldrig sökvägen, bara filens innehåll.

## Teknik och begränsningar

- **API:** Forever bygger på Mainline-klienten (spelvarianten "Camelot") och har Midnights regler för *secret values*. Varje värde från spelet kontrolleras med `issecretvalue()` innan det jämförs eller sparas, och varje API-anrop sker i `pcall` med funktionsdetektering.
- **Prestanda:** addonen använder ingen `OnUpdate`. Arbete görs bara när du interagerar med något, och omläsningar av försäljare och tränare väntar en kort stund (C_Timer) så att de inte körs för ofta. Samma position sparas inte igen inom en timme (0,5 kart-% tolerans), och listor som inte har ändrats ger ingen ny `seq`.
- **Tränartjänsternas spell-id** läses via `C_TooltipInfo.GetTrainerService`. Om det saknas används `GetTrainerServiceItemLink`.
- **Flåning och annan insamling** (från 1.0.3) känns igen på en lyckad Skinning/Mining/Herbalism-besvärjelse (alla id:n i Forevers spelfiler, och namnet som reserv) högst 3 sekunder före lootfönstret. Den gäller bara det lootfönstret. Fiske känns igen med `IsFishingLoot()`.
- **Questmål utan föremålsnamn** (t.ex. "0/8 ", eftersom namnet inte hunnit laddas) läses om vid `QUEST_LOG_UPDATE` och `ITEM_DATA_LOAD_RESULT`, högst en gång per sekund och högst 20 gånger. När namnet kommer uppdateras posten och får ny `seq`, så att den laddas upp igen. En läsbar text ersätts aldrig av en ofullständig.
- **Objektnamn** tas från målet för den senaste besvärjelse du kastade inom 15 sekunder före lootfönstret, men bara för Opening-, Mining- och Herb Gathering-besvärjelser (från 1.0.4, säkerhetsgranskningen L13) och aldrig om namnet är en spelares. Namnet används en gång och nollställs vid nästa besvärjelse. Saknas ett sådant namn sparas bara id:t.
- **Tjänste-NPC:er och brevlådor** (från 1.0.4): `MAIL_SHOW`, `AUCTION_HOUSE_SHOW`, `PET_STABLE_SHOW` och Mainlines `PLAYER_INTERACTION_MANAGER_FRAME_SHOW` (typerna slås upp med namn i `Enum.PlayerInteractionType`: `Auctioneer`, `StableMaster`, `SpiritHealer`, `MailInfo`). Händelser som klienten saknar registreras inte och gör ingenting.
- **Quests:** `minLvl` finns inte i Mainline-API:t och lämnas tomt. Nivån läses ur questloggen när du accepterar questen.
- **Vapen- och ridtränare** känns bara igen på den engelska undertiteln. Annars blir de `class`.
- **1.0.5:** fickstöld och förtrollningsupplösning känns igen som insamlingen (Pick Pocket 921 och Disenchant 13262 – de enda id:n med de namnen som spelare kastar i Forevers SpellName – och namnet som reserv). Kills läses vid `PLAYER_TARGET_CHANGED`, målets `UNIT_HEALTH` (bara för `target` där klienten har `RegisterUnitEvent`) och `PLAYER_REGEN_ENABLED`; `CanLootUnit` frågas en gång en sekund efter döden (C_Timer, ingen loop). Loot vars källa är en secret value kopplas till det döda målet om dess GUID är läsbar, annars till det senast vunna encountret (inom 5 minuter, en gång). Nya händelser som klienten saknar registreras inte och gör ingenting: `ENCOUNTER_END`, `CHAT_MSG_SYSTEM`, `CONFIRM_BINDER`, `BATTLEFIELDS_SHOW`, `CONFIRM_TALENT_WIPE`, `OPEN_TABARD_FRAME`, `ITEM_TEXT_BEGIN`, `PLAYER_SOFT_INTERACT_CHANGED`.
