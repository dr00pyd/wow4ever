-- ForeverScout – texts (Swedish / English).
-- WoW has no Swedish client, so "auto" picks Swedish only for svSE; `/scout lang sv` forces it.
local _, ns = ...

local texts = {
  en = {
    title = "ForeverScout",
    status_head = "ForeverScout %s - %d posts saved (seq %d).",
    status_counts = "NPCs %d, trainers %d, vendors %d, quests %d, objects %d, loot %d, flight points %d.",
    status_last = "Latest: %s \"%s\", %s ago.",
    status_none = "Nothing recorded yet - talk to NPCs, loot and fly as usual.",
    status_pos_off = "Positions are paused right now (instance, taxi or vehicle).",
    status_char = "Character data: %s.",
    on = "on", off = "off",
    help = {
      "/scout - show status",
      "/scout help - this help",
      "/scout char on|off - include class, race, level and professions (off by default; never name or realm)",
      "/scout lang sv|en|auto - language for these messages",
      "/scout reset - clear collected data (asks for confirmation)",
      "/scout sync - save now and let the client upload (reloads the UI)",
      "Data is uploaded by the ForeverScout client or at guide.wow4ever.se/bidra.",
    },
    char_on = "Character data is now ON for this character (class, race, level, professions, recipes).",
    char_off = "Character data is now OFF and has been removed from the file.",
    lang_set = "Language: %s.",
    reset_ask = "Type /scout reset confirm to clear all collected data (the install id is kept).",
    reset_done = "Collected data cleared.",
    sync = "Saving and sending … (/reload)",
    debug = "Errors this session: %d. Last: %s",
    unknown = "Unknown command. Type /scout help.",
    kind_npc = "NPC", kind_object = "object", kind_trainer = "trainer", kind_vendor = "vendor",
    kind_quest = "quest", kind_loot = "loot", kind_flight = "flight point",
    sec = "%d s", min = "%d min", hour = "%d h", day = "%d d",
  },
  sv = {
    title = "ForeverScout",
    status_head = "ForeverScout %s - %d poster sparade (seq %d).",
    status_counts = "NPC:er %d, tränare %d, försäljare %d, quests %d, objekt %d, loot %d, flygpunkter %d.",
    status_last = "Senast: %s \"%s\", för %s sedan.",
    status_none = "Inget insamlat än - prata med NPC:er, loota och flyg som vanligt.",
    status_pos_off = "Positioner är pausade just nu (instans, flygtur eller fordon).",
    status_char = "Karaktärsdata: %s.",
    on = "på", off = "av",
    help = {
      "/scout - visa status",
      "/scout help - den här hjälpen",
      "/scout char on|off - ta med klass, ras, nivå och yrken (av som standard; aldrig namn eller realm)",
      "/scout lang sv|en|auto - språk för de här meddelandena",
      "/scout reset - töm insamlad data (frågar först)",
      "/scout sync - spara nu så att klienten laddar upp (laddar om gränssnittet)",
      "Data laddas upp av ForeverScout-klienten eller på guide.wow4ever.se/bidra.",
    },
    char_on = "Karaktärsdata är nu PÅ för den här karaktären (klass, ras, nivå, yrken, recept).",
    char_off = "Karaktärsdata är nu AV och har tagits bort ur filen.",
    lang_set = "Språk: %s.",
    reset_ask = "Skriv /scout reset confirm för att tömma all insamlad data (installations-id behålls).",
    reset_done = "Insamlad data tömd.",
    sync = "Sparar och skickar … (/reload)",
    debug = "Fel denna session: %d. Senaste: %s",
    unknown = "Okänt kommando. Skriv /scout help.",
    kind_npc = "NPC", kind_object = "objekt", kind_trainer = "tränare", kind_vendor = "försäljare",
    kind_quest = "quest", kind_loot = "loot", kind_flight = "flygpunkt",
    sec = "%d s", min = "%d min", hour = "%d h", day = "%d d",
  },
}

ns.texts = texts

-- Returns the text table for the chosen language ("sv", "en" or nil/"auto").
function ns.GetTexts(choice)
  if choice == "sv" or choice == "en" then return texts[choice] end
  local loc = GetLocale and GetLocale() or "enUS"
  if loc == "svSE" then return texts.sv end
  return texts.en
end
