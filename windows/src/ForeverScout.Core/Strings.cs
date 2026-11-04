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
        ["err.addon-blocked"] = ("Addonversionen är spärrad – uppdaterar addonen.", "This addon version is blocked – updating the addon."),
        ["err.lua-parse"] = ("ForeverScout.lua kunde inte läsas (rad {0}).", "ForeverScout.lua could not be read (line {0})."),
        ["err.file-too-large"] = ("Filen är för stor.", "The file is too large."),
        ["err.io"] = ("Kunde inte läsa eller skriva en fil.", "Could not read or write a file."),
        ["err.exe-path"] = ("Hittar inte programfilen.", "Cannot find the program file."),
        ["err.access-denied"] = ("Åtkomst utanför WoW-katalogen nekades (skydd).", "Access outside the WoW folder was refused (safeguard)."),
        ["err.not-our-file"] = ("Filen innehåller annat än ForeverScoutDB och skickas inte.", "The file contains more than ForeverScoutDB and is not sent."),

        // ---- Serverns felkoder (lib/scout/http.ts, fältet "code") ----
        ["srv.origin"] = ("Fel ursprung.", "Wrong origin."),
        ["srv.rate-limited"] = ("För många försök – vänta en stund.", "Too many attempts – please wait a moment."),
        ["srv.too-large"] = ("För stor förfrågan.", "Request too large."),
        ["srv.invalid"] = ("Ogiltig förfrågan.", "Invalid request."),
        ["srv.name-length"] = ("Namnet ska vara 2–24 tecken.", "The name must be 2–24 characters."),
        ["srv.name-chars"] = ("Ogiltiga tecken i namnet.", "Invalid characters in the name."),
        ["srv.name-reserved"] = ("Namnet är reserverat.", "That name is reserved."),
        ["srv.name-taken"] = ("Namnet är upptaget.", "That name is taken."),
        ["srv.unauthorized"] = ("Nyckeln är ogiltig – koppla klienten igen.", "The key is invalid – link the client again."),
        ["srv.bad-code"] = ("Koden stämmer inte eller har gått ut.", "The code is wrong or has expired."),
        ["srv.bad-file"] = ("Filen kunde inte läsas som ForeverScout.lua.", "The file could not be read as ForeverScout.lua."),
        ["srv.addon-blocked"] = ("Addonversionen är spärrad – uppdaterar addonen.", "This addon version is blocked – updating the addon."),
        ["srv.not-dbcache"] = ("Filen är inte en giltig DBCache.bin.", "The file is not a valid DBCache.bin."),
        ["srv.unavailable"] = ("Insamlingen är tillfälligt avstängd.", "Collection is temporarily disabled."),
        ["srv.server"] = ("Serverfel.", "Server error."),
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
