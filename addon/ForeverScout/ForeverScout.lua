-- ForeverScout – collects world data for guide.wow4ever.se while you play.
-- Format: docs/briefs/SCOUT-KONTRAKT.md §1 (ForeverScoutDB, v = 1).
--
-- Design rules:
--  * Never show an error to the player: every event handler runs in pcall, every API is feature-detected.
--  * Midnight/Forever "secret values": every value read from the game passes clean() before it is
--    compared or stored. Secret = treated as missing.
--  * No OnUpdate. Work happens only on interaction events; re-scans are debounced with C_Timer.
--  * Never stored: character name, realm, account, full GUIDs (only the creature/object id, field 6).
--    Nothing about other players: target/mouseover only records creatures that no player controls.
local ADDON, ns = ...
ns = ns or {}

local VERSION = "1.0.4"
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
local MAX_MAIL = 300          -- mailbox spots, oldest dropped
local MAIL_RADIUS = 1         -- map-% : a mailbox this close to a saved one (same map) is the same mailbox

local floor, abs, type, tonumber, tostring, pairs, ipairs, select, pcall =
      math.floor, math.abs, type, tonumber, tostring, pairs, ipairs, select, pcall

local db                      -- = ForeverScoutDB once loaded
local opt                     -- = ForeverScoutOpt (per character, never uploaded)
local session = {
  errors = 0, lastError = nil,
  looted = {},                -- "<source GUID>|<src>" looted this session (memory only, never saved)
  gather = nil,               -- { kind = "skin"|"mine"|"herb"|"fish", at = GetTime(), cast = castGUID } from UNIT_SPELLCAST_SUCCEEDED
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
}

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
      local itemId = itemIdFromLink(try(GetLootSlotLink, slot))
      if itemId then items[#items + 1] = { id = itemId, c = c > 0 and c or 1 } end
    end
  end
  if #items == 0 and money <= 0 then return end
  session.looted[key] = true
  local m, x, y = playerPos()
  pushLoot({ q = nextSeq(), t = now(), src = "fish", id = m or 0, m = m, x = x, y = y, items = items, money = money > 0 and money or nil })
  touch("loot", "fishing")
end

local function onLootOpened()
  local n = num(try(GetNumLootItems)) or 0
  if n <= 0 then return end
  local moneyType0 = (Enum and Enum.LootSlotType and Enum.LootSlotType.Money) or 2
  if clean(try(IsFishingLoot)) then return lootFishing(n, moneyType0) end
  -- A finished Skinning/Mining/Herb Gathering cast just before this loot window: loot from a creature is
  -- that gathering ("skin"/"mine"/"herb"), not its ordinary drop. The cast belongs to this loot window only.
  local g = recentGather()
  session.gather = nil
  local gatherSrc = g and g.kind ~= "fish" and g.kind or nil
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
  for _, sg in ipairs(order) do
    local kind, id = parseGuid(sg)
    local src = kind
    if kind == "npc" and gatherSrc then src = gatherSrc end
    -- Dedup per (GUID, src): ordinary loot and skinning of the same corpse are two entries; neither blocks the other.
    local key = sg .. "|" .. tostring(src)
    if not session.looted[key] then
      session.looted[key] = true
      local s = sources[sg]
      if kind and (#s.items > 0 or s.money > 0) then
        pushLoot({ q = nextSeq(), t = now(), src = src, id = id, items = s.items, money = s.money > 0 and s.money or nil })
        touch("loot", tostring(id))
        if kind == "object" then
          local name = (t - (session.sentAt or 0) <= NAME_WINDOW) and session.sentName or nil
          session.sentName, session.sentAt = nil, 0   -- used once (L13)
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

local function noteMob(unit)
  if inCombat() then return end
  local guid = str(try(UnitGUID, unit))
  local kind, id = parseGuid(guid)
  if kind ~= "npc" then return end            -- players' and pets' GUIDs are not even remembered
  local t = num(try(GetTime)) or 0
  local last = session.mobSeen[guid]
  if last and t - last < MOB_REPEAT then return end
  if not last then
    if session.mobSeenN >= MOB_GUIDS then session.mobSeen = {}; session.mobSeenN = 0 end
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
  local l = num(try(UnitLevel, unit))
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
  if addArea(rec) or isNew then touch("mob", name) end
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
  elseif kind == PIT.MailInfo then onMailShow() end
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
  for _, k in ipairs({ "npcs", "trainers", "vendors", "quests", "objects", "loot", "flights", "mobs", "mail" }) do
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
    count(db.objects), #db.loot, count(db.flights), count(db.mobs), #db.mail,
  }
  local total = 0
  for _, v in ipairs(c) do total = total + v end
  say(string.format(L.status_head, VERSION, total, db.seq or 0))
  if total == 0 then say(L.status_none); return end
  say(string.format(L.status_counts, c[1], c[2], c[3], c[4], c[5], c[6], c[7], c[8], c[9]))
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
      for _, k in ipairs({ "npcs", "trainers", "vendors", "quests", "objects", "loot", "flights", "mobs", "mail" }) do db[k] = {} end
      db.char = nil
      session.looted = {}
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
  QUEST_LOG_UPDATE = queueObjectiveRefresh,
  ITEM_DATA_LOAD_RESULT = queueObjectiveRefresh,
  QUEST_PROGRESS = function() onQuestEnd(false) end,
  QUEST_COMPLETE = function() onQuestEnd(true) end,
  TAXIMAP_OPENED = onTaxiMapOpened,
  BANKFRAME_OPENED = function() noteUnit("npc", nil) end,
  -- 1.0.4: mobs you only look at, mailboxes and service NPCs (auction house, stable, spirit healer).
  PLAYER_TARGET_CHANGED = function() noteMob("target") end,
  UPDATE_MOUSEOVER_UNIT = function() noteMob("mouseover") end,
  MAIL_SHOW = onMailShow,
  AUCTION_HOUSE_SHOW = function() noteUnit("npc", { auction = true }) end,
  PET_STABLE_SHOW = function() noteUnit("npc", { stable = true }) end,
  PLAYER_INTERACTION_MANAGER_FRAME_SHOW = onInteractionShow,
  UNIT_SPELLCAST_SENT = onSpellcastSent,
  UNIT_SPELLCAST_SUCCEEDED = onSpellcastSucceeded,
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
ns._test = {
  session = session, parseGuid = parseGuid, clean = clean, db = function() return db end,
  noteMob = noteMob, wildNpc = wildNpc, onSpellcastSent = onSpellcastSent, onLootOpened = onLootOpened,
  onMailShow = onMailShow, onInteractionShow = onInteractionShow, handlers = handlers,
}
