using System.Globalization;

namespace ForeverScout.Core;

/// <summary>
/// All text som användaren kan se, på svenska och engelska (systemspråk eller val i Inställningar).
/// Core-fel bär koder (<see cref="ErrorCodes"/>) som översätts här via nycklarna "err.&lt;kod&gt;" och "srv.&lt;kod&gt;".
/// Testet <c>StringsTests</c> kräver att varje nyckel har både sv och en med samma platshållare.
/// </summary>
public static class Strings
{
    public static bool IsSv { get; private set; } = true;

    public static void Apply(string setting)
    {
        IsSv = setting switch
        {
            "sv" => true,
            "en" => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "sv",
        };
    }

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(IsSv ? "sv-SE" : "en-GB");

    private static readonly Dictionary<string, (string Sv, string En)> T = new()
    {
        ["app"] = ("Forever Scout", "Forever Scout"),
        ["upload"] = ("Ladda upp", "Upload"),
        ["uploading"] = ("Laddar upp …", "Uploading …"),
        ["general"] = ("Allmän data", "General data"),
        ["general.sub"] = ("Bidra till Forever-guidens data", "Contribute to the Forever guide's data"),
        ["col.type"] = ("Typ", "Type"),
        ["col.count"] = ("Antal", "Count"),
        ["col.size"] = ("Storlek", "Size"),
        ["col.uploaded"] = ("Uppladdat", "Uploaded"),
        ["col.desc"] = ("Beskrivning", "Description"),
        ["kind.Npcs"] = ("NPC:er", "NPCs"),
        ["kind.Objects"] = ("Föremål i världen", "World objects"),
        ["kind.Quests"] = ("Quests", "Quests"),
        ["kind.Trainers"] = ("Tränare", "Trainers"),
        ["kind.Vendors"] = ("Försäljare", "Vendors"),
        ["kind.Loot"] = ("Loot", "Loot"),
        ["kind.Hotfix"] = ("Hotfix-filer", "Hotfix files"),
        ["desc.Npcs"] = ("Var NPC:er står, nivå och vad de gör", "Where NPCs stand, their level and role"),
        ["desc.Objects"] = ("Malmådror, örter, kistor och annat i världen", "Ore veins, herbs, chests and more"),
        ["desc.Quests"] = ("Questgivare, mål och belöningar", "Quest givers, objectives and rewards"),
        ["desc.Trainers"] = ("Vad tränare lär ut och vad det kostar", "What trainers teach and what it costs"),
        ["desc.Vendors"] = ("Varor och priser hos försäljare", "Vendor items and prices"),
        ["desc.Loot"] = ("Vad som föll från fiender och föremål", "What dropped from enemies and objects"),
        ["desc.Hotfix"] = ("Spelets hotfix-cache (DBCache.bin)", "The game's hotfix cache (DBCache.bin)"),
        ["mine"] = ("Mitt bidrag", "My contribution"),
        ["points"] = ("Poäng", "Points"),
        ["rank"] = ("Plats", "Rank"),
        ["confirmed"] = ("Din uppladdning bekräftade", "Your upload confirmed"),
        ["confirmed.none"] = ("Inget bekräftat än – data bekräftas när två spelare rapporterat samma sak.", "Nothing confirmed yet – data is confirmed once two players report the same thing."),
        ["as"] = ("Bidrar som {0}", "Contributing as {0}"),
        ["game.idle"] = ("Laddar upp när spelet sparat (/reload, utloggning, avslut)", "Uploads when the game saves (/reload, logout, exit)"),
        ["game.none"] = ("Ingen WoW-katalog vald", "No WoW folder selected"),
        ["addon.ok"] = ("Addon {0} installerad", "Addon {0} installed"),
        ["addon.installed"] = ("Addonen installerades ({0}). Starta om spelet om det redan körs.", "The addon was installed ({0}). Restart the game if it is running."),
        ["addon.missing"] = ("Addonen saknas", "Addon missing"),
        ["addon.blocked"] = ("Addonversionen är spärrad – den senaste signerade versionen installerades ({0}). Din data laddas upp efter nästa spelsession.", "This addon version is blocked – the latest signed version was installed ({0}). Your data will be uploaded after your next play session."),
        ["addon.blocked.wait"] = ("Addonversion {0} är spärrad av guiden och det finns ingen nyare signerad version än. Uppladdningen väntar tills en uppdatering finns – Scout installerar aldrig en äldre addon.", "Addon version {0} is blocked by the guide and there is no newer signed version yet. Uploading waits until an update is available – Scout never installs an older addon."),
        // Spelkatalogen hittades automatiskt (L11): bekräftas en gång innan något läses eller laddas upp.
        ["gamedir.confirm.title"] = ("Bekräfta WoW-katalogen", "Confirm the WoW folder"),
        ["gamedir.confirm"] = ("Forever Scout hittade den här spelkatalogen:\n\n{0}\n\nÄr det din WoW Forever-katalog? Scout installerar addonen där och laddar upp addonens data därifrån. Den ligger oftast i …\\World of Warcraft\\_forever_.\n\nVälj Nej om du inte känner igen den – då får du välja katalogen själv.", "Forever Scout found this game folder:\n\n{0}\n\nIs this your WoW Forever folder? Scout installs the addon there and uploads the addon's data from it. It is usually …\\World of Warcraft\\_forever_.\n\nChoose No if you do not recognise it – you can then pick the folder yourself."),
        ["last"] = ("Senast uppladdat {0}", "Last uploaded {0}"),
        ["never"] = ("Inget uppladdat än", "Nothing uploaded yet"),
        ["retry"] = ("Offline – nytt försök {0}", "Offline – retrying {0}"),
        ["done"] = ("Uppladdat: {0} fil(er), {1} hotfix", "Uploaded: {0} file(s), {1} hotfix"),
        ["nothing"] = ("Inget nytt att ladda upp", "Nothing new to upload"),
        ["paused"] = ("Automatisk uppladdning pausad", "Automatic upload paused"),
        ["error"] = ("Fel: {0}", "Error: {0}"),
        ["toast.title"] = ("Ny data bekräftad!", "New data confirmed!"),
        ["toast.body"] = ("Din uppladdning bekräftade {0}", "Your upload confirmed {0}"),
        ["toast.more"] = ("{0} och {1} till", "{0} and {1} more"),
        ["tray.hint"] = ("Forever Scout körs i bakgrunden", "Forever Scout is running in the background"),
        ["tray.hint.body"] = ("Dubbelklicka på ikonen i fältet för att öppna fönstret igen.", "Double-click the icon in the notification area to open the window again."),
        ["tray.tooltip.points"] = ("{0} poäng", "{0} points"),
        // Fältmenyn
        ["menu.open"] = ("Öppna Forever Scout", "Open Forever Scout"),
        ["menu.upload"] = ("Ladda upp nu", "Upload now"),
        ["menu.login"] = ("Logga in – kommer snart", "Sign in – coming soon"),
        ["menu.pause"] = ("Pausa automatisk uppladdning", "Pause automatic upload"),
        ["menu.exit"] = ("Avsluta", "Exit"),
        ["menu.payload"] = ("Visa vad som skickas", "Show what is sent"),
        ["menu.about"] = ("Om Forever Scout", "About Forever Scout"),
        ["about.title"] = ("Om Forever Scout", "About Forever Scout"),
        ["about.text"] = ("Forever Scout {0} ({1})\n\nCommunityklient till Forever-guiden. Pratar bara med {2}.\nKällkoden är öppen.", "Forever Scout {0} ({1})\n\nCommunity client for the Forever guide. Talks only to {2}.\nThe source code is open."),
        ["about.variant.lite"] = ("liten version – kräver .NET 10 Desktop Runtime", "small version – requires .NET 10 Desktop Runtime"),
        ["about.variant.full"] = ("fristående version", "standalone version"),
        ["takeover.old"] = ("En äldre Forever Scout körs fortfarande – avsluta den via ikonen vid klockan (Avsluta) och starta igen.", "An older Forever Scout is still running – exit it from the icon by the clock (Exit) and start again."),
        ["payload.title"] = ("Det här skickas till Forever-guiden", "This is sent to the Forever guide"),
        ["set.openlog"] = ("Öppna logg", "Open log"),
        ["menu.settings"] = ("Inställningar …", "Settings …"),
        ["menu.update"] = ("Sök efter uppdateringar", "Check for updates"),
        ["menu.guide"] = ("Forever-guiden", "Forever guide"),
        ["menu.faq"] = ("Vanliga frågor", "FAQ"),
        ["menu.terms"] = ("Villkor", "Terms"),
        ["menu.privacy"] = ("Integritetspolicy", "Privacy policy"),
        ["menu.report"] = ("Rapportera fel", "Report a problem"),
        // Stäng-dialogen
        ["close.title"] = ("Stäng Forever Scout", "Close Forever Scout"),
        ["close.question"] = ("Vill du verkligen stänga Forever Scout?", "Do you really want to close Forever Scout?"),
        ["close.body"] = ("Data laddas inte upp automatiskt när appen är stängd.", "Data is not uploaded automatically while the app is closed."),
        ["close.exit"] = ("Stäng", "Close"),
        ["close.tray"] = ("Minimera till fältet", "Minimize to tray"),
        ["close.dontask"] = ("Fråga inte igen", "Don't ask again"),
        // Uppdateringar
        ["update.none"] = ("Du har senaste versionen ({0}).", "You have the latest version ({0})."),
        ["update.ready"] = ("Version {0} är nedladdad, signaturen är kontrollerad. Starta om nu?", "Version {0} is downloaded and its signature verified. Restart now?"),
        ["update.addon"] = ("Addonen uppdaterades till {0}.", "The addon was updated to {0}."),
        ["update.fail"] = ("Uppdateringen misslyckades: {0}", "Update failed: {0}"),
        ["update.inuse"] = ("Kunde inte ersätta ForeverScout.exe (filen används av ett annat program, t.ex. antivirus). Försök igen om en stund.", "Could not replace ForeverScout.exe (the file is in use by another program, e.g. antivirus). Please try again in a moment."),
        ["update.download"] = ("Den nya versionen kunde inte hämtas: {0}", "The new version could not be downloaded: {0}"),
        ["update.restart"] = ("Den nya versionen är på plats men kunde inte startas. Starta Forever Scout igen.", "The new version is in place but could not be started. Please start Forever Scout again."),
        ["updated.to"] = ("Uppdaterad till {0} ✓", "Updated to {0} ✓"),
        ["journal.updated"] = ("{0}: uppdaterad från {1} till {2}", "{0}: updated from {1} to {2}"),
        ["journal.updated.to"] = ("{0}: uppdaterad till {1}", "{0}: updated to {1}"),
        ["update.notstarted"] = ("Den nya versionen startade inte. Forever Scout fortsätter med den gamla – försök igen, eller hämta den nya versionen från nedladdningssidan.", "The new version did not start. Forever Scout keeps running the old one – try again, or get the new version from the download page."),
        ["update.openpage"] = ("Självuppdateringen har misslyckats flera gånger. Vill du öppna nedladdningssidan och hämta den nya versionen där?", "Self-update has failed several times. Do you want to open the download page and get the new version there?"),
        ["journal.updatefail"] = ("{0}: uppdateringen misslyckades ({1})", "{0}: update failed ({1})"),
        // Första start
        ["first.title"] = ("Välkommen till Forever Scout", "Welcome to Forever Scout"),
        ["first.intro"] = ("Scout samlar in NPC:er, quests, tränare och loot medan du spelar och skickar dem till Forever-guiden när du avslutar spelet. Inget konto- eller karaktärsnamn skickas.", "Scout collects NPCs, quests, trainers and loot while you play and sends them to the Forever guide when you quit the game. No account or character names are sent."),
        ["first.name"] = ("Välj ett visningsnamn (syns på topplistan)", "Choose a display name (shown on the leaderboard)"),
        ["first.register"] = ("Börja bidra", "Start contributing"),
        ["first.or"] = ("… eller koppla till ditt bidrag på sajten", "… or link to your contribution on the website"),
        ["first.code"] = ("Kod från guide.wow4ever.se/bidra", "Code from guide.wow4ever.se/en/contribute"),
        ["first.link"] = ("Koppla med kod", "Link with code"),
        ["first.login"] = ("Logga in – kommer snart", "Sign in – coming soon"),
        ["first.badname"] = ("Namnet ska vara 2–24 tecken.", "The name must be 2–24 characters."),
        ["first.badcode"] = ("Ange koden (8 tecken).", "Enter the code (8 characters)."),
        // Inställningar
        ["set.title"] = ("Inställningar", "Settings"),
        ["set.gamedir"] = ("WoW-katalog (Forever, t.ex. …\\World of Warcraft\\_forever_)", "WoW folder (Forever, e.g. …\\World of Warcraft\\_forever_)"),
        ["set.browse"] = ("Bläddra …", "Browse …"),
        ["set.find"] = ("Sök igen", "Search again"),
        ["set.lang"] = ("Språk", "Language"),
        ["set.lang.auto"] = ("Systemets språk", "System language"),
        ["set.autostart"] = ("Starta med Windows (i fältet)", "Start with Windows (in the tray)"),
        ["set.autoupload"] = ("Ladda upp automatiskt när spelet avslutas", "Upload automatically when the game exits"),
        ["set.hotfix"] = ("Skicka hotfix-cachen (DBCache.bin)", "Send the hotfix cache (DBCache.bin)"),
        ["set.askclose"] = ("Fråga innan appen stängs", "Ask before closing the app"),
        ["set.account"] = ("Bidragsgivare: {0}", "Contributor: {0}"),
        ["set.unlink"] = ("Koppla från", "Unlink"),
        ["set.unlink.confirm"] = ("Koppla från den här datorn? Nyckeln raderas lokalt.", "Unlink this computer? The key is deleted locally."),
        ["ok"] = ("Spara", "Save"),
        ["cancel"] = ("Avbryt", "Cancel"),
        ["pick.title"] = ("Välj WoW-katalog", "Choose WoW folder"),
        ["pick.intro"] = ("Välj spelets Forever-katalog. Den ligger oftast i …\\World of Warcraft\\_forever_.", "Choose the game's Forever folder. It is usually …\\World of Warcraft\\_forever_."),
        ["pick.none"] = ("Hittade ingen WoW-katalog automatiskt. Välj den med Bläddra.", "No WoW folder was found automatically. Pick it with Browse."),
        ["pick.invalid"] = ("Katalogen innehåller varken Interface eller WTF. Välj spelets underkatalog (t.ex. _forever_).", "The folder contains neither Interface nor WTF. Pick the game subfolder (e.g. _forever_)."),
        ["lang.sv"] = ("Svenska", "Svenska"),
        ["lang.en"] = ("English", "English"),

        // ---- UI V1: onboarding ----
        ["onb.eyebrow"] = ("WOW4EVER.SE · COMMUNITY-KLIENT", "WOW4EVER.SE · COMMUNITY CLIENT"),
        ["onb.title"] = ("Slå dig ner vid elden.", "Pull up a seat by the fire."),
        ["onb.sub"] = ("Du spelar. Tillsammans bygger vi världens kunskap.", "You play. Together we build the world's knowledge."),
        ["onb.start.title"] = ("Börja bidra", "Start contributing"),
        ["onb.start.text"] = ("Välj ditt visningsnamn. Scout håller utkik efter speldata och skickar den när spelet har sparat.", "Choose your display name. Scout keeps an eye out for game data and sends it once the game has saved."),
        ["onb.name"] = ("Ditt visningsnamn (syns på topplistan)", "Your display name (shown on the leaderboard)"),
        ["onb.quick"] = ("Tar bara ett ögonblick", "Only takes a moment"),
        ["onb.have"] = ("Har du redan ett bidragskonto?", "Already contributing on the website?"),
        ["onb.code"] = ("Kopplingskod från guide.wow4ever.se/bidra", "Link code from guide.wow4ever.se/en/contribute"),
        ["onb.code.ph"] = ("Ange din kod", "Enter your code"),
        ["onb.helps.title"] = ("Vad hjälper du till med?", "What are you helping with?"),
        ["onb.helps.1"] = ("NPC:er, quests och tränare", "NPCs, quests and trainers"),
        ["onb.helps.2"] = ("Loot, föremål och världens fynd", "Loot, objects and finds in the world"),
        ["onb.helps.3"] = ("Hotfix-data när spelet sparar", "Hotfix data when the game saves"),
        ["onb.privacy.title"] = ("Din integritet", "Your privacy"),
        ["onb.privacy.text"] = ("Bara addonens data i WoW-katalogen skickas – inget lösenord, inget konto- eller karaktärsnamn. Du kan alltid se exakt vad som skickas.", "Only the addon's data in the WoW folder is sent – no password, no account or character name. You can always see exactly what is sent."),
        ["onb.tavern.who"] = ("Värdshusvärdens råd:", "The innkeeper's advice:"),
        ["onb.tavern.quote"] = ("\"Den bästa hjälten är den som delar sin karta. Den näst bästa bjuder på en öl.\"", "\"The best hero shares their map. The second best buys a round.\""),
        ["onb.footer.ready"] = ("Scout redo", "Scout ready"),
        ["onb.footer.tag"] = ("Din data. Vårt gemensamma äventyr.", "Your data. Our shared adventure."),
        ["onb.working"] = ("Ett ögonblick …", "One moment …"),
        ["onb.usecode"] = ("Koppla med kod i stället", "Link with a code instead"),
        // ---- UI V1: dashboard ----
        ["dash.stat.collected"] = ("Insamlade dataposter", "Collected entries"),
        ["dash.stat.last"] = ("Senaste uppladdning", "Last upload"),
        ["dash.stat.points"] = ("Ditt bidrag", "Your contribution"),
        ["dash.points.unit"] = ("poäng", "points"),
        ["dash.last.ready"] = ("Redo", "Ready"),
        ["dash.last.never"] = ("Inte än", "Not yet"),
        ["dash.last.uploading"] = ("Pågår …", "In progress …"),
        ["dash.last.error"] = ("Fel", "Error"),
        ["dash.last.retry"] = ("Försöker igen", "Retrying"),
        ["dash.table.title"] = ("Insamlad speldata", "Collected game data"),
        ["dash.table.sub"] = ("Sånt som gör guiden bättre för alla äventyrare.", "Things that make the guide better for every adventurer."),
        ["col.status"] = ("Status", "Status"),
        ["st.nodata"] = ("Ingen data hittad", "No data found"),
        ["st.waiting"] = ("Väntar på spelsparning", "Waiting for the game to save"),
        ["st.ready"] = ("Redo att ladda upp", "Ready to upload"),
        ["st.uploading"] = ("Uppladdning pågår", "Uploading"),
        ["st.uploaded"] = ("Uppladdad ✓", "Uploaded ✓"),
        ["st.error"] = ("Fel", "Error"),
        ["pill.addon"] = ("Addon {0} anslutet", "Addon {0} connected"),
        ["pill.noaddon"] = ("Addonen saknas", "Addon missing"),
        ["mine.confirmed"] = ("Bekräftade fynd", "Confirmed finds"),
        ["mine.accepted"] = ("Godtaget av guiden", "Accepted by the guide"),
        ["mine.newbie"] = ("Nybliven scout", "Fledgling scout"),
        ["mine.newbie.text"] = ("Ditt första bekräftade fynd väntar. Varje karta börjar med ett steg.", "Your first confirmed find awaits. Every map starts with a single step."),
        ["mine.hint"] = ("Bekräftat = minst två spelare har rapporterat samma sak.", "Confirmed = at least two players reported the same thing."),
        ["journal.title"] = ("Scoutens lägerjournal", "Scout's camp journal"),
        ["journal.addon"] = ("Addon {0} installerad", "Addon {0} installed"),
        ["journal.uploaded"] = ("{0}: servern tog emot {1} fil(er)", "{0}: the server received {1} file(s)"),
        ["journal.hotfix"] = ("{0}: hotfix-cachen mottagen", "{0}: hotfix cache received"),
        ["journal.nothing"] = ("{0}: inget nytt att skicka", "{0}: nothing new to send"),
        ["journal.error"] = ("{0}: {1}", "{0}: {1}"),
        ["journal.waiting"] = ("Väntar på nästa spelsparning …", "Waiting for the next game save …"),
        ["humor.1"] = ("Scoutens anteckningar är säkrade!", "The scout's notes are safe!"),
        ["humor.2"] = ("Ännu en karta räddad från glömskan.", "Another map saved from oblivion."),
        ["humor.3"] = ("Värdshusvärden räknar fynden. Igen.", "The innkeeper is counting the finds. Again."),
        ["footer.next"] = ("Nästa synk: när spelet sparar (/reload, utloggning, avslut)", "Next sync: when the game saves (/reload, logout, exit)"),
        ["footer.ok"] = ("Allt fungerar", "All good"),

        // ---- Våra felkoder (ErrorCodes) ----
        ["err.network"] = ("Kunde inte nå guide.wow4ever.se.", "Could not reach guide.wow4ever.se."),
        ["err.timeout"] = ("Servern svarade inte i tid.", "The server did not respond in time."),
        ["err.http"] = ("Servern svarade med ett fel ({0}).", "The server returned an error ({0})."),
        ["err.no-key"] = ("Klienten är inte kopplad – registrera dig eller koppla med kod.", "The client is not linked – register or link with a code."),
        ["err.empty-response"] = ("Tomt svar från servern.", "Empty response from the server."),
        ["err.bad-response"] = ("Oväntat svar från servern.", "Unexpected response from the server."),
        ["err.upload-too-large"] = ("Filen är större än 5 MB.", "The file is larger than 5 MB."),
        ["err.hotfix-too-large"] = ("DBCache.bin är större än 30 MB.", "DBCache.bin is larger than 30 MB."),
        ["err.download-too-large"] = ("Nedladdningen är för stor.", "The download is too large."),
        ["err.other-host"] = ("Nedladdningen pekar på en annan värd – avbruten.", "The download points to another host – aborted."),
        ["err.bad-file-name"] = ("Ogiltigt filnamn i versionsinfon.", "Invalid file name in the version info."),
        ["err.sha-missing"] = ("Versionsinfon saknar SHA-256 – avbrutet.", "The version info has no SHA-256 – aborted."),
        ["err.sha-mismatch"] = ("Filens SHA-256 stämmer inte – filen kastades.", "The file's SHA-256 does not match – the file was discarded."),
        ["err.signature-missing"] = ("Uppdateringen är inte signerad – den installeras inte.", "The update is not signed – it will not be installed."),
        ["err.signature-invalid"] = ("Signaturen är ogiltig – uppdateringen installeras inte.", "The signature is invalid – the update will not be installed."),
        ["err.no-public-key"] = ("Den här versionen saknar släppnyckel och kan inte uppdatera sig.", "This build has no release key and cannot update itself."),
        ["err.not-newer"] = ("Ingen nyare version.", "No newer version."),
        ["err.no-release"] = ("Det finns inget släpp att hämta än.", "There is no release to download yet."),
        ["err.addon-missing"] = ("Addonen saknas i klienten.", "The addon is missing from the client."),
        ["err.addon-too-large"] = ("Addon-zip:en är för stor.", "The addon zip is too large."),
        ["err.addon-invalid"] = ("Addon-zip:en är ogiltig.", "The addon zip is invalid."),
        ["err.addon-blocked"] = ("Addonversionen är spärrad – uppladdningen väntar på en nyare addon.", "This addon version is blocked – uploading waits for a newer addon."),
        ["err.lua-parse"] = ("ForeverScout.lua kunde inte läsas (rad {0}).", "ForeverScout.lua could not be read (line {0})."),
        ["err.file-too-large"] = ("Filen är för stor.", "The file is too large."),
        ["err.io"] = ("Kunde inte läsa eller skriva en fil.", "Could not read or write a file."),
        ["err.exe-path"] = ("Hittar inte programfilen.", "Cannot find the program file."),
        ["err.update-failed"] = ("Uppdateringen misslyckades ({0}).", "The update failed ({0})."),
        ["err.redirect"] = ("Servern svarade med en omdirigering ({0}) – den följs inte, inget skickades vidare.", "The server answered with a redirect ({0}) – it is not followed and nothing was sent on."),
        ["err.release-stale"] = ("Versionsinfon är äldre än den senaste som redan kontrollerats – den används inte.", "The version info is older than the latest one already checked – it is not used."),
        ["err.access-denied"] =("Åtkomst utanför WoW-katalogen nekades (skydd).", "Access outside the WoW folder was refused (safeguard)."),
        ["err.not-our-file"] = ("Filen innehåller annat än ForeverScoutDB och skickas inte.", "The file contains more than ForeverScoutDB and is not sent."),
        ["err.app-translocated"] = ("Flytta Forever Scout till mappen Program (Applications) och starta den därifrån – från den här platsen kan appen inte uppdatera sig.", "Move Forever Scout to the Applications folder and start it from there – it cannot update itself from this location."),
        ["err.codesign-invalid"] = ("Den nya versionens Apple-signatur stämmer inte – uppdateringen installeras inte.", "The new version's Apple signature does not match – the update will not be installed."),
        ["err.no-bundle"] = ("Appen körs inte från Forever Scout.app och kan inte uppdatera sig.", "The app is not running from Forever Scout.app and cannot update itself."),

        // ---- macOS (client/mac): texter som skiljer sig från Windows. Mac-appen tar "mac.<nyckel>" före "<nyckel>". ----
        ["mac.set.autostart"] = ("Starta vid inloggning (i menyraden)", "Open at login (in the menu bar)"),
        ["mac.close.tray"] = ("Göm i menyraden", "Hide in the menu bar"),
        ["mac.tray.hint"] = ("Forever Scout körs i menyraden", "Forever Scout is running in the menu bar"),
        ["mac.tray.hint.body"] = ("Klicka på lägerelden i menyraden för att öppna fönstret igen.", "Click the campfire in the menu bar to open the window again."),
        ["mac.takeover.old"] = ("En annan Forever Scout körs redan – avsluta den via lägerelden i menyraden (Avsluta) och starta igen.", "Another Forever Scout is already running – quit it from the campfire in the menu bar (Exit) and start again."),
        ["mac.set.gamedir"] = ("WoW-katalog (Forever, t.ex. /Applications/World of Warcraft/_forever_)", "WoW folder (Forever, e.g. /Applications/World of Warcraft/_forever_)"),
        ["mac.pick.intro"] = ("Välj spelets Forever-katalog. Den ligger oftast i /Applications/World of Warcraft/_forever_.", "Choose the game's Forever folder. It is usually /Applications/World of Warcraft/_forever_."),
        ["mac.about.variant.full"] = ("macOS", "macOS"),
        ["mac.update.inuse"] = ("Kunde inte ersätta Forever Scout.app. Kontrollera att appen ligger i Program (Applications) och att ditt konto får ändra den, och försök igen.", "Could not replace Forever Scout.app. Check that the app is in Applications and that your account may change it, then try again."),
        ["mac.set.unlink.confirm"] = ("Koppla från den här datorn? Nyckeln raderas ur nyckelringen.", "Unlink this computer? The key is deleted from the keychain."),
        ["mac.menu.hide"] = ("Göm fönstret", "Hide window"),
        ["mac.gamedir.confirm"] = ("Forever Scout hittade den här spelkatalogen:\n\n{0}\n\nÄr det din WoW Forever-katalog? Scout installerar addonen där och laddar upp addonens data därifrån. Den ligger oftast i /Applications/World of Warcraft/_forever_.\n\nVälj Nej om du inte känner igen den – då får du välja katalogen själv.", "Forever Scout found this game folder:\n\n{0}\n\nIs this your WoW Forever folder? Scout installs the addon there and uploads the addon's data from it. It is usually /Applications/World of Warcraft/_forever_.\n\nChoose No if you do not recognise it – you can then pick the folder yourself."),

        // ---- Serverns felkoder (lib/scout/http.ts, fältet "code") ----
        ["srv.origin"] = ("Fel ursprung.", "Wrong origin."),
        ["srv.rate-limited"] = ("För många försök – vänta en stund.", "Too many attempts – please wait a moment."),
        ["srv.too-large"] = ("För stor förfrågan.", "Request too large."),
        ["srv.invalid"] = ("Ogiltig förfrågan.", "Invalid request."),
        ["srv.name-length"] = ("Namnet ska vara 2–24 tecken.", "The name must be 2–24 characters."),
        ["srv.name-chars"] = ("Ogiltiga tecken i namnet.", "Invalid characters in the name."),
        ["srv.name-reserved"] = ("Namnet är reserverat.", "That name is reserved."),
        ["srv.name-taken"] = ("Namnet är upptaget. Valde du det på webbsidan? Koppla med en kod från guide.wow4ever.se/bidra i stället.", "That name is taken. Did you choose it on the website? Link with a code from guide.wow4ever.se/en/contribute instead."),
        ["srv.unauthorized"] = ("Nyckeln är ogiltig – koppla klienten igen.", "The key is invalid – link the client again."),
        ["srv.bad-code"] = ("Koden stämmer inte eller har gått ut.", "The code is wrong or has expired."),
        ["srv.bad-file"] = ("Filen kunde inte läsas som ForeverScout.lua.", "The file could not be read as ForeverScout.lua."),
        ["srv.addon-blocked"] = ("Addonversionen är spärrad – uppladdningen väntar på en nyare addon.", "This addon version is blocked – uploading waits for a newer addon."),
        ["srv.not-dbcache"] = ("Filen är inte en giltig DBCache.bin.", "The file is not a valid DBCache.bin."),
        ["srv.unavailable"] = ("Insamlingen är tillfälligt avstängd.", "Collection is temporarily disabled."),
        ["srv.server"] = ("Serverfel.", "Server error."),
        ["srv.bad-file.detail"] = ("Servern avvisade filen: {0}", "The server rejected the file: {0}"),
        ["srv.detail"] = ("({0})", "({0})"),
        ["srv.unknown"] = ("Servern svarade med felet \"{0}\".", "The server returned the error \"{0}\"."),
    };

    /// <summary>Alla nycklar (för testet som kräver både sv och en).</summary>
    public static IReadOnlyDictionary<string, (string Sv, string En)> All => T;

    public static string S(string key) => T.TryGetValue(key, out var v) ? (IsSv ? v.Sv : v.En) : key;
    public static string F(string key, params object?[] args) => string.Format(Culture, S(key), args);
    public static bool Has(string key) => T.ContainsKey(key);

    public static string Kind(DataKind k) => S("kind." + k);
    public static string Desc(DataKind k) => S("desc." + k);

    /// <summary>Text för ett fel på valt språk. Okända undantag visas aldrig med sitt (ev. svenska/OS-språkiga) meddelande.</summary>
    public static string Error(Exception ex) => ex switch
    {
        // Serverns egen förklaring ("detail") visas efter vår text, t.ex. "Servern avvisade filen: Okänd eller saknad klientbuild".
        ApiException { FromServer: true, Code: "bad-file", ServerDetail: { } d } => F("srv.bad-file.detail", d),
        ApiException { FromServer: true, ServerDetail: { } d } a => (Has("srv." + a.Code) ? S("srv." + a.Code) : F("srv.unknown", a.Code)) + " " + F("srv.detail", d),
        ApiException { FromServer: true } a => Has("srv." + a.Code) ? S("srv." + a.Code) : F("srv.unknown", a.Code),
        ScoutException s when Has("err." + s.Code) => F("err." + s.Code, s.Detail ?? ""),
        IOException or UnauthorizedAccessException => S("err.io"),
        _ => S("err.bad-response"),
    };

    public static string Size(long bytes)
    {
        if (bytes <= 0) return "–";
        if (bytes < 1024) return string.Format(Culture, "{0} B", bytes);
        if (bytes < 1024 * 1024) return string.Format(Culture, "{0:0.0} kB", bytes / 1024.0);
        return string.Format(Culture, "{0:0.0} MB", bytes / 1024.0 / 1024.0);
    }

    public static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm", Culture) : local.ToString("d MMM HH:mm", Culture);
    }
}
