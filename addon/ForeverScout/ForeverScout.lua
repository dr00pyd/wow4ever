-- ForeverScout – collects world data for guide.wow4ever.se while you play.
-- Format: docs/briefs/SCOUT-KONTRAKT.md §1 (ForeverScoutDB, v = 1).
--
-- Design rules:
--  * Never show an error to the player: every event handler runs in pcall, every API is feature-detected.
--  * Midnight/Forever "secret values": every value read from the game passes clean() before it is
--    compared or stored. Secret = treated as missing.
--  * No OnUpdate. Work happens only on interaction events; re-scans are debounced with C_Timer.
--  * Never stored: character name, realm, account, full GUIDs (only the creature/object id, field 6).
local ADDON, ns = ...
ns = ns or {}

local VERSION = "1.0.2"
local FORMAT = 1
local MAX_SPOTS = 50          -- per NPC / object, oldest dropped
local MAX_LOOT = 2000         -- loot entries, oldest dropped
local SPOT_RADIUS = 0.5       -- map-% : closer than this to a recent spot = same spot
local SPOT_REPEAT = 3600      -- seconds before the same spot may be recorded again
local NAME_WINDOW = 15        -- seconds a UNIT_SPELLCAST_SENT target name is trusted for an object

local floor, abs, type, tonumber, tostring, pairs, ipairs, select, pcall =
      math.floor, math.abs, type, tonumber, tostring, pairs, ipairs, select, pcall

local db                      -- = ForeverScoutDB once loaded
local opt                     -- = ForeverScoutOpt (per character, never uploaded)
local session = {
  errors = 0, lastError = nil,
  looted = {},                -- source GUIDs looted this session (memory only, never saved)
  lastKind = nil, lastName = nil, lastTime = nil,
  vendorId = nil, trainerId = nil,
  trainerScans = 0, trainerQuietUntil = 0,
  sentName = nil, sentAt = 0,
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
    local l = num(try(UnitLevel, unit))
    if l and l > 0 then rec.l = l end
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

local function costList(i)
  local n = num(try(GetMerchantItemCostInfo, i)) or 0
  if n <= 0 then return nil end
  local ext = {}
  for j = 1, n do
    local _, value, link = try(GetMerchantItemCostItem, i, j)
    link = str(link)
    value = num(value)
    if link and value then
      local item = tonumber(link:match("item:(%d+)"))
      local cur = tonumber(link:match("currency:(%d+)"))
      if item then ext[#ext + 1] = { item = item, c = value }
      elseif cur then ext[#ext + 1] = { currency = cur, c = value } end
    end
  end
  if #ext == 0 then return nil end
  return ext
end

local function scanVendor()
  local id = session.vendorId
  if not id then return end
  local n = num(try(GetMerchantNumItems)) or 0
  local items = {}
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
  if #items == 0 then return end
  local old = db.vendors[id]
  local sig = signature(items, { "id", "price", "qty", "ext" })
  if old and old.items then
    -- Never replace a fuller list with one that is still loading.
    if #items < #old.items and now() - (old.t or 0) < 7 * 86400 then return end
    if signature(old.items, { "id", "price", "qty", "ext" }) == sig then return end
  end
  db.vendors[id] = { q = nextSeq(), t = now(), items = items }
  touch("vendor", (db.npcs[id] and db.npcs[id].n) or tostring(id))
end

local vendorPending = false
local function queueVendorScan()
  if vendorPending then return end
  vendorPending = true
  after(0.5, function() vendorPending = false; scanVendor() end)
end

local function onMerchantShow()
  local roles = { vendor = true }
  if clean(try(CanMerchantRepair)) then roles.repair = true end
  local kind, id = noteUnit("npc", roles)
  session.vendorId = (kind == "npc") and id or nil
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
  return r
end

local function questSig(rec)
  local g, e, rw = rec.giver or {}, rec.ender or {}, rec.rewards or {}
  return table.concat({
    tostring(rec.title), tostring(rec.lvl), tostring(rec.minLvl),
    tostring(g.npc), tostring(g.object), tostring(e.npc), tostring(e.object),
    tostring(rec.m), tostring(rec.x), tostring(rec.y),
    table.concat(rec.objectives or {}, "|"),
    table.concat(rw.items or {}, ","), table.concat(rw.choice or {}, ","), tostring(rw.money), tostring(rw.xp),
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

local function onQuestAccepted(a, b)
  local id = num(b) or num(a)
  if not id or id <= 0 then return end
  questUpdate(id, function(rec)
    local lvl, title = questLogLevel(id)
    if lvl then rec.lvl = lvl end
    rec.title = title or rec.title
    local objs = C_QuestLog and try(C_QuestLog.GetQuestObjectives, id)
    if type(objs) == "table" and #objs > 0 then
      local list = {}
      for _, o in ipairs(objs) do
        local t = type(o) == "table" and str(o.text)
        if t then list[#list + 1] = t end
      end
      if #list > 0 then rec.objectives = list end
    end
  end)
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

local function onSpellcastSent(unit, target)
  if unit ~= "player" then return end
  local name = str(target)
  if name then
    session.sentName = name
    session.sentAt = num(try(GetTime)) or 0
  end
end

local function pushLoot(entry)
  local loot = db.loot
  loot[#loot + 1] = entry
  while #loot > MAX_LOOT do table.remove(loot, 1) end
end

local function onLootOpened()
  if clean(try(IsFishingLoot)) then return end
  local n = num(try(GetNumLootItems)) or 0
  if n <= 0 then return end
  local moneyType = (Enum and Enum.LootSlotType and Enum.LootSlotType.Money) or 2
  local sources, order = {}, {}
  for slot = 1, n do
    local slotType = num(try(GetLootSlotType, slot))
    local itemId = itemIdFromLink(try(GetLootSlotLink, slot))
    local src = { try(GetLootSourceInfo, slot) }
    for k = 1, #src, 2 do
      local g = str(src[k])
      local c = num(src[k + 1]) or 1
      if g then
        local s = sources[g]
        if not s then s = { items = {}, money = 0 }; sources[g] = s; order[#order + 1] = g end
        if slotType == moneyType then
          s.money = s.money + c
        elseif itemId then
          s.items[#s.items + 1] = { id = itemId, c = c }
        end
      end
    end
  end
  local t = num(try(GetTime)) or 0
  for _, g in ipairs(order) do
    if not session.looted[g] then
      session.looted[g] = true
      local kind, id = parseGuid(g)
      local s = sources[g]
      if kind and (#s.items > 0 or s.money > 0) then
        pushLoot({ q = nextSeq(), t = now(), src = kind, id = id, items = s.items, money = s.money > 0 and s.money or nil })
        touch("loot", tostring(id))
        if kind == "object" then
          local name = (t - (session.sentAt or 0) <= NAME_WINDOW) and session.sentName or nil
          noteObject(id, name)
        end
      end
    end
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
  local current = (Enum and Enum.FlightPathState and Enum.FlightPathState.Current) or 0
  for _, node in ipairs(nodes) do
    if type(node) == "table" and num(node.state) == current then
      local nodeId, name = num(node.nodeID), str(node.name)
      local m, x, y = playerPos()
      if nodeId and name and m then
        local old = db.flights[nodeId]
        if not old or old.n ~= name or old.m ~= m or abs((old.x or 0) - x) > SPOT_RADIUS or abs((old.y or 0) - y) > SPOT_RADIUS then
          db.flights[nodeId] = { n = name, m = m, x = x, y = y, q = nextSeq() }
          touch("flight", name)
        end
      end
      return
    end
  end
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

local function ensureTables(d)
  for _, k in ipairs({ "npcs", "trainers", "vendors", "quests", "objects", "loot", "flights" }) do
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
    count(db.objects), #db.loot, count(db.flights),
  }
  local total = 0
  for _, v in ipairs(c) do total = total + v end
  say(string.format(L.status_head, VERSION, total, db.seq or 0))
  if total == 0 then say(L.status_none); return end
  say(string.format(L.status_counts, c[1], c[2], c[3], c[4], c[5], c[6], c[7]))
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
      for _, k in ipairs({ "npcs", "trainers", "vendors", "quests", "objects", "loot", "flights" }) do db[k] = {} end
      db.char = nil
      session.looted = {}
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
  MERCHANT_UPDATE = function() if session.vendorId then queueVendorScan() end end,
  MERCHANT_CLOSED = function() session.vendorId = nil end,
  TRAINER_SHOW = onTrainerShow,
  TRAINER_UPDATE = onTrainerUpdate,
  TRAINER_CLOSED = function() session.trainerId = nil end,
  QUEST_GREETING = function() noteUnit("npc", { quest = true }) end,
  QUEST_DETAIL = onQuestDetail,
  QUEST_ACCEPTED = onQuestAccepted,
  QUEST_PROGRESS = function() onQuestEnd(false) end,
  QUEST_COMPLETE = function() onQuestEnd(true) end,
  TAXIMAP_OPENED = onTaxiMapOpened,
  BANKFRAME_OPENED = function() noteUnit("npc", nil) end,
  UNIT_SPELLCAST_SENT = onSpellcastSent,
  LOOT_OPENED = onLootOpened,
  TRADE_SKILL_SHOW = queueRecipeScan,
  TRADE_SKILL_LIST_UPDATE = queueRecipeScan,
  PLAYER_LEVEL_UP = function() after(1, updateChar) end,
  SKILL_LINES_CHANGED = function() after(1, updateChar) end,
  PLAYER_LOGOUT = function() updateChar() end,
}

local frame = CreateFrame and CreateFrame("Frame") or nil

local function dispatch(_, event, ...)
  if event == "ADDON_LOADED" then
    if ... ~= ADDON then return end
    initDb()
    for ev in pairs(handlers) do
      try(frame.RegisterEvent, frame, ev)
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
ns._test = { session = session, parseGuid = parseGuid, clean = clean }
