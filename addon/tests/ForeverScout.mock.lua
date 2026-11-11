-- Offlinetest av ForeverScout med påhittade WoW-API:er (ingen spelklient, ingen server). Kör från repots rot:
--   lua5.1 client/addon/tests/ForeverScout.mock.lua
-- (eller via Pythons lupa: lupa.lua51). Ligger utanför addonmappen så att den aldrig följer med i zip-filen.
-- Testar 1.0.4 och 1.0.5 (se avsnittet längst ned): L13 (objektnamn bara från Opening/insamling, aldrig en spelare), mobs via mål/muspekare
-- (aldrig spelare/husdjur/secret values, inte i strid, ungefärliga områden med dedup och tak), brevlådor och
-- tjänsteroller via PLAYER_INTERACTION_MANAGER_FRAME_SHOW.
local DIR = "client/addon/ForeverScout/"

local fails, passes = 0, 0
local function check(cond, msg)
  if cond then passes = passes + 1 else fails = fails + 1; print("FEL: " .. msg) end
end

---------------------------------------------------------------------------
-- Påhittat WoW-API
---------------------------------------------------------------------------
local SECRET = setmetatable({}, { __tostring = function() return "<secret>" end })
function issecretvalue(v) return v == SECRET end

local clock, combat = 1000, false
local pos = { m = 1429, x = 0.40, y = 0.60 }
local units = {}
function GetTime() return clock end
function time() return 1793750000 + clock end
function GetBuildInfo() return "1.60.1", "70790", "Nov 1 2026", 16001 end
function GetLocale() return "enUS" end
local instance = nil   -- 1.0.7: instansens kart-id när spelaren är i en instans
function IsInInstance() return instance ~= nil end
function GetInstanceInfo()
  if not instance then return "Eastern Kingdoms", "none", 0, "", 0, 0, false, 0 end
  return "The Hall of Thanes", "party", 1, "Normal", 5, 0, false, instance
end
function UnitOnTaxi() return false end
function UnitInVehicle() return false end
function InCombatLockdown() return combat end
function UnitAffectingCombat(u) return u == "player" and combat end
C_Timer = { After = function(_, fn) fn() end }
C_Map = {
  GetBestMapForUnit = function() return pos.m end,
  GetPlayerMapPosition = function() return { GetXY = function() return pos.x, pos.y end } end,
}
Enum = { PlayerInteractionType = { MailInfo = 17, SpiritHealer = 18, Auctioneer = 21, StableMaster = 22 }, LootSlotType = { Money = 2 } }
local function u(t) return t and units[t] end
function UnitGUID(t) local x = u(t); return x and x.guid end
function UnitName(t) local x = u(t); return x and x.name end
function UnitExists(t) return u(t) ~= nil end
function UnitIsPlayer(t) local x = u(t); if x then return x.isPlayer or false end; return t == "Bob" end
function UnitPlayerControlled(t) local x = u(t); if not x then return false end; return x.controlled or false end
function UnitIsOtherPlayersPet() return false end
function UnitIsUnit(a, b) return a == b end
function UnitLevel(t) local x = u(t); return x and x.level end
function UnitClassification(t) local x = u(t); return x and x.class end
function UnitCreatureType(t) local x = u(t); if x then return x.ctype, x.ctypeId end end
function UnitReaction(t) local x = u(t); return x and x.reaction end
function UnitFactionGroup(t) return t == "player" and "Alliance" or nil end
-- Loot
local loot = {}
function GetNumLootItems() return #loot end
function GetLootSlotType() return 1 end
function GetLootSlotLink(i) return loot[i] and ("|Hitem:" .. loot[i].item .. ":|h") end
function GetLootSourceInfo(i) return loot[i].guid, 1 end
function IsFishingLoot() return false end
C_Spell = { GetSpellName = function(id) return ({ [3365] = "Opening", [2575] = "Mining", [1459] = "Arcane Intellect" })[id] end }

local frame = { events = {} }
function frame:RegisterEvent(e) self.events[e] = true end
function frame:UnregisterEvent(e) self.events[e] = nil end
function frame:SetScript(_, fn) self.script = fn end
function CreateFrame() return frame end
SlashCmdList = {}
DEFAULT_CHAT_FRAME = { AddMessage = function() end }

---------------------------------------------------------------------------
-- Ladda addonen
---------------------------------------------------------------------------
local ns = {}
assert(loadfile(DIR .. "Locale.lua"))("ForeverScout", ns)
assert(loadfile(DIR .. "ForeverScout.lua"))("ForeverScout", ns)
local function fire(event, ...) frame.script(frame, event, ...) end
fire("ADDON_LOADED", "ForeverScout")
local T = ns._test
local db = T.db()
check(db.addon == "1.0.9", "addonversion 1.0.9")
check(type(db.mobs) == "table" and type(db.mail) == "table", "mobs och mail finns")
for _, e in ipairs({ "PLAYER_TARGET_CHANGED", "UPDATE_MOUSEOVER_UNIT", "MAIL_SHOW", "AUCTION_HOUSE_SHOW", "PLAYER_INTERACTION_MANAGER_FRAME_SHOW" }) do
  check(frame.events[e], "händelsen registrerad: " .. e)
end
-- 1.0.6: en rad i chatten efter inloggning/reload, så att spelaren ser att addonen är laddad.
do
  local said = {}
  local old = DEFAULT_CHAT_FRAME.AddMessage
  DEFAULT_CHAT_FRAME.AddMessage = function(_, msg) said[#said + 1] = msg end
  check(frame.events.PLAYER_LOGIN, "händelsen registrerad: PLAYER_LOGIN")
  fire("PLAYER_LOGIN")
  DEFAULT_CHAT_FRAME.AddMessage = old
  check(#said == 1 and said[1]:find("1.0.9", 1, true) and said[1]:find("/reload", 1, true), "inloggningsraden nämner version och /reload")
end

---------------------------------------------------------------------------
-- L13: objektnamn
---------------------------------------------------------------------------
local CHEST = "GameObject-0-1-2-3-2843-0000000001"
local function lootChest()
  loot = { { item = 2589, guid = CHEST } }
  T.session.looted = {}
  fire("LOOT_OPENED")
end
fire("UNIT_SPELLCAST_SENT", "player", "Thrall", "Cast-1", 1459)          -- en vanlig besvärjelse på en spelare
check(T.session.sentName == nil, "L13: namn sparas inte för andra besvärjelser")
units.target = { guid = "Player-1-0001", name = "Bob", isPlayer = true }
fire("UNIT_SPELLCAST_SENT", "player", "Bob", "Cast-2", 3365)            -- Opening men målet är en spelare
check(T.session.sentName == nil, "L13: aldrig en spelares namn")
units.target = nil
fire("UNIT_SPELLCAST_SENT", "player", "Bob", "Cast-3", 3365)            -- gruppmedlem via namn
check(T.session.sentName == nil, "L13: aldrig en gruppmedlems namn")
fire("UNIT_SPELLCAST_SENT", "player", "Battered Chest", "Cast-4", 3365)
check(T.session.sentName == "Battered Chest", "L13: Opening på ett objekt godtas")
lootChest()
check(db.objects[2843] and db.objects[2843].n == "Battered Chest", "L13: objektet får namnet")
check(T.session.sentName == nil, "L13: namnet nollställs efter användning")
fire("UNIT_SPELLCAST_SENT", "player", "Copper Vein", "Cast-5", 2575)     -- Mining
check(T.session.sentName == "Copper Vein", "L13: Mining godtas")
fire("UNIT_SPELLCAST_SENT", "player", "Somebody", "Cast-6", 1459)
check(T.session.sentName == nil, "L13: en ny besvärjelse rensar namnet")

---------------------------------------------------------------------------
-- Mobs
---------------------------------------------------------------------------
local WOLF = "Creature-0-1-2-3-299-0000000001"
units.target = { guid = WOLF, name = "Diseased Young Wolf", level = 2, class = "normal", ctype = "Beast", ctypeId = 1, reaction = 2 }
local seq0 = db.seq
fire("PLAYER_TARGET_CHANGED")
local wolf = db.mobs[299]
check(wolf and wolf.n == "Diseased Young Wolf", "mob sparas")
check(wolf and wolf.c == "normal" and wolf.ct == 1 and wolf.lmin == 2 and wolf.lmax == 2 and wolf.r == "hostile", "mobfält")
check(wolf and #wolf.a == 1 and wolf.a[1].m == 1429 and wolf.a[1].x == 40 and wolf.a[1].t % 86400 == 0, "ungefärligt område, dag")
check(db.seq == seq0 + 1, "ny seq för nytt område")
fire("PLAYER_TARGET_CHANGED")
check(#wolf.a == 1 and db.seq == seq0 + 1, "samma GUID inom 120 s ignoreras")
clock = clock + 200
units.target.guid = "Creature-0-1-2-3-299-0000000002"; units.target.level = 3
pos.x = 0.42
fire("PLAYER_TARGET_CHANGED")
check(#wolf.a == 1 and wolf.lmax == 3, "område inom 5 kart-% = samma; level uppdateras")
pos.x = 0.60
units.target.guid = "Creature-0-1-2-3-299-0000000003"
fire("PLAYER_TARGET_CHANGED")
check(#wolf.a == 2, "nytt område längre bort")
for i = 1, 20 do
  pos.x = (i * 4.9) / 100 % 1; pos.y = 0.1 + i * 0.03
  units.target.guid = "Creature-0-1-2-3-299-1" .. i
  fire("PLAYER_TARGET_CHANGED")
end
check(#wolf.a == 8, "högst 8 områden per mob")
-- Spelare, husdjur, secret values och strid
units.mouseover = { guid = "Player-1-0002", name = "Alice", isPlayer = true, level = 10 }
fire("UPDATE_MOUSEOVER_UNIT")
units.mouseover = { guid = "Creature-0-1-2-3-416-0000000001", name = "Fluffy", controlled = true, level = 5, class = "normal" }
fire("UPDATE_MOUSEOVER_UNIT")
units.mouseover = { guid = "Pet-0-1-2-3-416-0000000002", name = "Fluffy", level = 5 }
fire("UPDATE_MOUSEOVER_UNIT")
units.mouseover = { guid = "Creature-0-1-2-3-417-0000000001", name = "Secret", controlled = SECRET, level = 5 }
fire("UPDATE_MOUSEOVER_UNIT")
units.mouseover = { guid = "Creature-0-1-2-3-418-0000000001", name = "Searing Totem", level = 5, ctype = "Totem", ctypeId = 11 }
fire("UPDATE_MOUSEOVER_UNIT")
combat = true
units.mouseover = { guid = "Creature-0-1-2-3-30-0000000001", name = "Forest Spider", level = 5, class = "normal" }
fire("UPDATE_MOUSEOVER_UNIT")
combat = false
local n = 0
for id in pairs(db.mobs) do n = n + 1; check(id == 299, "oväntad mob " .. tostring(id)) end
check(n == 1, "inga spelare, husdjur, totem, secret values eller mobs i strid")
units.mouseover.guid = "Creature-0-1-2-3-30-0000000002"
fire("UPDATE_MOUSEOVER_UNIT")
check(db.mobs[30] and db.mobs[30].n == "Forest Spider", "utanför strid sparas mouseover")
-- Inget spelarnamn någonstans i filen
local function scan(t, seen)
  seen = seen or {}
  if seen[t] then return true end
  seen[t] = true
  for k, v in pairs(t) do
    if v == "Alice" or v == "Bob" or v == "Thrall" or v == "Fluffy" or k == "Alice" then return false end
    if type(v) == "table" and not scan(v, seen) then return false end
  end
  return true
end
check(scan(db), "inga spelar- eller husdjursnamn i ForeverScoutDB")

---------------------------------------------------------------------------
-- Brevlådor och tjänsteroller
---------------------------------------------------------------------------
pos.m, pos.x, pos.y = 1453, 0.5, 0.5
units.npc = { guid = "GameObject-0-1-2-3-143990-0000000001", name = "Mailbox" }
fire("MAIL_SHOW")
fire("PLAYER_INTERACTION_MANAGER_FRAME_SHOW", 17)
check(#db.mail == 1 and db.mail[1].m == 1453 and db.mail[1].x == 50 and db.mail[1].id == 143990, "brevlåda en gång")
units.npc = { guid = "Creature-0-1-2-3-99999-0000000001", name = "Mail Courier" }
pos.x = 0.6
fire("MAIL_SHOW")
check(#db.mail == 1, "en varelse är ingen brevlåda")
units.npc = { guid = "Creature-0-1-2-3-8670-0000000001", name = "Auctioneer Chilton", level = 50, reaction = 5 }
fire("PLAYER_INTERACTION_MANAGER_FRAME_SHOW", 21)
check(db.npcs[8670] and db.npcs[8670].roles.auction == true, "auktionsförrättare")
fire("AUCTION_HOUSE_SHOW")
check(#db.npcs[8670].spots == 1, "dubbla händelser ger en position")
units.npc = { guid = "Creature-0-1-2-3-6749-0000000001", name = "Erma", level = 30, reaction = 5 }
fire("PLAYER_INTERACTION_MANAGER_FRAME_SHOW", 22)
check(db.npcs[6749] and db.npcs[6749].roles.stable == true, "stallmästare")
units.npc = { guid = "Creature-0-1-2-3-6491-0000000001", name = "Spirit Healer", level = 60, reaction = 5 }
fire("PLAYER_INTERACTION_MANAGER_FRAME_SHOW", 18)
check(db.npcs[6491] and db.npcs[6491].roles.spirit == true, "andeläkare")
fire("PLAYER_INTERACTION_MANAGER_FRAME_SHOW", 999)
check(T.session.errors == 0, "inga fångade fel (" .. tostring(T.session.lastError) .. ")")

-- /scout (status) får inte krascha med de nya räknarna
SlashCmdList.FOREVERSCOUT("")
check(T.session.errors == 0, "/scout utan fel (" .. tostring(T.session.lastError) .. ")")

---------------------------------------------------------------------------
-- 1.0.5: fickstöld/förtrollningsupplösning, kostnadssträng, gruppdedup (h/grp/pf), kills, dolda lootkällor,
-- quest-XP med spelarens nivå, exakta mobplatser, HP/mana per nivå, alla varor, objekt, suffix, berusning,
-- latens, flygkostnader och nya roller.
---------------------------------------------------------------------------
local net = { home = 40, world = 60 }
function GetNetStats() return 0, 0, net.home, net.world end
local group = false
function IsInGroup() return group end
function UnitIsDead(t) local x = u(t); if x then return x.dead or false end; return false end
function UnitIsTapDenied(t) local x = u(t); if x then return x.tapDenied or false end; return false end
function CheckInteractDistance(t) local x = u(t); return x and x.near or false end
function UnitHealthMax(t) local x = u(t); return x and x.hpmax end
function UnitPowerType(t) local x = u(t); return x and x.ptype or 1 end
function UnitPowerMax(t) local x = u(t); return x and x.mpmax end
local lootable = {}
function CanLootUnit(g) return lootable[g] end
C_Item = {
  GetItemIDByGUID = function() return nil end,          -- förtrollningsupplöst föremål finns inte längre
  GetItemInfoInstant = function(n) return n == "Runed Copper Ring" and 15210 or nil end,
}
C_Spell.GetSpellName = function(id)
  return ({ [3365] = "Opening", [2575] = "Mining", [1459] = "Arcane Intellect", [921] = "Pick Pocket", [13262] = "Disenchant" })[id]
end
-- Lootfönstret: varje plats { item, guid, c, link }
function GetLootSlotLink(i) local l = loot[i]; return l and (l.link or ("|Hitem:" .. l.item .. ":|h")) end
function GetLootSourceInfo(i) return loot[i].guid, loot[i].c or 1 end
local function lootWindow(slots) loot = slots; fire("LOOT_OPENED") end

-- guidTag och suffix
local CORPSE = "Creature-0-4372-0-48-299-000012ABCD"
local tag = T.guidTag(CORPSE)
check(type(tag) == "string" and tag:match("^%x%x%x%x%x%x%x%x$") ~= nil, "guidTag: 8 hex-tecken")
check(T.guidTag(CORPSE) == tag and T.guidTag("Creature-0-4372-0-48-299-000012ABCE") ~= tag, "guidTag: stabil och olika för olika kroppar")
check(T.suffixFromLink("|cff1eff00|Hitem:15210:0:0:0:0:0:1017:987654:12|h[Runed Copper Ring of the Bear]|h|r") == 1017, "suffix ur länken")
check(T.suffixFromLink("|Hitem:15210:0:0:0:0:0:-12:0|h") == -12, "negativt suffix")
check(T.suffixFromLink("|Hitem:2589:0:0:0:0:0:0:987654|h") == nil and T.suffixFromLink("|Hitem:2589:|h") == nil, "inget suffix")

-- Fickstöld: egen källa "pick" som inte blockerar kroppens vanliga loot
pos.m, pos.x, pos.y = 1429, 0.3, 0.3
local nLoot = #db.loot
fire("UNIT_SPELLCAST_SUCCEEDED", "player", "Cast-p1", 921)
lootWindow({ { item = 5428, guid = CORPSE } })
local pick = db.loot[#db.loot]
check(#db.loot == nLoot + 1 and pick.src == "pick" and pick.id == 299, "fickstöld = src pick")
lootWindow({ { item = 2589, guid = CORPSE } })
local corpse = db.loot[#db.loot]
check(#db.loot == nLoot + 2 and corpse.src == "npc" and corpse.id == 299, "kroppens loot blockeras inte av fickstölden")
check(corpse.h == tag and corpse.pf == "Alliance" and corpse.grp == nil, "loot: kroppstagg, fraktion, ingen grupp")
lootWindow({ { item = 2589, guid = CORPSE } })
check(#db.loot == nLoot + 2, "samma kropp igen: ingen ny post")

-- Förtrollningsupplösning: föremålet finns inte längre, id:t från kastet
fire("UNIT_SPELLCAST_SENT", "player", "Runed Copper Ring", "Cast-d1", 13262)
fire("UNIT_SPELLCAST_SUCCEEDED", "player", "Cast-d1", 13262)
lootWindow({ { item = 10940, guid = "Item-4372-0-40000000ABCDEF01", c = 2 } })
local dis = db.loot[#db.loot]
check(dis.src == "dis" and dis.id == 15210 and dis.items[1].id == 10940 and dis.items[1].c == 2, "förtrollningsupplösning = src dis, föremålets id")

-- Grupp och suffix
group = true
lootWindow({ { item = 15210, guid = "Creature-0-4372-0-48-1922-0000000001", link = "|Hitem:15210:0:0:0:0:0:1017:55555:12|h" } })
local g = db.loot[#db.loot]
check(g.grp == true and g.items[1].s == 1017, "i grupp: grp = true; suffix på föremålet")
group = false

-- Latens över 300 ms: ingen loot sparas
net.world = 450
local before = #db.loot
fire("UNIT_SPELLCAST_SUCCEEDED", "player", "Cast-p2", 921)
lootWindow({ { item = 2589, guid = "Creature-0-4372-0-48-299-0000000777" } })
check(#db.loot == before and T.session.gather == nil, "hög latens: lootfönstret hoppas över")
net.world = 60

-- Dolda (secret) lootkällor i instanser: död målenhet, sedan bossen från ENCOUNTER_END (en gång)
local DEAD = "Creature-0-4372-389-77-11520-0000000042"
units.target = { guid = DEAD, name = "Taragaman the Hungerer", dead = true, level = 16 }
lootWindow({ { item = 14145, guid = SECRET } })
local hid = db.loot[#db.loot]
check(hid.src == "npc" and hid.id == 11520 and hid.h == T.guidTag(DEAD), "dold källa: den döda målenheten")
units.target = nil
fire("ENCOUNTER_END", 663, "Lucifron", 9, 40, 1)
lootWindow({ { item = 16800, guid = SECRET } })
local boss = db.loot[#db.loot]
check(boss.src == "boss" and boss.id == 663 and boss.h == nil, "dold källa: bossen från ENCOUNTER_END")
before = #db.loot
lootWindow({ { item = 16801, guid = SECRET } })
check(#db.loot == before, "bossen används bara en gång")
fire("ENCOUNTER_END", 664, "Magmadar", 9, 40, 0)
lootWindow({ { item = 16802, guid = SECRET } })
check(#db.loot == before, "förlorat encounter ger ingen boss")

check(hid.im == nil and boss.im == nil, "utanför instans: inget instans-id på loot")

-- 1.0.7: i en instans följer instansens kart-id med loot, kills och mobbar – aldrig en position
instance = 3065
local HALL = "Creature-0-4372-3065-77-250001-0000000043"
units.target = { guid = HALL, name = "Plunder", dead = true, level = 30 }
lootWindow({ { item = 2589, guid = HALL } })
local hl = db.loot[#db.loot]
check(hl.src == "npc" and hl.id == 250001 and hl.im == 3065 and hl.m == nil and hl.x == nil, "instans: loot får im, ingen position")
-- 1.0.8: spelet döljer mål för addons i instanser – mobben och killen noteras när kroppen lootas
local pm = db.mobs[250001]
check(pm and pm.n == "Plunder" and pm.i and pm.i[1] == 3065 and pm.lmin == 30 and (pm.a == nil or #pm.a == 0), "instans: lootad mob sparas med namn, level och instans – ingen plats")
local pk = db.kills[#db.kills]
check(pk and pk.id == 250001 and pk.im == 3065 and pk.loot == true and pk.h ~= nil, "instans: lootad kropp räknas som kill")
local nKills, nMobs = #db.kills, 0
for _ in pairs(db.mobs) do nMobs = nMobs + 1 end
lootWindow({ { item = 2589, guid = HALL } })
check(#db.kills == nKills, "samma kropp räknas bara en gång")
-- Utan mål eller muspekare på kroppen: killen räknas, men ingen namnlös mob sparas
units.target = nil
local NONAME = "Creature-0-4372-3065-77-250003-0000000045"
lootWindow({ { item = 2589, guid = NONAME } })
check(#db.kills == nKills + 1 and db.kills[#db.kills].id == 250003 and db.mobs[250003] == nil, "instans: kill utan läsbart namn ger ingen mob")
-- Målet är en annan varelse än kroppen: dess namn får inte hamna på den lootade mobben
units.target = { guid = "Creature-0-4372-3065-77-250009-0000000049", name = "Någon Annan", dead = false, level = 31 }
local OTHER = "Creature-0-4372-3065-77-250004-0000000046"
lootWindow({ { item = 2589, guid = OTHER } })
check(db.mobs[250004] == nil, "instans: namnet tas bara från enheten som ÄR kroppen")
units.target = { guid = HALL, name = "Plunder", dead = true, level = 30 }
units.target = nil
fire("ENCOUNTER_END", 3494, "Plunder", 1, 5, 1)
lootWindow({ { item = 2590, guid = SECRET } })
check(db.loot[#db.loot].src == "boss" and db.loot[#db.loot].id == 3494 and db.loot[#db.loot].im == 3065, "instans: bossloot får im")
clock = clock + 300
local GHOST = "Creature-0-4372-3065-77-250002-0000000044"
units.mouseover = { guid = GHOST, name = "Ghostly Attendant", level = 30, class = "normal", ctype = "Undead", ctypeId = 6, reaction = 4 }
fire("UPDATE_MOUSEOVER_UNIT")
local gm = db.mobs[250002]
check(gm and gm.n == "Ghostly Attendant" and gm.i and gm.i[1] == 3065 and #gm.i == 1, "instans: mobben får instansens id")
check(gm and (gm.a == nil or #gm.a == 0) and (gm.e == nil or #gm.e == 0), "instans: ingen mobplats")
clock = clock + 300
fire("UPDATE_MOUSEOVER_UNIT")
check(#gm.i == 1, "samma instans sparas en gång")
units.mouseover = nil

-- 1.0.9: vilken varelse som är vilken boss (namnen döljs i instanser, encounter-id:t gör det inte)
local function bossRows(e, s)
  local out = {}
  for _, b in ipairs(db.bosses) do if b.e == e and (not s or b.s == s) then out[#out + 1] = b end end
  return out
end
units.boss1 = { guid = "Creature-0-4372-3065-77-263396-0000000050", name = "Faldrim Anvilmar" }
fire("ENCOUNTER_START", 3493, "Faldrim Anvilmar", 1, 5)
local fb = bossRows(3493, "u")[1]
check(fb and fb.id == 263396 and fb.im == 3065 and fb.o == 1 and fb.r == nil and fb.n == nil and fb.t and fb.q, "boss: boss1 under fighten ger encounter → varelse")
fire("INSTANCE_ENCOUNTER_ENGAGE_UNIT")
fire("ENCOUNTER_END", 3493, "Faldrim Anvilmar", 1, 5, 1)
check(#bossRows(3493, "u") == 1, "boss: samma koppling sparas en gång")
units.boss1 = { guid = SECRET }
fire("ENCOUNTER_START", 3495, "Magmatus", 1, 5)
fire("INSTANCE_ENCOUNTER_ENGAGE_UNIT")
check(#bossRows(3495, "u") == 0, "boss: dold enhet (secret value) ger ingen koppling")
units.boss1 = nil
fire("ENCOUNTER_END", 3495, "Magmatus", 1, 5, 1)
lootWindow({ { item = 2589, guid = "Creature-0-4372-3065-77-263438-0000000060" } })
lootWindow({ { item = 2589, guid = "Creature-0-4372-3065-77-263390-0000000061" } })
lootWindow({ { item = 2589, guid = "Creature-0-4372-3065-77-263438-0000000062" } })
local c1 = bossRows(3495, "l")
check(#c1 == 2 and c1[1].id == 263438 and c1[1].o == 1 and c1[2].id == 263390 and c1[2].o == 2 and c1[1].r and c1[1].r == c1[2].r, "boss: första lootade varelserna efter vunnen fight blir kandidater i ordning, en gång var, samma fight")
lootWindow({ { item = 2589, guid = "Creature-0-4372-3065-77-263389-0000000063" } })
lootWindow({ { item = 2589, guid = "Creature-0-4372-3065-77-263466-0000000064" } })
check(#bossRows(3495, "l") == 3, "boss: högst tre kandidater per fight")
fire("ENCOUNTER_END", 3496, "Durgen Dirgehammer", 1, 5, 0)
lootWindow({ { item = 2589, guid = "Creature-0-4372-3065-77-263470-0000000065" } })
check(#bossRows(3496) == 0, "boss: förlorad fight ger inga kandidater")
fire("ENCOUNTER_END", 3494, "Plunder", 1, 5, 1)
clock = clock + 301
lootWindow({ { item = 2589, guid = "Creature-0-4372-3065-77-263397-0000000066" } })
check(#bossRows(3494, "l") == 0, "boss: loot efter tidsfönstret är ingen kandidat")
local named = false
for _, b in ipairs(db.bosses) do for _, v in pairs(b) do if v == "Faldrim Anvilmar" or v == "Magmatus" then named = true end end end
check(not named, "boss: inga namn sparas")
instance = nil
fire("ENCOUNTER_START", 2741, "Rhahk'Zor", 1, 5)
units.boss1 = { guid = "Creature-0-4372-36-77-644-0000000070" }
fire("INSTANCE_ENCOUNTER_ENGAGE_UNIT")
units.boss1 = nil
check(#bossRows(2741) == 0, "boss: utanför instans sparas ingenting")
fire("ENCOUNTER_END", 2741, "Rhahk'Zor", 1, 5, 0)

-- Kills: sedd levande som mål, sedan död med vår tagg; CanLootUnit efter en sekund
local K1 = "Creature-0-4372-0-48-113-0000000101"
units.target = { guid = K1, name = "Stonetusk Boar", level = 6, reaction = 2 }
fire("PLAYER_TARGET_CHANGED")
units.target.dead = true
lootable[K1] = false
local nk = #db.kills
fire("UNIT_HEALTH", "target")
local k = db.kills[#db.kills]
check(#db.kills == nk + 1 and k.id == 113 and k.h == T.guidTag(K1) and k.loot == false and k.t % 86400 == 0, "kill utan loot")
fire("UNIT_HEALTH", "target")
fire("PLAYER_REGEN_ENABLED")
check(#db.kills == nk + 1, "samma kill räknas en gång")
local K2 = "Creature-0-4372-0-48-113-0000000102"
units.target = { guid = K2, name = "Stonetusk Boar", level = 6 }
fire("PLAYER_TARGET_CHANGED")
units.target.dead = true
lootable[K2] = true
fire("PLAYER_REGEN_ENABLED")
check(#db.kills == nk + 2 and db.kills[#db.kills].loot == true, "kill med loot (lämnar strid)")
local K3 = "Creature-0-4372-0-48-113-0000000103"
units.target = { guid = K3, name = "Stonetusk Boar", level = 6 }
fire("PLAYER_TARGET_CHANGED")
units.target.dead, units.target.tapDenied = true, true
fire("UNIT_HEALTH", "target")
units.target = { guid = "Creature-0-4372-0-48-113-0000000104", name = "Stonetusk Boar", dead = true }
fire("PLAYER_TARGET_CHANGED")
check(#db.kills == nk + 2, "ingen kill: någon annans tagg, eller en kropp vi aldrig sett levande")
units.target = { guid = "Player-1-0003", name = "Carol", isPlayer = true }
fire("PLAYER_TARGET_CHANGED")
units.target.dead = true
fire("UNIT_HEALTH", "target")
check(#db.kills == nk + 2, "spelare räknas aldrig som kill")

-- Exakta mobplatser och HP/mana per nivå
pos.m, pos.x, pos.y = 1429, 0.25, 0.55
local KOB = "Creature-0-4372-0-48-6-0000000201"
units.target = { guid = KOB, name = "Kobold Vermin", level = 2, class = "normal", ctype = "Humanoid", ctypeId = 7, near = true, hpmax = 55, ptype = 0, mpmax = 80 }
fire("PLAYER_TARGET_CHANGED")
local kob = db.mobs[6]
check(kob and kob.e and #kob.e == 1 and kob.e[1].m == 1429 and kob.e[1].x == 25 and kob.e[1].t % 86400 == 0, "exakt plats inom interaktionsavstånd")
check(kob and kob.hp and kob.hp[2] == 55 and kob.mp and kob.mp[2] == 80 and type(kob.hq) == "number", "HP och mana per nivå")
units.target = { guid = "Creature-0-4372-0-48-6-0000000202", name = "Kobold Vermin", level = 3, near = true, tapDenied = true, hpmax = 100, ptype = 1, mpmax = 100 }
pos.x = 0.40
fire("PLAYER_TARGET_CHANGED")
check(#kob.e == 1, "ingen exakt plats när någon annan har taggat moben")
check(kob.hp[3] == 100 and kob.mp[3] == nil, "100 HP på nivå 3 godtas; ilska/energi sparas inte")
units.target = { guid = "Creature-0-4372-0-48-6-0000000203", name = "Kobold Vermin", level = 9, hpmax = 100 }
fire("PLAYER_TARGET_CHANGED")
check(kob.hp[9] == nil, "100 HP över nivå 5 = procent, sparas inte")
units.npc = { guid = "Creature-0-1-2-3-1294-0000000001", name = "Aldric Moore", level = 25, reaction = 5, hpmax = 1342 }
fire("GOSSIP_SHOW")
check(db.npcs[1294] and db.npcs[1294].hp and db.npcs[1294].hp[25] == 1342, "NPC: HP per nivå")

-- Berusad: ingen nivå
DRUNK_MESSAGE_SELF1, DRUNK_MESSAGE_SELF2 = "You feel sober again.", "You feel tipsy."
DRUNK_MESSAGE_SELF3, DRUNK_MESSAGE_SELF4 = "You feel drunk.  Whoa!", "You feel completely smashed."
fire("CHAT_MSG_SYSTEM", "You feel drunk.  Whoa!")
check(T.drunk() == true, "berusad efter systemmeddelandet")
clock = clock + 300
units.target = { guid = "Creature-0-4372-0-48-6-0000000204", name = "Kobold Vermin", level = 40, near = true, hpmax = 9000 }
fire("PLAYER_TARGET_CHANGED")
check(kob.lmax == 9 and kob.hp[40] == nil, "berusad: ingen nivå och ingen HP per nivå")
fire("CHAT_MSG_SYSTEM", "Bob has come online.")
check(T.drunk() == true, "andra systemmeddelanden ändrar ingenting")
fire("CHAT_MSG_SYSTEM", "You feel sober again.")
check(T.drunk() == false, "nykter igen")

-- Försäljare: alla varor (filtret återställs), kostnadssträng, rykte
local merchantFilter, filterCalls = 2, 0
LE_LOOT_FILTER_ALL = 1
function GetMerchantFilter() return merchantFilter end
function SetMerchantFilter(f) merchantFilter = f; filterCalls = filterCalls + 1 end
local wares = {
  { id = 2589, price = 13, ext = false },
  { id = 20558, price = 0, ext = true },
}
function GetMerchantNumItems() return merchantFilter == 1 and #wares or 1 end
function GetMerchantItemID(i) return wares[i] and wares[i].id end
function GetMerchantItemInfo(i) local w = wares[i]; return "x", 1, w.price, 1, -1, true, true, w.ext end
function GetMerchantItemCostInfo(i) return wares[i].ext and 2 or 0 end
function GetMerchantItemCostItem(i, j)
  if j == 1 then return 2, 10, "|Hcurrency:392|h" end   -- valutan först: strängen ska ändå sorteras
  return 1, 3, "|Hitem:20558:|h"
end
check(T.costList(2) == "20558:3,c392:10", "kostnad som sorterad sträng")
units.npc = { guid = "Creature-0-1-2-3-12778-0000000001", name = "Lieutenant Rachel Vaccar", level = 61, reaction = 6 }
fire("MERCHANT_SHOW")
local vend = db.vendors[12778]
check(vend and #vend.items == 2 and vend.items[2].ext == "20558:3,c392:10" and vend.rr == 6, "alla varor, ext-sträng och rykte")
check(merchantFilter == 2 and filterCalls == 2, "spelarens filter återställs")
fire("MERCHANT_UPDATE")
check(T.session.vendorScans == 1, "vår egen filterändring ger ingen ny skanning")
fire("MERCHANT_CLOSED")

-- Quest: XP och pengar med spelarens nivå
units.player = { level = 12 }
function GetQuestID() return 33 end
function GetTitleText() return "Wolves Across the Border" end
function GetObjectiveText() return "Bring 8 Tough Wolf Meat." end
function GetNumQuestRewards() return 0 end
function GetNumQuestChoices() return 0 end
function GetRewardMoney() return 75 end
function GetRewardXP() return 210 end
units.npc = { guid = "Creature-0-1-2-3-196-0000000001", name = "Eagan Peltskinner", level = 5, reaction = 5 }
fire("QUEST_DETAIL")
local q33 = db.quests[33]
check(q33 and q33.rewards.xp == 210 and q33.rewards.pl == 12, "questbelöning med spelarens nivå")
units.player = nil

-- Flygkostnader (TaxiNodeCost) och flygmästarens rykte
Enum.FlightPathState = { Current = 0, Reachable = 1, Unreachable = 2 }
function GetTaxiMapID() return 1429 end
C_TaxiMap = { GetAllTaxiNodes = function()
  return {
    { nodeID = 2, name = "Stormwind, Elwynn", state = 0, slotIndex = 1 },
    { nodeID = 4, name = "Sentinel Hill, Westfall", state = 1, slotIndex = 2 },
    { nodeID = 6, name = "Ironforge, Dun Morogh", state = 2, slotIndex = 3 },
  }
end }
function TaxiNodeCost(slot) return ({ 0, 50, 999 })[slot] end
units.npc = { guid = "Creature-0-1-2-3-352-0000000001", name = "Dungar Longdrink", level = 55, reaction = 5 }
pos.m, pos.x, pos.y = 1453, 0.66, 0.62
fire("TAXIMAP_OPENED")
local fl = db.flights[2]
check(fl and fl.c and fl.c[4] == 50 and fl.c[6] == nil and fl.rr == 5, "flygkostnad per nåbar nod")
local fq = fl and fl.q
fire("TAXIMAP_OPENED")
check(db.flights[2].q == fq, "oförändrade kostnader ger ingen ny seq")

-- Roller: värdshusvärd, stridsmästare, talangåterställning, vapenrock
units.npc = { guid = "Creature-0-1-2-3-6740-0000000001", name = "Innkeeper Allison", level = 30, reaction = 5 }
fire("CONFIRM_BINDER")
check(db.npcs[6740].roles.innkeeper == true, "värdshusvärd via CONFIRM_BINDER")
units.npc = { guid = "Creature-0-1-2-3-14981-0000000001", name = "Elfarran", level = 61, reaction = 5 }
fire("BATTLEFIELDS_SHOW")
check(db.npcs[14981].roles.battlemaster == true, "stridsmästare")
units.npc = { guid = "Creature-0-1-2-3-5497-0000000001", name = "Jennea Cannon", level = 50, reaction = 5 }
fire("CONFIRM_TALENT_WIPE", 10000)
check(db.npcs[5497].roles.talent == true, "talangåterställning")
units.npc = { guid = "Creature-0-1-2-3-5049-0000000001", name = "Lyesa Steelbrow", level = 45, reaction = 5 }
fire("OPEN_TABARD_FRAME")
check(db.npcs[5049].roles.tabard == true, "vapenrocksförsäljare")
Enum.PlayerInteractionType.Binder = 9
units.npc = { guid = "Creature-0-1-2-3-6741-0000000001", name = "Innkeeper Norman", level = 30, reaction = 5 }
fire("PLAYER_INTERACTION_MANAGER_FRAME_SHOW", 9)
check(db.npcs[6741].roles.innkeeper == true, "värdshusvärd via PLAYER_INTERACTION_MANAGER_FRAME_SHOW")

-- Läsbara objekt och soft interact
pos.m, pos.x, pos.y = 1429, 0.45, 0.45
units.npc = { guid = "GameObject-0-1-2-3-175730-0000000001", name = "Warning Sign" }
fire("ITEM_TEXT_BEGIN")
check(db.objects[175730] and db.objects[175730].n == "Warning Sign" and #db.objects[175730].spots == 1, "läsbart objekt")
units.npc = nil
fire("ITEM_TEXT_BEGIN")
local VEIN = "GameObject-0-1-2-3-1731-0000000009"
units.softinteract = { guid = VEIN, name = "Copper Vein" }
pos.x = 0.12
fire("PLAYER_SOFT_INTERACT_CHANGED", nil, VEIN)
check(db.objects[1731] and #db.objects[1731].spots == 1 and db.objects[1731].n == "Copper Vein", "soft interact: objektets plats")
fire("PLAYER_SOFT_INTERACT_CHANGED", nil, "Creature-0-1-2-3-299-0000000077")
check(db.mobs[299].n == "Diseased Young Wolf", "soft interact: varelser ignoreras")
combat = true
pos.x = 0.8
fire("PLAYER_SOFT_INTERACT_CHANGED", nil, "GameObject-0-1-2-3-1732-0000000001")
combat = false
check(db.objects[1732] == nil, "soft interact: inget i strid")

for _, e in ipairs({ "UNIT_HEALTH", "ENCOUNTER_END", "ENCOUNTER_START", "INSTANCE_ENCOUNTER_ENGAGE_UNIT", "CHAT_MSG_SYSTEM", "CONFIRM_BINDER", "BATTLEFIELDS_SHOW", "CONFIRM_TALENT_WIPE",
                     "OPEN_TABARD_FRAME", "ITEM_TEXT_BEGIN", "PLAYER_SOFT_INTERACT_CHANGED", "PLAYER_REGEN_ENABLED" }) do
  check(frame.events[e], "händelsen registrerad: " .. e)
end

-- Integritet: inga GUID:er och inga spelarnamn någonstans i filen
local function noGuids(t, seen)
  seen = seen or {}
  if seen[t] then return true end
  seen[t] = true
  for k, v in pairs(t) do
    for _, x in ipairs({ k, v }) do
      if type(x) == "string" and (x:find("Creature%-") or x:find("GameObject%-") or x:find("Item%-") or x:find("Player%-")
         or x == "Carol" or x == "Lucifron" or x == "Runed Copper Ring" or x:find("drunk")) then return false end
    end
    if type(v) == "table" and not noGuids(v, seen) then return false end
  end
  return true
end
check(noGuids(db), "inga GUID:er, spelarnamn, bossnamn eller chattext i ForeverScoutDB")
check(scan(db), "inga spelar- eller husdjursnamn i ForeverScoutDB (1.0.5)")
SlashCmdList.FOREVERSCOUT("")
check(T.session.errors == 0, "1.0.5 utan fångade fel (" .. tostring(T.session.lastError) .. ")")

print(string.format("ForeverScout mock: %d ok, %d fel", passes, fails))
if fails > 0 then error("ForeverScout mock: " .. fails .. " fel") end
