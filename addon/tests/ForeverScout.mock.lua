-- Offlinetest av ForeverScout med påhittade WoW-API:er (ingen spelklient, ingen server). Kör från repots rot:
--   lua5.1 client/addon/tests/ForeverScout.mock.lua
-- (eller via Pythons lupa: lupa.lua51). Ligger utanför addonmappen så att den aldrig följer med i zip-filen.
-- Testar 1.0.4: L13 (objektnamn bara från Opening/insamling, aldrig en spelare), mobs via mål/muspekare
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
function IsInInstance() return false end
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
check(db.addon == "1.0.4", "addonversion 1.0.4")
check(type(db.mobs) == "table" and type(db.mail) == "table", "mobs och mail finns")
for _, e in ipairs({ "PLAYER_TARGET_CHANGED", "UPDATE_MOUSEOVER_UNIT", "MAIL_SHOW", "AUCTION_HOUSE_SHOW", "PLAYER_INTERACTION_MANAGER_FRAME_SHOW" }) do
  check(frame.events[e], "händelsen registrerad: " .. e)
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

print(string.format("ForeverScout mock: %d ok, %d fel", passes, fails))
if fails > 0 then error("ForeverScout mock: " .. fails .. " fel") end
