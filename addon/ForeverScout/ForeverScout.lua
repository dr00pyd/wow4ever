-- ForeverScout – collects world data for guide.wow4ever.se while you play.
-- Format: docs/briefs/SCOUT-KONTRAKT.md §1 (ForeverScoutDB, v = 1).
--
-- Design rules:
--  * Never show an error to the player: every event handler runs in pcall, every API is feature-detected.
--  * Midnight/Forever "secret values": every value read from the game passes clean() before it is
--    compared or stored. Secret = treated as missing.
--  * No OnUpdate. Work happens only on interaction events; re-scans are debounced with C_Timer.
--  * Never stored: character name, realm, account, full GUIDs (only the creature/object id, field 6, and from
--    1.0.5 a short non-reversible tag of a corpse GUID, see guidTag). Nothing about other players: target/mouseover
--    only records creatures that no player controls.
--  * Never a position inside instances. From 1.0.7 only WHICH instance (its map ID) goes with loot, kills and mobs
--    seen there, so the site can list an instance's mobs and boss loot (instanceMap).
local ADDON, ns = ...
ns = ns or {}

local VERSION = "1.0.8"
local FORMAT = 1
local MAX_SPOTS = 50          -- per NPC / object, oldest dropped
local MAX_LOOT = 2000         -- loot entries, oldest dropped
local SPOT_RADIUS = 0.5       -- map-% : closer than this to a recent spot = same spot
local SPOT_REPEAT = 3600      -- seconds before the same spot may be recorded again
local NAME_WINDOW = 15        -- seconds a UNIT_SPELLCAST_SENT target name is trusted for an object
local GATHER_WINDOW = 3       -- seconds a successful Skinning/Mining/Herb Gathering/Fishing cast marks the next loot
local OBJ_RETRIES = 20        -- times a quest objective without an item name is re-read (throttled, see below)
-- 1.0.4: mobs seen on target/mouseover (approximate area = the player's own position, never the mob's).
local MAX_MOBS = 3000         -- mobs in the file; new mobs are ignored above this (existing ones still update)
local MOB_AREAS = 8           -- approximate spots per mob; full = no more (keeps seq and the file stable)
local MOB_RADIUS = 5          -- map-% : a sighting this close to a saved area (same map) is the same area
local MOB_REPEAT = 120        -- seconds before the same GUID is looked at again (memory only, never saved)
local MOB_GUIDS = 2000        -- remembered GUIDs before the memory is cleared
local MOB_INSTANCES = 4       -- 1.0.7: instance map IDs per mob
local MAX_MAIL = 300          -- mailbox spots, oldest dropped
local MAIL_RADIUS = 1         -- map-% : a mailbox this close to a saved one (same map) is the same mailbox
-- 1.0.5
local MAX_KILLS = 3000        -- kill rows, oldest dropped
local KILL_LOOT_DELAY = 1     -- seconds after a kill before CanLootUnit is asked (the corpse's loot is ready then)
local MOB_EXACT = 8           -- exact spots per mob (player within interact distance of it)
local EXACT_RADIUS = 1        -- map-% : an exact spot this close to a saved one (same map) is the same spot
local ENC_WINDOW = 300        -- seconds after a won ENCOUNTER_END a loot window with hidden sources may be the boss
local MAX_LATENCY = 300       -- ms : loot is not recorded above this (slot and source data can be stale)
local DRUNK_MAX = 900         -- seconds a "you feel drunk" message is trusted without a "sober again"
local TAG_SALT = "ForeverScout-1.0.5|"  -- per-version salt for guidTag (the tag is never the GUID)

local floor, abs, type, tonumber, tostring, pairs, ipairs, select, pcall =
      math.floor, math.abs, type, tonumber, tostring, pairs, ipairs, select, pcall

local db                      -- = ForeverScoutDB once loaded
local opt                     -- = ForeverScoutOpt (per character, never uploaded)
local session = {
  errors = 0, lastError = nil,
  looted = {},                -- "<source GUID>|<src>" looted this session (memory only, never saved)
  gather = nil,               -- { kind = "skin"|"mine"|"herb"|"fish"|"pick"|"dis", at = GetTime(), cast = castGUID } from UNIT_SPELLCAST_SUCCEEDED
  disItem = nil,              -- item id being disenchanted (UNIT_SPELLCAST_SENT), for a destroyed item's loot window
  alive = {}, killed = {}, killN = 0, -- GUID -> true: seen alive as target / counted as a kill (memory only, never saved)
  mobNear = {},               -- GUID -> true: an exact spot was tried for this mob (memory only)
  softSeen = {}, softN = 0,   -- GUID -> GetTime() of the last soft-interact look at an object (memory only)
  encounter = nil,            -- { id, name, at } of the last won ENCOUNTER_END (memory only; the name is never saved)
  drunkAt = nil,              -- GetTime() of the last "you feel drunk" system message
  vendorScans = 0, vendorQuietUntil = 0, vendorRR = nil,
  objPending = {},            -- questID -> retries left, for objectives that arrived without an item name
  objQueued = false,
  lastKind = nil, lastName = nil, lastTime = nil,
  vendorId = nil, trainerId = nil,
  trainerScans = 0, trainerQuietUntil = 0,
  sentName = nil, sentAt = 0,
  mobSeen = {}, mobSeenN = 0, -- GUID -> GetTime() of the last look (memory only, never saved)
  mobCount = 0,
  recipes = nil,
  resetArmed = false,
}

---------------------------------------------------------------------------
-- Safe helpers
---------------------------------------------------------------------------

local function pass(ok, ...)
  if ok then return ... end
  return nil
end

-- Calls fn(...) protected; returns its results or nil. Missing functions return nil.
local function try(fn, ...)
  if type(fn) ~= "function" then return nil end
  return pass(pcall(fn, ...))
end

-- Returns v unless it is a secret value (or the check itself fails).
local function clean(v)
  if v == nil then return nil end
  if issecretvalue then
    local ok, secret = pcall(issecretvalue, v)
    if not ok or secret then return nil end
  end
  return v
end

local function num(v)
  v = clean(v)
  if type(v) == "number" and v == v then return v end
  return nil
end

local function str(v)
  v = clean(v)
  if type(v) == "string" and v ~= "" then return v end
  return nil
end

-- true/false from a unit predicate; nil when the answer is unknown (secret value or failed call).
local function unitFlag(fn, ...)
  if type(fn) ~= "function" then return nil end
  local ok, v = pcall(fn, ...)
  if not ok then return nil end
  if v ~= nil and issecretvalue then
    local ok2, secret = pcall(issecretvalue, v)
    if not ok2 or secret then return nil end
  end
  return v and true or false
end

local function round2(v)
  return floor(v * 100 + 0.5) / 100
end

local function now()
  return num(try(time)) or 0
end

local function nextSeq()
  db.seq = (tonumber(db.seq) or 0) + 1
  return db.seq
end

local function itemIdFromLink(link)
  link = str(link)
  if not link then return nil end
  return tonumber(link:match("item:(%d+)"))
end

-- 1.0.5: random suffix id of an item link ("item:id:enchant:gem1:gem2:gem3:gem4:suffix:unique:…" – field 7).
-- Only the suffix is read; the unique id (field 8) identifies one copy of the item and is never touched.
local function suffixFromLink(link)
  link = str(link)
  local body = link and link:match("item:([%-%d:]*)")
  if not body then return nil end
  local i = 0
  for f in (body .. ":"):gmatch("([^:]*):") do
    i = i + 1
    if i == 7 then
      local s = tonumber(f)
      if s and s ~= 0 then return s end
      return nil
    end
  end
  return nil
end

-- 1.0.5: a short tag for a corpse/chest GUID, so that the server can count one corpse once when every group member
-- uploads it. 32-bit polynomial hash of a per-version salt + the GUID, as 8 hex digits. The GUID carries far more
-- than 32 bits (server, instance, zone and spawn uid), so countless GUIDs share each tag: it cannot be turned back
-- into the GUID. The GUID itself is never saved.
local function guidTag(g)
  g = str(g)
  if not g then return nil end
  local s = TAG_SALT .. g
  local h = 5381
  for i = 1, #s do h = (h * 33 + s:byte(i)) % 4294967296 end
  return string.format("%08x", h)
end

-- In a party or raid? (IsInGroup on Mainline, the party/raid counters on older clients.) Unknown = false.
local function inGroup()
  if IsInGroup then return unitFlag(IsInGroup) == true end
  local n = num(try(GetNumGroupMembers)) or ((num(try(GetNumPartyMembers)) or 0) + (num(try(GetNumRaidMembers)) or 0))
  return n > 0
end

-- Latency above MAX_LATENCY ms (home or world)? Then loot slots can still show stale data – the window is skipped.
local function laggy()
  local _, _, home, world = try(GetNetStats)
  home, world = num(home) or 0, num(world) or 0
  return home > MAX_LATENCY or world > MAX_LATENCY
end

-- Drunk (a "you feel tipsy/drunk/smashed" system message in the last DRUNK_MAX seconds, no "sober again" since)?
-- Being drunk changes the levels the client shows, so no level is recorded then.
local function drunk()
  local at = session.drunkAt
  return at ~= nil and (num(try(GetTime)) or 0) - at < DRUNK_MAX
end

local function splitGuid(g)
  if strsplit then return strsplit("-", g) end
  local parts, i = {}, 0
  for p in (g .. "-"):gmatch("([^-]*)-") do i = i + 1; parts[i] = p end
  return parts[1], parts[2], parts[3], parts[4], parts[5], parts[6], parts[7]
end

-- GUID -> kind ("npc" | "object" | "item"), id. Only field 6 is ever used; the GUID itself is not stored.
local function parseGuid(g)
  g = str(g)
  if not g then return nil end
  local kind, _, _, _, _, id = splitGuid(g)
  if kind == "Creature" or kind == "Vehicle" then
    id = tonumber(id); if id then return "npc", id end
  elseif kind == "GameObject" then
    id = tonumber(id); if id then return "object", id end
  elseif kind == "Item" then
    local itemId = C_Item and num(try(C_Item.GetItemIDByGUID, g))
    if itemId then return "item", itemId end
  end
  return nil
end

local function after(sec, fn)
  if C_Timer and C_Timer.After then
    try(C_Timer.After, sec, function() try(fn) end)
  else
    try(fn)
  end
end

local function touch(kind, name)
  session.lastKind, session.lastName, session.lastTime = kind, name, now()
end

-- Cheap, stable signature of a list of flat tables (used to avoid bumping seq when nothing changed).
local function signature(list, keys)
  local out, n = {}, 0
  for _, row in ipairs(list) do
    for _, k in ipairs(keys) do
      local v = row[k]
      if type(v) == "table" then
        for _, e in ipairs(v) do n = n + 1; out[n] = tostring(e.item or e.currency) .. "x" .. tostring(e.c) end
      else
        n = n + 1; out[n] = tostring(v)
      end
    end
    n = n + 1; out[n] = ";"
  end
  return table.concat(out, ",")
end

---------------------------------------------------------------------------
-- Position (never inside instances, on taxis or in vehicles)
---------------------------------------------------------------------------

local function positionAllowed()
  if clean(try(IsInInstance)) then return false end
  if clean(try(UnitOnTaxi, "player")) then return false end
  if clean(try(UnitInVehicle, "player")) then return false end
  return true
end

-- 1.0.7: the instance's map ID (8th value of GetInstanceInfo) inside a dungeon, raid or battleground, else nil.
-- Only which instance – never where in it.
local function instanceMap()
  if not clean(try(IsInInstance)) then return nil end
  local info = { try(GetInstanceInfo) }
  local id = num(info[8])
  if id and id > 0 and id == floor(id) then return id end
  return nil
end

local function playerPos()
  if not positionAllowed() then return nil end
  if not (C_Map and C_Map.GetBestMapForUnit and C_Map.GetPlayerMapPosition) then return nil end
  local m = num(try(C_Map.GetBestMapForUnit, "player"))
  if not m then return nil end
  local pos = try(C_Map.GetPlayerMapPosition, m, "player")
  if type(pos) ~= "table" then return nil end
  local x, y
  if type(pos.GetXY) == "function" then
    x, y = try(pos.GetXY, pos)
  else
    x, y = pos.x, pos.y
  end
  x, y = num(x), num(y)
  if not x or not y or (x == 0 and y == 0) or x < 0 or x > 1 or y < 0 or y > 1 then return nil end
  return m, round2(x * 100), round2(y * 100)
end

-- Adds the player's current position to rec.spots unless an equivalent recent spot exists.
local function addSpot(rec)
  local m, x, y = playerPos()
  if not m then return false end
  local t = now()
  rec.spots = rec.spots or {}
  local spots = rec.spots
  for i = #spots, 1, -1 do
    local s = spots[i]
    if s.m == m and abs((s.x or 0) - x) < SPOT_RADIUS and abs((s.y or 0) - y) < SPOT_RADIUS
       and t - (s.t or 0) < SPOT_REPEAT then
      return false
    end
  end
  spots[#spots + 1] = { m = m, x = x, y = y, t = t, q = nextSeq() }
  while #spots > MAX_SPOTS do table.remove(spots, 1) end
  return true
end

---------------------------------------------------------------------------
-- NPCs and objects
---------------------------------------------------------------------------

local function reactionWord(r)
  r = num(r)
  if not r then return nil end
  if r <= 3 then return "hostile" elseif r == 4 then return "neutral" end
  return "friendly"
end

-- NPC subtitle ("Mage Trainer") = tooltip line 2 when it is not the level line.
local function subtitle(unit)
  if not (C_TooltipInfo and C_TooltipInfo.GetUnit) then return nil end
  local data = try(C_TooltipInfo.GetUnit, unit)
  if type(data) ~= "table" or type(data.lines) ~= "table" then return nil end
  local line = data.lines[2]
  local txt = type(line) == "table" and str(line.leftText)
  if not txt then return nil end
  if txt:find("%d") or txt:find("%?%?") then return nil end
  local levelWord = str(LEVEL)
  if levelWord and txt:find(levelWord, 1, true) then return nil end
  txt = (txt:gsub("^<", ""))
  txt = (txt:gsub(">$", ""))
  return txt
end

-- 1.0.5: max health and max mana per level (rec.hp / rec.mp = { [level] = value }). Nothing when the level is
-- unknown, a value is secret or the player is drunk. rec.hq = seq of the last change, so the change is uploaded.
local function noteStats(rec, unit, l)
  if not l or l < 1 or drunk() then return false end
  local changed = false
  local hp = num(try(UnitHealthMax, unit))
  -- A health shown as a percentage (exactly 100 above level 5) is not the real value.
  if hp and hp > 0 and hp < 10000000 and not (hp == 100 and l > 5) then
    rec.hp = rec.hp or {}
    if rec.hp[l] ~= hp then rec.hp[l] = hp; changed = true end
  end
  if num(try(UnitPowerType, unit)) == 0 then   -- mana only (rage/energy maxima say nothing about the NPC)
    local mp = num(try(UnitPowerMax, unit, 0))
    if mp and mp > 0 and mp < 10000000 then
      rec.mp = rec.mp or {}
      if rec.mp[l] ~= mp then rec.mp[l] = mp; changed = true end
    end
  end
  if changed then rec.hq = nextSeq() end
  return changed
end

local function noteObject(id, name)
  local rec = db.objects[id]
  if not rec then rec = {}; db.objects[id] = rec end
  if name then rec.n = name end
  local added = addSpot(rec)
  touch("object", rec.n or tostring(id))
  return rec, added
end

-- Records the unit we are interacting with ("npc"). Returns kind, id.
local function noteUnit(unit, roles)
  local kind, id = parseGuid(try(UnitGUID, unit))
  if kind == "npc" then
    local rec = db.npcs[id]
    if not rec then rec = {}; db.npcs[id] = rec end
    rec.n = str(try(UnitName, unit)) or rec.n
    local s = subtitle(unit)
    if s then rec.s = s end
    local l = not drunk() and num(try(UnitLevel, unit)) or nil
    if l and l > 0 then rec.l = l end
    noteStats(rec, unit, l)
    rec.r = reactionWord(try(UnitReaction, unit, "player")) or rec.r
    rec.pf = str(try(UnitFactionGroup, "player")) or rec.pf
    rec.roles = rec.roles or {}
    if roles then for k, v in pairs(roles) do if v then rec.roles[k] = true end end end
    addSpot(rec)
    touch("npc", rec.n or tostring(id))
    return kind, id
  elseif kind == "object" then
    noteObject(id, str(try(UnitName, unit)))
    return kind, id
  end
  return nil
end

---------------------------------------------------------------------------
-- Gossip, bank, quest greeting
---------------------------------------------------------------------------

local ICON_ROLE = {
  [132060] = "vendor",     -- VendorGossipIcon
  [132058] = "trainer",    -- TrainerGossipIcon
  [132057] = "flight",     -- TaxiGossipIcon
  [132052] = "innkeeper",  -- BinderGossipIcon
}

local function onGossip()
  local roles = { gossip = true }
  local options = C_GossipInfo and try(C_GossipInfo.GetOptions)
  if type(options) == "table" then
    for _, o in ipairs(options) do
      if type(o) == "table" then
        local role = ICON_ROLE[num(o.overrideIconID) or 0] or ICON_ROLE[num(o.icon) or 0]
        if role then roles[role] = true end
      end
    end
  end
  if C_GossipInfo then
    local a = num(try(C_GossipInfo.GetNumAvailableQuests)) or 0
    local b = num(try(C_GossipInfo.GetNumActiveQuests)) or 0
    if a > 0 or b > 0 then roles.quest = true end
  end
  noteUnit("npc", roles)
end

---------------------------------------------------------------------------
-- Vendors
---------------------------------------------------------------------------

-- Alternative cost as one compact, sorted string (1.0.5; 1.0.4 saved a table the server could not read):
-- "<itemId>:<count>" for an item, "c<currencyId>:<count>" for a currency, comma-separated, e.g. "c392:10,20558:3".
local function costList(i)
  local n = num(try(GetMerchantItemCostInfo, i)) or 0
  if n <= 0 then return nil end
  local parts = {}
  for j = 1, n do
    local _, value, link = try(GetMerchantItemCostItem, i, j)
    link = str(link)
    value = num(value)
    if link and value and value > 0 then
      local item = tonumber(link:match("item:(%d+)"))
      local cur = tonumber(link:match("currency:(%d+)"))
      if item then parts[#parts + 1] = item .. ":" .. floor(value)
      elseif cur then parts[#parts + 1] = "c" .. cur .. ":" .. floor(value) end
    end
  end
  if #parts == 0 then return nil end
  table.sort(parts)
  return table.concat(parts, ",")
end

-- 1.0.5: shows every item (Mainline's merchant filter, LE_LOOT_FILTER_ALL) while fn runs, then restores the
-- player's filter. Our own filter changes fire MERCHANT_UPDATE; those are ignored (vendorQuietUntil).
local function withAllMerchantItems(fn)
  local all = type(LE_LOOT_FILTER_ALL) == "number" and LE_LOOT_FILTER_ALL or nil
  local saved = all and GetMerchantFilter and SetMerchantFilter and num(try(GetMerchantFilter)) or nil
  if saved == all then saved = nil end
  if saved then
    session.vendorQuietUntil = (num(try(GetTime)) or 0) + 1
    try(SetMerchantFilter, all)
  end
  try(fn)
  if saved then
    try(SetMerchantFilter, saved)
    session.vendorQuietUntil = (num(try(GetTime)) or 0) + 1
  end
end

local function scanVendor()
  local id = session.vendorId
  if not id then return end
  local items = {}
  withAllMerchantItems(function()
    local n = num(try(GetMerchantNumItems)) or 0
    for i = 1, n do
      local itemId = num(try(GetMerchantItemID, i)) or itemIdFromLink(try(GetMerchantItemLink, i))
      if itemId then
        local price, qty, avail, hasExt
        local info = C_MerchantFrame and try(C_MerchantFrame.GetItemInfo, i)
        if type(info) == "table" then
          price, qty, avail, hasExt = num(info.price), num(info.stackCount), num(info.numAvailable), clean(info.hasExtendedCost)
        else
          local _, _, p, q, a, _, _, e = try(GetMerchantItemInfo, i)
          price, qty, avail, hasExt = num(p), num(q), num(a), clean(e)
        end
        local row = { id = itemId, price = price or 0, qty = qty or 1, limited = (avail and avail >= 0) and avail or -1 }
        if hasExt then row.ext = costList(i) end
        items[#items + 1] = row
      end
    end
  end)
  if #items == 0 then return end
  local old = db.vendors[id]
  local sig = signature(items, { "id", "price", "qty", "ext" }) .. "|" .. tostring(session.vendorRR)
  if old and old.items then
    -- Never replace a fuller list with one that is still loading.
    if #items < #old.items and now() - (old.t or 0) < 7 * 86400 then return end
    if signature(old.items, { "id", "price", "qty", "ext" }) .. "|" .. tostring(old.rr) == sig then return end
  end
  -- rr = the vendor's UnitReaction toward the player (1–8; 6+ = honored or better, prices may carry a reputation
  -- discount). The server prefers prices seen without a discount.
  db.vendors[id] = { q = nextSeq(), t = now(), rr = session.vendorRR, items = items }
  touch("vendor", (db.npcs[id] and db.npcs[id].n) or tostring(id))
end

local vendorPending = false
local function queueVendorScan()
  if vendorPending or session.vendorScans >= 6 then return end
  vendorPending = true
  after(0.5, function()
    vendorPending = false
    session.vendorScans = session.vendorScans + 1
    scanVendor()
  end)
end

local function onMerchantShow()
  local roles = { vendor = true }
  if clean(try(CanMerchantRepair)) then roles.repair = true end
  local kind, id = noteUnit("npc", roles)
  session.vendorId = (kind == "npc") and id or nil
  session.vendorRR = num(try(UnitReaction, "npc", "player"))
  session.vendorScans = 0
  queueVendorScan()
end

local function onMerchantUpdate()
  if not session.vendorId then return end
  if (num(try(GetTime)) or 0) < session.vendorQuietUntil then return end
  queueVendorScan()
end

---------------------------------------------------------------------------
-- Trainers
---------------------------------------------------------------------------

local SERVICE_TYPES = { available = true, unavailable = true, used = true, header = true }
local FILTERS = { "available", "unavailable", "used" }

local function trainerKind(id)
  local tt = C_Trainer and num(try(C_Trainer.GetTrainerType))
  if tt == 3 then return "pet" end
  if tt == 2 or clean(try(IsTradeskillTrainer)) then return "profession" end
  local s = db.npcs[id] and db.npcs[id].s
  if s then
    if s:find("Weapon") then return "weapon" end
    if s:find("Riding") then return "riding" end
  end
  return "class"
end

local function readService(i)
  local a, b, c, d, e = try(GetTrainerServiceInfo, i)
  local name, stype, reqLevel, rank
  if type(b) == "string" and SERVICE_TYPES[b] then
    -- Mainline/Forever order: name, type, texture, reqLevel, subText
    name, stype, reqLevel, rank = a, b, d, e
  else
    -- Classic order: name, subText, type, expanded
    name, rank, stype = a, b, c
    reqLevel = try(GetTrainerServiceLevelReq, i)
  end
  name, stype = str(name), clean(stype)
  if not name or stype == "header" then return nil end
  local svc = { name = name, rank = str(rank), lvl = num(reqLevel), cost = num((try(GetTrainerServiceCost, i))) or 0 }
  if C_TooltipInfo and C_TooltipInfo.GetTrainerService then
    local data = try(C_TooltipInfo.GetTrainerService, i)
    if type(data) == "table" then svc.spell = num(data.id) end
  end
  if not svc.spell then
    local link = str(try(GetTrainerServiceItemLink, i))
    if link then svc.spell = tonumber(link:match("spell:(%d+)")) end
  end
  local skill, skillRank = try(GetTrainerServiceSkillReq, i)
  skill, skillRank = str(skill), num(skillRank)
  if skill and skillRank and skillRank > 0 then svc.skill = skill; svc.reqSkill = skillRank end
  return svc
end

local function scanTrainer()
  local id = session.trainerId
  if not id then return end
  -- Our own filter changes fire TRAINER_UPDATE; ignore those.
  session.trainerQuietUntil = (num(try(GetTime)) or 0) + 1
  -- Show every service (available, unavailable, used), then restore the player's filter.
  local saved = {}
  for _, f in ipairs(FILTERS) do
    saved[f] = clean(try(GetTrainerServiceTypeFilter, f))
    try(SetTrainerServiceTypeFilter, f, true)
  end
  local services = {}
  local n = num(try(GetNumTrainerServices)) or 0
  for i = 1, n do
    local svc = readService(i)
    if svc then services[#services + 1] = svc end
  end
  for _, f in ipairs(FILTERS) do
    if saved[f] ~= nil then try(SetTrainerServiceTypeFilter, f, saved[f] and true or false) end
  end
  session.trainerQuietUntil = (num(try(GetTime)) or 0) + 1
  if #services == 0 then return end
  local keys = { "spell", "name", "rank", "cost", "lvl", "skill", "reqSkill" }
  local old = db.trainers[id]
  if old and old.services then
    if #services < #old.services and now() - (old.t or 0) < 7 * 86400 then return end
    if signature(old.services, keys) == signature(services, keys) then return end
  end
  db.trainers[id] = { q = nextSeq(), t = now(), kind = trainerKind(id), services = services }
  touch("trainer", (db.npcs[id] and db.npcs[id].n) or tostring(id))
end

local trainerPending = false
local function queueTrainerScan()
  if trainerPending or session.trainerScans >= 4 then return end
  trainerPending = true
  after(0.3, function()
    trainerPending = false
    session.trainerScans = session.trainerScans + 1
    scanTrainer()
  end)
end

local function onTrainerShow()
  local kind, id = noteUnit("npc", { trainer = true })
  session.trainerId = (kind == "npc") and id or nil
  session.trainerScans = 0
  queueTrainerScan()
end

local function onTrainerUpdate()
  if not session.trainerId then return end
  if (num(try(GetTime)) or 0) < session.trainerQuietUntil then return end
  queueTrainerScan()
end

---------------------------------------------------------------------------
-- Quests
---------------------------------------------------------------------------

local function questRewards()
  local r = { items = {}, choice = {} }
  for i = 1, (num(try(GetNumQuestRewards)) or 0) do
    local id = itemIdFromLink(try(GetQuestItemLink, "reward", i))
    if id then r.items[#r.items + 1] = id end
  end
  for i = 1, (num(try(GetNumQuestChoices)) or 0) do
    local id = itemIdFromLink(try(GetQuestItemLink, "choice", i))
    if id then r.choice[#r.choice + 1] = id end
  end
  r.money = num(try(GetRewardMoney)) or 0
  r.xp = num(try(GetRewardXP)) or 0
  -- 1.0.5: XP and money depend on the player's level (less XP far above the quest, money instead of XP at the
  -- level cap) – the level is saved with them so the server can keep the representative values.
  local pl = num(try(UnitLevel, "player"))
  if pl and pl > 0 then r.pl = pl end
  return r
end

local function questSig(rec)
  local g, e, rw = rec.giver or {}, rec.ender or {}, rec.rewards or {}
  return table.concat({
    tostring(rec.title), tostring(rec.lvl), tostring(rec.minLvl),
    tostring(g.npc), tostring(g.object), tostring(e.npc), tostring(e.object),
    tostring(rec.m), tostring(rec.x), tostring(rec.y),
    table.concat(rec.objectives or {}, "|"),
    table.concat(rw.items or {}, ","), table.concat(rw.choice or {}, ","), tostring(rw.money), tostring(rw.xp), tostring(rw.pl),
  }, "#")
end

local function questUpdate(id, fn)
  local rec = db.quests[id]
  local before = rec and questSig(rec)
  if not rec then rec = {}; db.quests[id] = rec end
  fn(rec)
  if questSig(rec) ~= before then
    rec.q = nextSeq()
    rec.t = now()
    touch("quest", rec.title or tostring(id))
  end
end

local function currentQuestId()
  local id = num(try(GetQuestID))
  if id and id > 0 then return id end
  return nil
end

local function onQuestDetail()
  local id = currentQuestId()
  if not id then return end
  local kind, who = noteUnit("npc", { quest = true })
  questUpdate(id, function(rec)
    rec.title = str(try(GetTitleText)) or rec.title
    local obj = str(try(GetObjectiveText))
    if obj and not rec.objectives then rec.objectives = { obj } end
    rec.rewards = questRewards()
    if kind == "npc" then rec.giver = { npc = who } elseif kind == "object" then rec.giver = { object = who } end
    local m, x, y = playerPos()
    if m and rec.giver then rec.m, rec.x, rec.y = m, x, y end
  end)
end

-- Questens nivå ur questloggen. Fungerar även för quests som startas från ett föremål eller en leverans
-- (t.ex. 3364), där QUEST_DETAIL saknar nivå: prova C_QuestLog.GetInfo, sedan Classic-API:t
-- GetQuestLogIndexByID/GetQuestLogTitle och sist C_QuestLog.GetQuestDifficultyLevel.
local function questLogLevel(id)
  local QL = C_QuestLog
  local idx = QL and num(try(QL.GetLogIndexForQuestID, id))
  local info = QL and idx and try(QL.GetInfo, idx)
  if type(info) == "table" then
    local lvl = num(info.level)
    if lvl and lvl > 0 then return lvl, str(info.title) end
  end
  idx = idx or num(try(GetQuestLogIndexByID, id))
  if idx and idx > 0 then
    local title, level = try(GetQuestLogTitle, idx)
    level = num(level)
    if level and level > 0 then return level, str(title) end
  end
  local lvl = QL and num(try(QL.GetQuestDifficultyLevel, id))
  if lvl and lvl > 0 then return lvl, nil end
  return nil, nil
end

-- An objective text without a name ("0/8 " – the item name has not loaded yet) is incomplete.
local function objectiveIncomplete(t)
  return t == nil or not t:find("%a") or t:match("^%s*:?%s*%d+%s*/%s*%d+%s*$") ~= nil
end

-- The quest's objective texts from the quest log: C_QuestLog.GetQuestObjectives, else the Classic leaderboard API.
local function questObjectives(id)
  local list = {}
  local objs = C_QuestLog and try(C_QuestLog.GetQuestObjectives, id)
  if type(objs) == "table" and #objs > 0 then
    for _, o in ipairs(objs) do
      local t = type(o) == "table" and (str(o.text) or "") or nil
      if t then list[#list + 1] = t end
    end
    return list
  end
  local idx = num(try(GetQuestLogIndexByID, id))
  if idx and idx > 0 then
    for i = 1, (num(try(GetNumQuestLeaderBoards, idx)) or 0) do
      list[#list + 1] = str((try(GetQuestLogLeaderBoard, i, idx))) or ""
    end
  end
  return list
end

-- Re-reads the objectives; returns true when every objective has its name. Bumps seq only if something changed.
local function refreshObjectives(id)
  local list = questObjectives(id)
  if #list == 0 then return false end
  local complete = true
  for _, t in ipairs(list) do if objectiveIncomplete(t) then complete = false end end
  local rec = db.quests[id]
  local old = rec and rec.objectives and table.concat(rec.objectives, "|") or nil
  -- Never replace complete texts with incomplete ones.
  local oldComplete = rec and rec.objectives ~= nil
  if oldComplete then for _, t in ipairs(rec.objectives) do if objectiveIncomplete(t) then oldComplete = false end end end
  if table.concat(list, "|") ~= old and (complete or not oldComplete) then
    questUpdate(id, function(r) r.objectives = list end)
  end
  return complete
end

-- Throttled retry for objectives that are still missing names (QUEST_LOG_UPDATE / ITEM_DATA_LOAD_RESULT), never OnUpdate.
local function queueObjectiveRefresh()
  if session.objQueued or next(session.objPending) == nil then return end
  session.objQueued = true
  after(1, function()
    session.objQueued = false
    for id, left in pairs(session.objPending) do
      if refreshObjectives(id) or left <= 1 then session.objPending[id] = nil else session.objPending[id] = left - 1 end
    end
  end)
end

local function onQuestAccepted(a, b)
  local id = num(b) or num(a)
  if not id or id <= 0 then return end
  questUpdate(id, function(rec)
    local lvl, title = questLogLevel(id)
    if lvl then rec.lvl = lvl end
    rec.title = title or rec.title
  end)
  if not refreshObjectives(id) then session.objPending[id] = OBJ_RETRIES end
end

local function onQuestEnd(withRewards)
  local id = currentQuestId()
  if not id then return end
  local kind, who = noteUnit("npc", { quest = true })
  questUpdate(id, function(rec)
    rec.title = str(try(GetTitleText)) or rec.title
    if kind == "npc" then rec.ender = { npc = who } elseif kind == "object" then rec.ender = { object = who } end
    if withRewards then rec.rewards = questRewards() end
  end)
end

---------------------------------------------------------------------------
-- Loot and gathered objects
---------------------------------------------------------------------------

-- Gathering spells: every spell id with these names in the Forever 1.60 client data (wago.tools SpellName, product
-- wowf), including Forever's own new ids (1235xxx/1240xxx/…). The localized name of the first id and the English
-- names are a fallback, so a later new rank/id still matches.
local GATHER_SPELLS = {
  skin = { ids = { 8613, 8617, 8618, 10768, 13697, 1240952, 1240963, 1240974, 1240987, 1248530, 1300763, 1300766 },
           en = { "Skinning" } },
  mine = { ids = { 2575, 2576, 2577, 2578, 2579, 3564, 10248, 12560, 13611, 1235230, 1240951, 1240962, 1240973, 1240986, 1264639, 1272073 },
           en = { "Mining" } },
  herb = { ids = { 2366, 2368, 2369, 2371, 3570, 9134, 11993, 13614, 1235236, 1240949, 1240960, 1240971, 1240984 },
           en = { "Herbalism", "Herb Gathering" } },
  fish = { ids = { 7620, 7731, 7732, 13615, 18248, 24303, 1229271, 1240948, 1240959, 1240970, 1240983 },
           en = { "Fishing" } },
  -- 1.0.5: Pick Pocket and Disenchant (the only ids with these names in the Forever 1.60 SpellName that a player
  -- casts; "(PT)" test spells and the passive "Improved Pick Pocket…" are left out).
  pick = { ids = { 921 }, en = { "Pick Pocket" } },
  dis = { ids = { 13262 }, en = { "Disenchant" } },
}
-- The loot source each gathering kind applies to: skinning a creature, mining/herbing a creature (ore/herb nodes are
-- objects and stay "object"), picking a creature's pocket, disenchanting an item.
local GATHER_ON = { skin = "npc", mine = "npc", herb = "npc", pick = "npc", dis = "item" }

local function spellName(id)
  if not id then return nil end
  if C_Spell and C_Spell.GetSpellName then
    local n = str(try(C_Spell.GetSpellName, id))
    if n then return n end
  end
  return str((try(GetSpellInfo, id)))
end

local gatherById, gatherByName
local function gatherKind(spellId)
  if not gatherById then
    gatherById, gatherByName = {}, {}
    for kind, def in pairs(GATHER_SPELLS) do
      for _, id in ipairs(def.ids) do gatherById[id] = kind end
      for _, en in ipairs(def.en) do gatherByName[en] = kind end
      local loc = spellName(def.ids[1])
      if loc then gatherByName[loc] = kind end
    end
  end
  local id = num(spellId)
  if id and gatherById[id] then return gatherById[id] end
  local n = spellName(id)
  return n and gatherByName[n] or nil
end

-- Spells cast on a game object to open it: "Opening"/"Opening - No Text", Forever's "Gathering" and "Pick Lock"
-- (Forever 1.60 client SpellName, wago.tools product wowf). Name fallback as for GATHER_SPELLS.
local OPEN_SPELLS = { ids = { 3365, 3366, 6247, 6249, 6477, 6478, 6658, 6802, 8917, 21248, 21288, 21651, 22810, 24390, 24391,
                              26868, 423088, 423632, 461679, 463320, 1221363, 1221732, 1234592, 1236393, 1269930, 1273960,
                              1308379, 1309310, 1309978, 1310156, 1310491, 1804, 6461, 6463 },
                      en = { "Opening", "Opening - No Text", "Gathering", "Pick Lock" } }
local openById, openByName
local function openSpell(spellId)
  if not openById then
    openById, openByName = {}, {}
    for _, id in ipairs(OPEN_SPELLS.ids) do openById[id] = true end
    for _, en in ipairs(OPEN_SPELLS.en) do openByName[en] = true end
    local loc = spellName(OPEN_SPELLS.ids[1])
    if loc then openByName[loc] = true end
  end
  local id = num(spellId)
  if id and openById[id] then return true end
  local n = spellName(id)
  return n ~= nil and openByName[n] == true
end

-- Is this name a player's (ours, the target's, the mouseover's or a group member's)? Unknown counts as yes.
local function playerName(name)
  if name == str(try(UnitName, "player")) then return true end
  for _, u in ipairs({ "target", "mouseover", "focus" }) do
    if str(try(UnitName, u)) == name and unitFlag(UnitIsPlayer, u) ~= false then return true end
  end
  -- UnitIsPlayer also takes a group member's name; false/nil = not a known player.
  return unitFlag(UnitIsPlayer, name) == true
end

-- UNIT_SPELLCAST_SENT(unit, target, castGUID, spellID). The target name is only kept for Opening and gathering
-- casts (on an object) and never when it is a player's name (säkerhetsgranskningen L13). Every new cast clears
-- the previous name, and the loot window uses it at most once.
local function onSpellcastSent(unit, target, _, spellId)
  if unit ~= "player" then return end
  session.sentName, session.sentAt = nil, 0
  local name = str(target)
  local g = gatherKind(spellId)
  if g == "dis" then
    -- The disenchanted item is destroyed, so its GUID may no longer give an id in the loot window: remember the id
    -- now (the target is the item's name; GetItemInfoInstant answers for an item in the bags).
    local f = (C_Item and C_Item.GetItemInfoInstant) or GetItemInfoInstant
    session.disItem = name and num(try(f, name)) or nil
    return
  end
  if not name or not (openSpell(spellId) or g == "mine" or g == "herb") then return end
  if playerName(name) then return end
  session.sentName = name
  session.sentAt = num(try(GetTime)) or 0
end

-- UNIT_SPELLCAST_SUCCEEDED(unit, castGUID, spellID): remember a finished gathering cast for the next LOOT_OPENED.
local function onSpellcastSucceeded(unit, castGuid, spellId)
  if unit ~= "player" then return end
  local kind = gatherKind(spellId)
  if kind then session.gather = { kind = kind, at = num(try(GetTime)) or 0, cast = str(castGuid) } end
  -- The latest fishing cast identifies the catch (reopening the same loot window must not log it twice).
  if kind == "fish" then session.fishCast = str(castGuid) or tostring(num(try(GetTime)) or 0) end
end

local function recentGather()
  local g = session.gather
  if g and (num(try(GetTime)) or 0) - g.at <= GATHER_WINDOW then return g end
  return nil
end

local function pushLoot(entry)
  entry.im = entry.im or instanceMap()   -- 1.0.7: the instance it dropped in (nil outside)
  local loot = db.loot
  loot[#loot + 1] = entry
  while #loot > MAX_LOOT do table.remove(loot, 1) end
end

-- Fishing: one entry per catch (src = "fish", id = uiMapID where it was caught, plus the position), deduped per
-- fishing cast. Counts come from GetLootSourceInfo like other loot, else GetLootSlotInfo's quantity.
local function lootFishing(n, moneyType)
  local key = "fish|" .. (session.fishCast or tostring(floor(num(try(GetTime)) or 0)))
  session.gather = nil
  if session.looted[key] then return end
  local items, money = {}, 0
  for slot = 1, n do
    local slotType = num(try(GetLootSlotType, slot))
    local srcInfo = { try(GetLootSourceInfo, slot) }
    local c = 0
    for k = 2, #srcInfo, 2 do c = c + (num(srcInfo[k]) or 0) end
    if c == 0 then local _, _, qty = try(GetLootSlotInfo, slot); c = num(qty) or 0 end
    if slotType == moneyType then
      money = money + c
    else
      local link = try(GetLootSlotLink, slot)
      local itemId = itemIdFromLink(link)
      if itemId then items[#items + 1] = { id = itemId, c = c > 0 and c or 1, s = suffixFromLink(link) } end
    end
  end
  if #items == 0 and money <= 0 then return end
  session.looted[key] = true
  local m, x, y = playerPos()
  pushLoot({ q = nextSeq(), t = now(), src = "fish", id = m or 0, m = m, x = x, y = y, items = items, money = money > 0 and money or nil,
             pf = str(try(UnitFactionGroup, "player")) })
  touch("loot", "fishing")
end

local SECRET_SOURCE = "?"   -- loot slots whose source GUID the game hides (secret value, e.g. in instances)

-- 1.0.8: defined after the mob and kill code (see "Looted in an instance" below).
local noteLootedMob

-- 1.0.5: who dropped loot whose source GUIDs are hidden? First the dead target when its GUID is readable, then the
-- boss of a won encounter (ENCOUNTER_END) in the last ENC_WINDOW seconds – once per encounter, and only if a dead
-- target with a readable name is that boss. Returns key, kind, src, id (or nil: the loot is not recorded).
local function hiddenSource()
  if unitFlag(UnitIsDead, "target") == true then
    local g = str(try(UnitGUID, "target"))
    local kind, id = parseGuid(g)
    if kind == "npc" then return g, kind, "npc", id end
  end
  local e = session.encounter
  if e and not e.used and (num(try(GetTime)) or 0) - e.at <= ENC_WINDOW then
    local dead = unitFlag(UnitIsDead, "target") == true
    local tn = dead and str(try(UnitName, "target")) or nil
    if not tn or not e.name or tn == e.name then
      e.used = true
      return "enc|" .. e.id .. "|" .. e.at, "boss", "boss", e.id
    end
  end
  return nil
end

local function onLootOpened()
  local n = num(try(GetNumLootItems)) or 0
  if n <= 0 then return end
  if laggy() then session.gather = nil; return end   -- 1.0.5: stale slot data at high latency
  local moneyType0 = (Enum and Enum.LootSlotType and Enum.LootSlotType.Money) or 2
  if clean(try(IsFishingLoot)) then return lootFishing(n, moneyType0) end
  -- A finished Skinning/Mining/Herb Gathering/Pick Pocket/Disenchant cast just before this loot window: loot from
  -- the matching source (GATHER_ON) is that gathering, not its ordinary drop. The cast belongs to this window only.
  local g = recentGather()
  session.gather = nil
  local gatherSrc = g and g.kind ~= "fish" and g.kind or nil
  local moneyType = (Enum and Enum.LootSlotType and Enum.LootSlotType.Money) or 2
  local sources, order = {}, {}
  for slot = 1, n do
    local slotType = num(try(GetLootSlotType, slot))
    local link = try(GetLootSlotLink, slot)
    local itemId = itemIdFromLink(link)
    local suffix = suffixFromLink(link)
    local src = { try(GetLootSourceInfo, slot) }
    for k = 1, #src, 2 do
      local g = str(src[k])
      if not g and src[k] ~= nil then g = SECRET_SOURCE end
      local c = num(src[k + 1]) or 1
      if g then
        local s = sources[g]
        if not s then s = { items = {}, money = 0 }; sources[g] = s; order[#order + 1] = g end
        if slotType == moneyType then
          s.money = s.money + c
        elseif itemId then
          s.items[#s.items + 1] = { id = itemId, c = c, s = suffix }
        end
      end
    end
  end
  local t = num(try(GetTime)) or 0
  local pf = str(try(UnitFactionGroup, "player"))
  local grp = inGroup() or nil
  for _, sg in ipairs(order) do
    local guid, kind, src, id
    if sg == SECRET_SOURCE and gatherSrc then
      -- A gathering cast (skinning, pick pocket …) with a hidden source: its target, never a boss.
      local tg = str(try(UnitGUID, "target"))
      local tk, tid = parseGuid(tg)
      if tk and GATHER_ON[gatherSrc] == tk then guid, kind, src, id = tg, tk, gatherSrc, tid end
    elseif sg == SECRET_SOURCE then
      guid, kind, src, id = hiddenSource()
    else
      kind, id = parseGuid(sg)
      guid, src = sg, kind
      -- A disenchanted item is gone, so its GUID may not resolve: use the id remembered at the cast.
      if gatherSrc == "dis" and not kind and sg:find("^Item%-") then kind, id, src = "item", session.disItem, "item" end
      if gatherSrc and kind and GATHER_ON[gatherSrc] == kind then src = gatherSrc end
    end
    -- Dedup per (GUID, src): ordinary loot and skinning or pickpocketing of the same corpse are separate entries;
    -- none of them blocks another.
    local key = guid and (guid .. "|" .. tostring(src))
    if key and id and not session.looted[key] then
      session.looted[key] = true
      local s = sources[sg]
      if kind and (#s.items > 0 or s.money > 0) then
        -- h = corpse/chest tag (guidTag) so the server counts a corpse once when several group members upload it.
        pushLoot({ q = nextSeq(), t = now(), src = src, id = id, items = s.items, money = s.money > 0 and s.money or nil,
                   h = kind ~= "boss" and guidTag(guid) or nil, grp = grp, pf = pf })
        touch("loot", tostring(id))
        -- 1.0.8: inside an instance the game hides targets from addons, so the mob and the kill are noted here,
        -- where the corpse's GUID is readable.
        if kind == "npc" and src == "npc" and noteLootedMob then noteLootedMob(guid, id, grp) end
        if kind == "object" then
          local name = (t - (session.sentAt or 0) <= NAME_WINDOW) and session.sentName or nil
          session.sentName, session.sentAt = nil, 0   -- used once (L13)
          noteObject(id, name)
        end
      end
    end
  end
  if gatherSrc == "dis" then session.disItem = nil end
end

-- ENCOUNTER_END(encounterID, name, difficultyID, groupSize, success): a won encounter may own the next loot window
-- whose sources are hidden (hiddenSource). The name is only compared, never saved.
local function onEncounterEnd(encounterId, name, _, _, success)
  encounterId = num(encounterId)
  if not encounterId or encounterId <= 0 or num(success) ~= 1 then return end
  session.encounter = { id = encounterId, name = str(name), at = num(try(GetTime)) or 0 }
end

-- CHAT_MSG_SYSTEM: only compared with the client's own drunk/sober texts (DRUNK_MESSAGE_SELF1–4) for the drunk
-- guard. Nothing from the message is ever saved.
local function onSystemMessage(msg)
  msg = str(msg)
  if not msg or not _G then return end
  if msg == str(_G.DRUNK_MESSAGE_SELF1) then session.drunkAt = nil; return end
  for i = 2, 4 do
    if msg == str(_G["DRUNK_MESSAGE_SELF" .. i]) then session.drunkAt = num(try(GetTime)) or 0; return end
  end
end

---------------------------------------------------------------------------
-- Flight points
---------------------------------------------------------------------------

local function onTaxiMapOpened()
  noteUnit("npc", { flight = true })
  if not (C_TaxiMap and C_TaxiMap.GetAllTaxiNodes) then return end
  local mapId = num(try(GetTaxiMapID)) or (C_Map and num(try(C_Map.GetBestMapForUnit, "player")))
  if not mapId then return end
  local nodes = try(C_TaxiMap.GetAllTaxiNodes, mapId)
  if type(nodes) ~= "table" then return end
  local FPS = Enum and Enum.FlightPathState
  local current = (FPS and FPS.Current) or 0
  local reachable = (FPS and FPS.Reachable) or 1
  -- 1.0.5: the price (copper) from here to every reachable node, by node id (TaxiNodeCost(slotIndex)).
  local costs, nCosts = {}, 0
  for _, node in ipairs(nodes) do
    if type(node) == "table" and num(node.state) == reachable then
      local dest, slot = num(node.nodeID), num(node.slotIndex)
      local c = dest and slot and num(try(TaxiNodeCost, slot))
      if c and c >= 0 then costs[dest] = floor(c); nCosts = nCosts + 1 end
    end
  end
  local function costSig(t)
    local keys = {}
    for k, v in pairs(t or {}) do keys[#keys + 1] = tostring(k) .. "=" .. tostring(v) end
    table.sort(keys)
    return table.concat(keys, ",")
  end
  for _, node in ipairs(nodes) do
    if type(node) == "table" and num(node.state) == current then
      local nodeId, name = num(node.nodeID), str(node.name)
      local m, x, y = playerPos()
      if nodeId and name and m then
        local old = db.flights[nodeId]
        local rr = num(try(UnitReaction, "npc", "player"))
        local c = nCosts > 0 and costs or (old and old.c) or nil
        if not old or old.n ~= name or old.m ~= m or abs((old.x or 0) - x) > SPOT_RADIUS or abs((old.y or 0) - y) > SPOT_RADIUS
           or costSig(old.c) ~= costSig(c) or (nCosts > 0 and old.rr ~= rr) then
          -- rr = the flight master's UnitReaction toward the player (prices may carry a reputation discount at 6+).
          db.flights[nodeId] = { n = name, m = m, x = x, y = y, q = nextSeq(), c = c, rr = nCosts > 0 and rr or (old and old.rr) or nil }
          touch("flight", name)
        end
      end
      return
    end
  end
end

---------------------------------------------------------------------------
-- Mobs and NPCs you only look at (PLAYER_TARGET_CHANGED / UPDATE_MOUSEOVER_UNIT), 1.0.4
---------------------------------------------------------------------------

-- UnitClassification values; anything else (or a secret) is left out.
local CLASSIFICATIONS = { normal = true, elite = true, rare = true, rareelite = true, worldboss = true, trivial = true, minus = true }
-- Creature type ids (CreatureType.db2) by English name – fallback when UnitCreatureType has no id return.
local CREATURE_TYPES = {
  Beast = 1, Dragonkin = 2, Demon = 3, Elemental = 4, Giant = 5, Undead = 6, Humanoid = 7, Critter = 8,
  Mechanical = 9, ["Not specified"] = 10, Totem = 11, ["Non-combat Pet"] = 12, ["Gas Cloud"] = 13, ["Wild Pet"] = 14, Aberration = 15,
}

-- In combat (or unknown): mobs are skipped – unit data may be secret then, and it is not needed.
local function inCombat()
  if unitFlag(InCombatLockdown) ~= false then return true end
  return unitFlag(UnitAffectingCombat, "player") ~= false
end

-- A creature that no player controls: never a player, a pet, a guardian, a totem or a companion (their names
-- can be chosen by players). Every check must answer a clean "no"; unknown counts as "yes" and skips the unit.
local function wildNpc(unit)
  if unitFlag(UnitExists, unit) ~= true then return false end
  if unitFlag(UnitIsPlayer, unit) ~= false then return false end
  if unitFlag(UnitPlayerControlled, unit) ~= false then return false end
  if UnitIsOtherPlayersPet and unitFlag(UnitIsOtherPlayersPet, unit) ~= false then return false end
  if UnitIsBattlePetCompanion and unitFlag(UnitIsBattlePetCompanion, unit) ~= false then return false end
  if UnitIsUnit and unitFlag(UnitIsUnit, unit, "pet") == true then return false end
  return true
end

local function creatureType(unit)
  local name, id = try(UnitCreatureType, unit)
  id = num(id)
  if id and id >= 1 and id <= 50 then return id end
  name = str(name)
  return name and CREATURE_TYPES[name] or nil
end

-- Adds the player's position as an approximate area unless a saved area on the same map is within MOB_RADIUS.
-- The time is rounded to the day: an area says where a mob lives, not when the player was there.
local function addArea(rec)
  local m, x, y = playerPos()
  if not m then return false end
  rec.a = rec.a or {}
  for _, s in ipairs(rec.a) do
    if s.m == m and abs((s.x or 0) - x) < MOB_RADIUS and abs((s.y or 0) - y) < MOB_RADIUS then return false end
  end
  if #rec.a >= MOB_AREAS then return false end
  rec.a[#rec.a + 1] = { m = m, x = x, y = y, t = floor(now() / 86400) * 86400, q = nextSeq() }
  return true
end

-- 1.0.5: an exact spot – the player stands within interact distance (CheckInteractDistance index 3, ~10 yards) of a
-- mob nobody else has tapped, out of combat. Saved in rec.e (at most MOB_EXACT, 1 map-% apart, day only).
local function nearUnit(unit)
  if not CheckInteractDistance then return false end
  if unitFlag(CheckInteractDistance, unit, 3) ~= true then return false end
  return unitFlag(UnitIsTapDenied, unit) == false
end

local function addExact(rec)
  local m, x, y = playerPos()
  if not m then return false end
  rec.e = rec.e or {}
  for _, s in ipairs(rec.e) do
    if s.m == m and abs((s.x or 0) - x) < EXACT_RADIUS and abs((s.y or 0) - y) < EXACT_RADIUS then return false end
  end
  if #rec.e >= MOB_EXACT then return false end
  rec.e[#rec.e + 1] = { m = m, x = x, y = y, t = floor(now() / 86400) * 86400, q = nextSeq() }
  return true
end

local function noteMob(unit)
  if inCombat() then return end
  local guid = str(try(UnitGUID, unit))
  local kind, id = parseGuid(guid)
  if kind ~= "npc" then return end            -- players' and pets' GUIDs are not even remembered
  local t = num(try(GetTime)) or 0
  local last = session.mobSeen[guid]
  -- A mob looked at again within MOB_REPEAT is skipped – unless the player is now next to it (exact spot, once).
  local near = not session.mobNear[guid] and nearUnit(unit)
  if last and t - last < MOB_REPEAT and not near then return end
  if not last then
    if session.mobSeenN >= MOB_GUIDS then session.mobSeen, session.mobNear, session.mobSeenN = {}, {}, 0 end
    session.mobSeenN = session.mobSeenN + 1
  end
  session.mobSeen[guid] = t
  if not wildNpc(unit) then return end
  local name = str(try(UnitName, unit))
  if not name then return end
  local ct = creatureType(unit)
  if ct == 11 or ct == 12 then return end      -- totems and companion pets belong to players
  local rec = db.mobs[id]
  local isNew = not rec
  if isNew then
    if session.mobCount >= MAX_MOBS then return end
    rec = {}; db.mobs[id] = rec; session.mobCount = session.mobCount + 1
  end
  rec.n = name
  -- 1.0.7: instances the mob was seen in (map IDs, at most MOB_INSTANCES) – the only location kept inside one.
  local inst, newInst = instanceMap(), false
  if inst then
    rec.i = type(rec.i) == "table" and rec.i or {}
    local known = false
    for _, v in ipairs(rec.i) do if v == inst then known = true end end
    if not known and #rec.i < MOB_INSTANCES then rec.i[#rec.i + 1] = inst; newInst = true end
  end
  -- 1.0.5: no level while drunk (the client then shows other levels).
  local l = not drunk() and num(try(UnitLevel, unit)) or nil
  if l and l > 0 then
    if not rec.lmin or rec.lmin < 1 then rec.lmin, rec.lmax = l, l
    else rec.lmin, rec.lmax = math.min(rec.lmin, l), math.max(rec.lmax or l, l) end
  elseif l == -1 and not rec.lmin then
    rec.lmin, rec.lmax = -1, -1                 -- "??" (skull)
  end
  rec.r = reactionWord(try(UnitReaction, unit, "player")) or rec.r
  rec.pf = str(try(UnitFactionGroup, "player")) or rec.pf
  local c = str(try(UnitClassification, unit))
  if c and CLASSIFICATIONS[c] then rec.c = c end
  if ct then rec.ct = ct end
  local changed = addArea(rec) or isNew or newInst
  if near then
    session.mobNear[guid] = true
    if addExact(rec) then changed = true end
  end
  if noteStats(rec, unit, l) then changed = true end
  if changed then touch("mob", name) end
end

---------------------------------------------------------------------------
-- Kills (1.0.5): the denominator for drop chances
---------------------------------------------------------------------------

-- A creature counts as our kill when it was seen alive as our target and then dead with the tap ours or our group's
-- (UnitIsTapDenied = false). KILL_LOOT_DELAY later CanLootUnit says whether the corpse had loot, so the server can
-- count kills without loot (they never open a loot window). Saved: creature id, corpse tag (guidTag), the day,
-- loot yes/no (nil = unknown) and the group flag – never the GUID. Event-driven only (target changes, the target's
-- UNIT_HEALTH, leaving combat).
local function pushKill(entry)
  local kills = db.kills
  kills[#kills + 1] = entry
  while #kills > MAX_KILLS do table.remove(kills, 1) end
end

local function remember(set, guid)
  if session.killN >= MOB_GUIDS then session.alive, session.killed, session.killN = {}, {}, 0 end
  if not set[guid] then session.killN = session.killN + 1 end
  set[guid] = true
end

local function watchTarget()
  local guid = str(try(UnitGUID, "target"))
  local kind, id = parseGuid(guid)
  if kind ~= "npc" or session.killed[guid] then return end
  local dead = unitFlag(UnitIsDead, "target")
  if dead == false then
    if not session.alive[guid] and wildNpc("target") then remember(session.alive, guid) end
    return
  end
  if dead ~= true or not session.alive[guid] then return end
  if unitFlag(UnitIsTapDenied, "target") ~= false then return end
  remember(session.killed, guid)
  session.alive[guid] = nil
  local entry = { t = floor(now() / 86400) * 86400, id = id, h = guidTag(guid), grp = inGroup() or nil, im = instanceMap() }
  after(KILL_LOOT_DELAY, function()
    if CanLootUnit then
      local has = clean(try(CanLootUnit, guid))
      if has ~= nil then entry.loot = has and true or false end
    end
    entry.q = nextSeq()
    pushKill(entry)
    touch("kill", tostring(id))
  end)
end

---------------------------------------------------------------------------
-- Looted in an instance (1.0.8)
---------------------------------------------------------------------------

-- Inside instances the game hides unit information from addons while fighting (secret values), so noteMob and
-- watchTarget above skip nearly everything there: The Hall of Thanes produced loot but no mobs and no kills.
-- The loot window does give the corpse's GUID. When a creature is looted inside an instance:
--   * the kill is recorded (once per corpse – not if watchTarget already did), with loot = true;
--   * the mob is recorded with the instance id, if its name can be read from the target or the mouseover unit
--     that IS this corpse (same GUID). Only the name, level and classification – never a position.
-- Outside instances nothing changes.
local function corpseUnit(guid)
  for _, unit in ipairs({ "target", "mouseover" }) do
    if str(try(UnitGUID, unit)) == guid then return unit end
  end
  return nil
end

noteLootedMob = function(guid, id, grp)
  local inst = instanceMap()
  if not inst then return end
  if not session.killed[guid] then
    remember(session.killed, guid)
    session.alive[guid] = nil
    pushKill({ t = floor(now() / 86400) * 86400, id = id, h = guidTag(guid), grp = grp, im = inst, loot = true, q = nextSeq() })
    touch("kill", tostring(id))
  end
  local unit = corpseUnit(guid)
  local name = unit and str(try(UnitName, unit)) or nil
  if not name then return end
  local ct = creatureType(unit)
  if ct == 11 or ct == 12 then return end        -- totems and companion pets belong to players
  local rec = db.mobs[id]
  local isNew = not rec
  if isNew then
    if session.mobCount >= MAX_MOBS then return end
    rec = {}; db.mobs[id] = rec; session.mobCount = session.mobCount + 1
  end
  rec.n = name
  rec.i = type(rec.i) == "table" and rec.i or {}
  local known = false
  for _, v in ipairs(rec.i) do if v == inst then known = true end end
  local newInst = false
  if not known and #rec.i < MOB_INSTANCES then rec.i[#rec.i + 1] = inst; newInst = true end
  local l = not drunk() and num(try(UnitLevel, unit)) or nil
  if l and l > 0 then
    if not rec.lmin or rec.lmin < 1 then rec.lmin, rec.lmax = l, l
    else rec.lmin, rec.lmax = math.min(rec.lmin, l), math.max(rec.lmax or l, l) end
  elseif l == -1 and not rec.lmin then
    rec.lmin, rec.lmax = -1, -1
  end
  local c = str(try(UnitClassification, unit))
  if c and CLASSIFICATIONS[c] then rec.c = c end
  if ct then rec.ct = ct end
  rec.pf = str(try(UnitFactionGroup, "player")) or rec.pf
  if isNew or newInst then touch("mob", name) end
end

---------------------------------------------------------------------------
-- Mailboxes and service NPCs (1.0.4)
---------------------------------------------------------------------------

-- MAIL_SHOW: a mailbox is a game object, so the position is the player's (standing at it). A creature as the
-- interaction unit (a summoned courier) is not a mailbox. The object id is kept when the client gives it.
local function onMailShow()
  local kind, id = parseGuid(try(UnitGUID, "npc"))
  if kind == "npc" then return end
  local m, x, y = playerPos()
  if not m then return end
  for _, s in ipairs(db.mail) do
    if s.m == m and abs((s.x or 0) - x) < MAIL_RADIUS and abs((s.y or 0) - y) < MAIL_RADIUS then return end
  end
  db.mail[#db.mail + 1] = { m = m, x = x, y = y, t = now(), q = nextSeq(), id = (kind == "object") and id or nil }
  while #db.mail > MAX_MAIL do table.remove(db.mail, 1) end
  touch("mail", tostring(m))
end

-- PLAYER_INTERACTION_MANAGER_FRAME_SHOW(type) (Mainline): the interaction type names are looked up in
-- Enum.PlayerInteractionType, never as numbers – a name the client lacks simply never matches.
local function onInteractionShow(kind)
  local PIT = Enum and Enum.PlayerInteractionType
  kind = num(kind)
  if type(PIT) ~= "table" or not kind then return end
  if kind == PIT.Auctioneer then noteUnit("npc", { auction = true })
  elseif kind == PIT.StableMaster then noteUnit("npc", { stable = true })
  elseif kind == PIT.SpiritHealer then noteUnit("npc", { spirit = true })
  elseif kind == PIT.MailInfo then onMailShow()
  -- 1.0.5: innkeeper, battlemaster, talent reset and tabard vendor (both spellings the enum has used).
  elseif kind == PIT.Binder then noteUnit("npc", { innkeeper = true })
  elseif kind == PIT.BattleMaster or kind == PIT.Battlemaster then noteUnit("npc", { battlemaster = true })
  elseif kind == PIT.TalentMaster then noteUnit("npc", { talent = true })
  elseif kind == PIT.GuildTabardVendor or kind == PIT.TabardVendor then noteUnit("npc", { tabard = true }) end
end

-- 1.0.5: a readable game object (book, plaque, sign) – ITEM_TEXT_BEGIN with an object as the interaction unit.
-- Text read from a bag item has no object there and is ignored; the text itself is never read.
local function onItemTextBegin()
  local kind, id = parseGuid(try(UnitGUID, "npc"))
  if kind == "object" then noteObject(id, str(try(UnitName, "npc"))) end
end

-- 1.0.5: PLAYER_SOFT_INTERACT_CHANGED(old, new) – the object the interact key would use now (Mainline soft
-- targeting, a few yards away). Only game objects, out of combat, each GUID at most once per MOB_REPEAT seconds.
local function onSoftInteract(_, newGuid)
  if inCombat() then return end
  local guid = str(newGuid)
  local kind, id = parseGuid(guid)
  if kind ~= "object" then return end
  local t = num(try(GetTime)) or 0
  local last = session.softSeen[guid]
  if last and t - last < MOB_REPEAT then return end
  if not last then
    if session.softN >= MOB_GUIDS then session.softSeen, session.softN = {}, 0 end
    session.softN = session.softN + 1
  end
  session.softSeen[guid] = t
  -- The name only when the soft target still is this object (never another unit's name).
  local name = str(try(UnitGUID, "softinteract")) == guid and str(try(UnitName, "softinteract")) or nil
  noteObject(id, name)
end

---------------------------------------------------------------------------
-- Optional character data (off by default, per character)
---------------------------------------------------------------------------

local function updateChar()
  if not (opt and opt.char) then return end
  local _, classFile = try(UnitClass, "player")
  local _, raceFile = try(UnitRace, "player")
  local c = {
    class = str(classFile), race = str(raceFile), level = num(try(UnitLevel, "player")),
    professions = {},
  }
  local profs = { try(GetProfessions) }
  for i = 1, 6 do
    local idx = num(profs[i])
    if idx then
      local _, _, rank, _, _, _, skillLine = try(GetProfessionInfo, idx)
      skillLine, rank = num(skillLine), num(rank)
      if skillLine then c.professions[#c.professions + 1] = { id = skillLine, skill = rank or 0 } end
    end
  end
  local old = db.char
  local recipes = {}
  local seen = {}
  if type(old) == "table" and type(old.recipes) == "table" and old.class == c.class and old.race == c.race then
    for _, r in ipairs(old.recipes) do if not seen[r] then seen[r] = true; recipes[#recipes + 1] = r end end
  end
  for r in pairs(session.recipes or {}) do if not seen[r] then seen[r] = true; recipes[#recipes + 1] = r end end
  table.sort(recipes)
  c.recipes = recipes
  db.char = c
end

local function scanRecipes()
  if not (opt and opt.char) or not C_TradeSkillUI then return end
  local ids = try(C_TradeSkillUI.GetAllRecipeIDs)
  if type(ids) ~= "table" then return end
  session.recipes = session.recipes or {}
  for _, rid in ipairs(ids) do
    rid = num(rid)
    local info = rid and try(C_TradeSkillUI.GetRecipeInfo, rid)
    if type(info) == "table" and clean(info.learned) then session.recipes[rid] = true end
  end
  updateChar()
end

local recipePending = false
local function queueRecipeScan()
  if recipePending or not (opt and opt.char) then return end
  recipePending = true
  after(1, function() recipePending = false; scanRecipes() end)
end

---------------------------------------------------------------------------
-- Database
---------------------------------------------------------------------------

local function newInstallId()
  local hex = {}
  for i = 1, 16 do hex[i] = string.format("%x", math.random(0, 15)) end
  return table.concat(hex)
end

local TABLES = { "npcs", "trainers", "vendors", "quests", "objects", "loot", "flights", "mobs", "mail", "kills" }

local function ensureTables(d)
  for _, k in ipairs(TABLES) do
    if type(d[k]) ~= "table" then d[k] = {} end
  end
end

local function initDb()
  if type(ForeverScoutDB) ~= "table" then ForeverScoutDB = {} end
  db = ForeverScoutDB
  db.v = FORMAT
  db.addon = VERSION
  -- Hela byggsträngen, t.ex. "1.60.1.70790" (GetBuildInfo: version, build, datum, interface).
  local version, build = try(GetBuildInfo)
  version, build = str(version), str(build) or (num(build) and tostring(build))
  db.build = (version and build and (version .. "." .. build)) or version or db.build
  db.locale = str(try(GetLocale)) or db.locale
  if type(db.install) ~= "string" or #db.install < 8 then db.install = newInstallId() end
  db.seq = tonumber(db.seq) or 0
  ensureTables(db)
  session.mobCount = 0
  for _ in pairs(db.mobs) do session.mobCount = session.mobCount + 1 end
  if type(ForeverScoutOpt) ~= "table" then ForeverScoutOpt = { char = false } end
  opt = ForeverScoutOpt
end

---------------------------------------------------------------------------
-- Slash command
---------------------------------------------------------------------------

local function T()
  return ns.GetTexts and ns.GetTexts(opt and opt.lang) or {}
end

local function say(msg)
  local frame = DEFAULT_CHAT_FRAME
  if frame and frame.AddMessage then
    try(frame.AddMessage, frame, "|cff7fb4ffForeverScout|r: " .. tostring(msg))
  end
end

-- /scout test (1.0.8): what the game lets the addon read about the current target right now – to find out why
-- something is not recorded (e.g. inside an instance). Prints only yes/no/hidden; nothing is saved or sent.
local function diagnose()
  local function flag(fn, ...)
    if type(fn) ~= "function" then return "n/a" end
    local v = unitFlag(fn, ...)
    if v == nil then return "hidden" end
    return v and "yes" or "no"
  end
  local function readable(v) return v ~= nil and "ok" or "hidden" end
  local inst = instanceMap()
  say("test: instance=" .. tostring(inst or "no") .. " combat=" .. (inCombat() and "yes" or "no") .. " mobs=" .. tostring(session.mobCount))
  for _, unit in ipairs({ "target", "mouseover" }) do
    if unitFlag(UnitExists, unit) == true then
      local kind = parseGuid(str(try(UnitGUID, unit)))
      say(unit .. ": guid=" .. readable(str(try(UnitGUID, unit))) .. " kind=" .. tostring(kind) .. " name=" .. readable(str(try(UnitName, unit)))
        .. " level=" .. readable(num(try(UnitLevel, unit))) .. " dead=" .. flag(UnitIsDead, unit) .. " player=" .. flag(UnitIsPlayer, unit)
        .. " controlled=" .. flag(UnitPlayerControlled, unit) .. " tapDenied=" .. flag(UnitIsTapDenied, unit) .. " wild=" .. (wildNpc(unit) and "yes" or "no"))
    else
      say(unit .. ": none")
    end
  end
end

local function count(t)
  local n = 0
  for _ in pairs(t or {}) do n = n + 1 end
  return n
end

local function ago(L, secs)
  secs = math.max(0, secs or 0)
  if secs < 60 then return string.format(L.sec, secs) end
  if secs < 3600 then return string.format(L.min, floor(secs / 60)) end
  if secs < 86400 then return string.format(L.hour, floor(secs / 3600)) end
  return string.format(L.day, floor(secs / 86400))
end

local function showStatus()
  local L = T()
  local c = {
    count(db.npcs), count(db.trainers), count(db.vendors), count(db.quests),
    count(db.objects), #db.loot, count(db.flights), count(db.mobs), #db.mail, #db.kills,
  }
  local total = 0
  for _, v in ipairs(c) do total = total + v end
  say(string.format(L.status_head, VERSION, total, db.seq or 0))
  if total == 0 then say(L.status_none); return end
  say(string.format(L.status_counts, c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9], c[10]))
  if session.lastKind then
    say(string.format(L.status_last, L["kind_" .. session.lastKind] or session.lastKind,
      tostring(session.lastName), ago(L, now() - (session.lastTime or now()))))
  end
  if not positionAllowed() then say(L.status_pos_off) end
  say(string.format(L.status_char, (opt and opt.char) and L.on or L.off))
end

local function onSlash(msg)
  if not db then return end
  local L = T()
  msg = (type(msg) == "string" and msg or ""):lower()
  local cmd, arg = msg:match("^%s*(%S*)%s*(.-)%s*$")
  if cmd ~= "reset" then session.resetArmed = false end
  if cmd == "" or cmd == "status" then
    showStatus()
  elseif cmd == "help" then
    for _, line in ipairs(L.help) do say(line) end
  elseif cmd == "char" then
    if arg == "on" then
      opt.char = true; updateChar(); say(L.char_on)
    elseif arg == "off" then
      opt.char = false; db.char = nil; session.recipes = nil; say(L.char_off)
    else
      say(string.format(L.status_char, opt.char and L.on or L.off))
    end
  elseif cmd == "lang" then
    if arg == "sv" or arg == "en" then opt.lang = arg else opt.lang = nil end
    L = T()
    say(string.format(L.lang_set, opt.lang or "auto"))
  elseif cmd == "reset" then
    if arg == "confirm" and session.resetArmed then
      for _, k in ipairs(TABLES) do db[k] = {} end
      db.char = nil
      session.looted = {}
      session.alive, session.killed, session.killN, session.mobNear = {}, {}, 0, {}
      session.objPending = {}
      session.mobSeen, session.mobSeenN, session.mobCount = {}, 0, 0
      session.resetArmed = false
      say(L.reset_done)       -- seq and install are kept so the uploader never re-sends old posts
    else
      session.resetArmed = true
      say(L.reset_ask)
    end
  elseif cmd == "sync" then
    -- WoW skriver SavedVariables bara vid /reload, utloggning och avslut. Forever Scout-klienten
    -- bevakar filen och laddar upp när den sparats.
    say(L.sync)
    if C_UI and C_UI.Reload then C_UI.Reload() elseif ReloadUI then ReloadUI() end
  elseif cmd == "test" then
    diagnose()
  elseif cmd == "debug" then
    say(string.format(L.debug, session.errors, tostring(session.lastError or "-")))
  else
    say(L.unknown)
  end
end

---------------------------------------------------------------------------
-- Events
---------------------------------------------------------------------------

local handlers = {
  GOSSIP_SHOW = onGossip,
  MERCHANT_SHOW = onMerchantShow,
  MERCHANT_UPDATE = onMerchantUpdate,
  MERCHANT_CLOSED = function() session.vendorId = nil end,
  TRAINER_SHOW = onTrainerShow,
  TRAINER_UPDATE = onTrainerUpdate,
  TRAINER_CLOSED = function() session.trainerId = nil end,
  QUEST_GREETING = function() noteUnit("npc", { quest = true }) end,
  QUEST_DETAIL = onQuestDetail,
  QUEST_ACCEPTED = onQuestAccepted,
  QUEST_LOG_UPDATE = queueObjectiveRefresh,
  ITEM_DATA_LOAD_RESULT = queueObjectiveRefresh,
  QUEST_PROGRESS = function() onQuestEnd(false) end,
  QUEST_COMPLETE = function() onQuestEnd(true) end,
  TAXIMAP_OPENED = onTaxiMapOpened,
  BANKFRAME_OPENED = function() noteUnit("npc", nil) end,
  -- 1.0.4: mobs you only look at, mailboxes and service NPCs (auction house, stable, spirit healer).
  PLAYER_TARGET_CHANGED = function() noteMob("target"); watchTarget() end,
  UPDATE_MOUSEOVER_UNIT = function() noteMob("mouseover") end,
  MAIL_SHOW = onMailShow,
  AUCTION_HOUSE_SHOW = function() noteUnit("npc", { auction = true }) end,
  PET_STABLE_SHOW = function() noteUnit("npc", { stable = true }) end,
  PLAYER_INTERACTION_MANAGER_FRAME_SHOW = onInteractionShow,
  -- 1.0.5: kills (the target's health and leaving combat), encounters, the drunk guard, more service roles and
  -- object positions from readable and soft-interact objects.
  UNIT_HEALTH = function(unit) if unit == "target" then watchTarget() end end,
  PLAYER_REGEN_ENABLED = watchTarget,
  ENCOUNTER_END = onEncounterEnd,
  CHAT_MSG_SYSTEM = onSystemMessage,
  CONFIRM_BINDER = function() noteUnit("npc", { innkeeper = true }) end,
  BATTLEFIELDS_SHOW = function() noteUnit("npc", { battlemaster = true }) end,
  CONFIRM_TALENT_WIPE = function() noteUnit("npc", { talent = true }) end,
  OPEN_TABARD_FRAME = function() noteUnit("npc", { tabard = true }) end,
  ITEM_TEXT_BEGIN = onItemTextBegin,
  PLAYER_SOFT_INTERACT_CHANGED = onSoftInteract,
  UNIT_SPELLCAST_SENT = onSpellcastSent,
  UNIT_SPELLCAST_SUCCEEDED = onSpellcastSucceeded,
  LOOT_OPENED = onLootOpened,
  TRADE_SKILL_SHOW = queueRecipeScan,
  TRADE_SKILL_LIST_UPDATE = queueRecipeScan,
  PLAYER_LEVEL_UP = function() after(1, updateChar) end,
  SKILL_LINES_CHANGED = function() after(1, updateChar) end,
  PLAYER_LOGOUT = function() updateChar() end,
  -- 1.0.6: one line in chat after login and /reload, so that players can see that the addon is loaded.
  PLAYER_LOGIN = function() after(5, function() say(string.format(T().loaded or "%s", VERSION)) end) end,
}

local frame = CreateFrame and CreateFrame("Frame") or nil

local function dispatch(_, event, ...)
  if event == "ADDON_LOADED" then
    if ... ~= ADDON then return end
    initDb()
    for ev in pairs(handlers) do
      -- UNIT_HEALTH only for the target where the client can filter by unit (fewer events to ignore).
      if ev == "UNIT_HEALTH" and frame.RegisterUnitEvent then
        try(frame.RegisterUnitEvent, frame, ev, "target")
      else
        try(frame.RegisterEvent, frame, ev)
      end
    end
    try(frame.UnregisterEvent, frame, "ADDON_LOADED")
    return
  end
  if not db then return end
  local fn = handlers[event]
  if fn then fn(...) end
end

if frame then
  frame:SetScript("OnEvent", function(self, event, ...)
    local ok, err = pcall(dispatch, self, event, ...)
    if not ok then
      session.errors = session.errors + 1
      session.lastError = tostring(event) .. ": " .. tostring(err)
    end
  end)
  try(frame.RegisterEvent, frame, "ADDON_LOADED")
end

SLASH_FOREVERSCOUT1 = "/scout"
SLASH_FOREVERSCOUT2 = "/foreverscout"
if SlashCmdList then
  SlashCmdList.FOREVERSCOUT = function(msg)
    local ok, err = pcall(onSlash, msg)
    if not ok then
      session.errors = session.errors + 1
      session.lastError = "slash: " .. tostring(err)
    end
  end
end

-- Test hook (only used by the offline mock test; harmless in game).
ns._test = {
  session = session, parseGuid = parseGuid, clean = clean, db = function() return db end,
  noteMob = noteMob, wildNpc = wildNpc, onSpellcastSent = onSpellcastSent, onLootOpened = onLootOpened,
  onMailShow = onMailShow, onInteractionShow = onInteractionShow, handlers = handlers,
  guidTag = guidTag, suffixFromLink = suffixFromLink, costList = costList, drunk = drunk,
}
