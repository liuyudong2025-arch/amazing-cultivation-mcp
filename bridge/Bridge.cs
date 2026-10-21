using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Reflection;
using FairyGUI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using WebSocketSharp;
using XiaWorld;

namespace AmazingCultivationMCP
{
    public static class Bridge
    {
        private static readonly object Sync = new object();
        private static readonly Queue<string> Incoming = new Queue<string>();
        private static readonly Dictionary<string, WeakReference> UiTargets = new Dictionary<string, WeakReference>();
        private static readonly Dictionary<string, JObject> CompletedRequests = new Dictionary<string, JObject>();
        private static readonly Queue<string> CompletedRequestOrder = new Queue<string>();
        private static readonly List<JObject> EventJournal = new List<JObject>();
        private static WebSocket socket;
        private static float reconnectAt;
        private static string sessionId;
        private static object lastWorld;
        private static int worldEpoch;
        private static long snapshotSequence;
        private static string lastTransportError;
        private static string explicitConnectionPath;
        private static BridgeRunner runner;
        private static string sequenceId;
        private static float sequenceExpiresAt;
        private static long eventSequence;

        public static void Initialize()
        {
            if (sessionId == null) {
                sessionId = Guid.NewGuid().ToString("N");
                reconnectAt = 0;
                Debug.Log("[AmazingCultivationMCP] bridge initialized");
            }
            if (runner == null) {
                GameObject host = new GameObject("AmazingCultivationMCP.BridgeRunner");
                UnityEngine.Object.DontDestroyOnLoad(host);
                runner = host.AddComponent<BridgeRunner>();
            }
        }

        public static void Initialize(string configPath)
        {
            explicitConnectionPath = configPath;
            Initialize();
        }

        public static string Diagnostic()
        {
            string path = ConnectionPath();
            return "session=" + (sessionId == null ? "none" : sessionId) +
                ", config=" + path +
                ", exists=" + File.Exists(path) +
                ", socket=" + (socket == null ? "none" : socket.ReadyState.ToString()) +
                ", error=" + (String.IsNullOrEmpty(lastTransportError) ? "none" : lastTransportError);
        }

        public static void Update()
        {
            if (sessionId == null) Initialize();
            object currentWorld = World.Instance;
            if (!System.Object.ReferenceEquals(currentWorld, lastWorld)) {
                lastWorld = currentWorld;
                worldEpoch++;
                UiTargets.Clear();
            }
            if ((socket == null || !socket.IsAlive) && Time.realtimeSinceStartup >= reconnectAt) {
                reconnectAt = Time.realtimeSinceStartup + 5f;
                TryConnect();
            }
            for (int i = 0; i < 32; i++) {
                string message = null;
                System.Threading.Monitor.Enter(Sync);
                try { if (Incoming.Count > 0) message = Incoming.Dequeue(); }
                finally { System.Threading.Monitor.Exit(Sync); }
                if (message == null) break;
                Process(message);
            }
        }

        public static void Shutdown()
        {
            WebSocket prior = socket;
            socket = null;
            if (prior != null) { try { prior.Close(); } catch { } }
            System.Threading.Monitor.Enter(Sync);
            try { Incoming.Clear(); }
            finally { System.Threading.Monitor.Exit(Sync); }
            UiTargets.Clear();
            if (runner != null) {
                BridgeRunner priorRunner = runner;
                runner = null;
                try { UnityEngine.Object.Destroy(priorRunner.gameObject); } catch { }
            }
            sessionId = null;
        }

        private static string ConnectionPath()
        {
            if (!String.IsNullOrEmpty(explicitConnectionPath)) return explicitConnectionPath;
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmazingCultivationMCP");
            return Path.Combine(directory, "bridge.json");
        }

        private static void TryConnect()
        {
            try {
                string configPath = ConnectionPath();
                if (!File.Exists(configPath)) {
                    lastTransportError = "Runtime connection file not found: " + configPath;
                    Debug.LogError("[AmazingCultivationMCP] " + lastTransportError);
                    return;
                }
                JObject config = JObject.Parse(File.ReadAllText(configPath));
                string url = (string)config["wsUrl"];
                string token = (string)config["bridgeToken"];
                if (String.IsNullOrEmpty(url) || String.IsNullOrEmpty(token)) throw new InvalidDataException("bridge.json is incomplete");
                WebSocket next = new WebSocket(url + "?token=" + Uri.EscapeDataString(token));
                next.OnMessage += delegate(object sender, MessageEventArgs e) {
                    if (!e.IsText) return;
                    System.Threading.Monitor.Enter(Sync);
                    try { Incoming.Enqueue(e.Data); }
                    finally { System.Threading.Monitor.Exit(Sync); }
                };
                next.OnOpen += delegate { lastTransportError = null; Debug.Log("[AmazingCultivationMCP] runtime connected"); };
                next.OnError += delegate(object sender, WebSocketSharp.ErrorEventArgs e) {
                    lastTransportError = e.Message;
                    Debug.LogError("[AmazingCultivationMCP] WebSocket error: " + e.Message);
                };
                next.OnClose += delegate(object sender, CloseEventArgs e) {
                    if (System.Object.ReferenceEquals(socket, next)) socket = null;
                    Debug.LogWarning("[AmazingCultivationMCP] WebSocket closed: " + e.Code + " " + e.Reason);
                };
                socket = next;
                Debug.Log("[AmazingCultivationMCP] connecting to local runtime");
                next.Connect();
            } catch (Exception ex) {
                lastTransportError = ex.ToString();
                Debug.LogError("[AmazingCultivationMCP] connection failed: " + ex);
            }
        }

        private static void Process(string text)
        {
            JObject request = null;
            string id = null;
            JObject response = new JObject();
            try {
                request = JObject.Parse(text);
                id = (string)request["id"];
                string method = (string)request["method"];
                JObject args = request["args"] as JObject ?? new JObject();
                string requestId = (string)request["requestId"] ?? (string)args["requestId"];
                JObject completed;
                if (!String.IsNullOrEmpty(requestId) && IsMutation(method) && CompletedRequests.TryGetValue(requestId, out completed)) {
                    response = (JObject)completed.DeepClone();
                    response["id"] = id;
                    JObject replayMeta = response["meta"] as JObject ?? new JObject();
                    replayMeta["replayed"] = true;
                    replayMeta["requestId"] = requestId;
                    response["meta"] = replayMeta;
                    try { if (socket != null && socket.IsAlive) socket.SendString(response.ToString(Formatting.None)); }
                    catch (Exception ex) { lastTransportError = ex.Message; }
                    return;
                }
                CheckSequence(method, args);
                object data = Invoke(method, args);
                response["id"] = id;
                response["success"] = true;
                response["meta"] = JToken.FromObject(Meta());
                response["data"] = data == null ? JValue.CreateNull() : JToken.FromObject(data);
                if (!String.IsNullOrEmpty(requestId) && IsMutation(method)) CacheCompleted(requestId, response);
                if (IsMutation(method)) RecordEvent(method, requestId, true, response["data"]);
            } catch (Exception ex) {
                response["id"] = id;
                response["success"] = false;
                response["meta"] = JToken.FromObject(Meta());
                JObject error = new JObject();
                error["code"] = ex is RpcException ? ((RpcException)ex).Code : "INTERNAL_ERROR";
                error["message"] = ex.Message;
                response["error"] = error;
                string method = request == null ? null : (string)request["method"];
                JObject args = request == null ? null : request["args"] as JObject;
                string requestId = request == null ? null : ((string)request["requestId"] ?? (args == null ? null : (string)args["requestId"]));
                if (!String.IsNullOrEmpty(requestId) && IsMutation(method)) CacheCompleted(requestId, response);
                if (IsMutation(method)) RecordEvent(method, requestId, false, error);
            }
            try { if (socket != null && socket.IsAlive) socket.SendString(response.ToString(Formatting.None)); }
            catch (Exception ex) { lastTransportError = ex.Message; }
        }

        private static object Invoke(string method, JObject args)
        {
            switch (method) {
                case "session.status": return Status();
                case "session.sequence.begin": return BeginSequence(args);
                case "session.sequence.end": return EndSequence(args);
                case "session.result": return SessionResult(args);
                case "events.poll": return PollEvents(args);
                case "state.world": return WorldState();
                case "state.overview": return OverviewState(args);
                case "state.map": return MapState();
                case "state.npcs": return NpcState(args);
                case "state.npc": return NpcDetail(args);
                case "state.npcProfile": return NpcProfileState(args);
                case "state.npcCultivation": return NpcCultivationState(args);
                case "state.breakthrough": return BreakthroughState(args);
                case "state.bodyCultivation": return BodyCultivationState(args);
                case "state.esotericaCatalog": return EsotericaCatalogState(args);
                case "state.spellCatalog": return SpellCatalogState(args);
                case "state.skillTree": return SkillTreeState(args);
                case "state.godCultivation": return GodCultivationState(args);
                case "state.godBuildingCatalog": return GodBuildingCatalogState(args);
                case "state.interactions": return InteractionState(args);
                case "state.npcSkills": return NpcSkillsState(args);
                case "state.npcHealth": return NpcHealthState(args);
                case "state.npcInventory": return NpcInventoryState(args);
                case "state.item": return ItemState(args);
                case "state.refiningForecast": return RefiningForecastState(args);
                case "state.combat": return CombatState(args);
                case "state.buildCatalog": return BuildCatalogState(args);
                case "state.building": return BuildingState(args);
                case "state.productionCatalog": return ProductionCatalogState(args);
                case "state.recipes": return RecipeState(args);
                case "state.formations": return FormationState(args);
                case "state.formationCatalog": return FormationCatalogState(args);
                case "state.areas": return AreaState(args);
                case "state.areaCatalog": return AreaCatalogState();
                case "state.trade": return TradeState(args);
                case "state.tradeWindow": return TradeWindowState();
                case "state.worldMap": return WorldMapState(args);
                case "state.outspread": return OutspreadState(args);
                case "state.outspreadCatalog": return OutspreadCatalogState(args);
                case "state.dlc": return DlcState();
                case "state.storyChoice": return StoryChoiceState();
                case "state.grid": return GridState(args);
                case "state.qiField": return QiFieldState(args);
                case "state.fengShui": return FengShuiState(args);
                case "state.population": return PopulationState(args);
                case "state.things": return ThingsState(args);
                case "state.resources": return ResourceState(args);
                case "state.school": return SchoolState();
                case "state.selection": return SelectionState();
                case "state.enums": return EnumState();
                case "state.alerts": return AlertState(args);
                case "state.commandTypes": return CommandTypesState(args);
                case "state.commands": return CommandsState(args);
                case "state.input": return MapInputState();
                case "game.focusNpc": return FocusNpc(args);
                case "game.focusThing": return FocusThing(args);
                case "game.lookGrid": return LookGrid(args);
                case "game.openPanel": return OpenPanel(args);
                case "game.save": return SaveGame(args);
                case "game.setWorkPriority": return SetWorkPriority(args);
                case "game.setWorkEnabled": return SetWorkEnabled(args);
                case "game.setVipWork": return SetVipWork(args);
                case "game.interruptNpcJob": return InterruptNpcJob(args);
                case "game.setSchedule": return SetSchedule(args);
                case "game.setSchoolMaster": return SetSchoolMaster(args);
                case "game.setPracticeMode": return SetPracticeMode(args);
                case "game.setPracticeBehaviour": return SetPracticeBehaviour(args);
                case "game.changeGong": return ChangeGong(args);
                case "game.promoteInnerDisciple": return PromoteInnerDisciple(args);
                case "game.learnSkillTreeNode": return LearnSkillTreeNode(args);
                case "game.beginBreakthrough": return BeginBreakthrough(args);
                case "game.useItem": return UseItem(args);
                case "game.equipItem": return EquipItem(args);
                case "game.unequipItem": return UnequipItem(args);
                case "game.assignBed": return AssignBed(args);
                case "game.assignPracticePlace": return AssignPracticePlace(args);
                case "game.huntAnimal": return HuntAnimal(args);
                case "game.slaughterAnimal": return SlaughterAnimal(args);
                case "game.moveNpcTo": return MoveNpcTo(args);
                case "game.talkToNpc": return TalkToNpc(args);
                case "game.performTalkAction": return PerformTalkAction(args);
                case "game.openGodCity": return OpenGodCity(args);
                case "game.createGodBuilding": return CreateGodBuilding(args);
                case "game.upgradeGodBuilding": return UpgradeGodBuilding(args);
                case "game.removeGodBuilding": return RemoveGodBuilding(args);
                case "game.toggleGodBuilding": return ToggleGodBuilding(args);
                case "game.setGodBuildingGuard": return SetGodBuildingGuard(args);
                case "game.refineArtifact": return RefineArtifact(args);
                case "game.changeBodyAttitude": return ChangeBodyAttitude(args);
                case "game.beginBodyQuenching": return BeginBodyQuenching(args);
                case "game.cancelBodyQuenching": return CancelBodyQuenching(args);
                case "game.attackTarget": return AttackTarget(args);
                case "game.stopCombat": return StopCombat(args);
                case "game.castSkill": return CastSkill(args);
                case "game.setAutoSkills": return SetAutoSkills(args);
                case "game.castArtifacts": return CastArtifacts(args);
                case "game.beginBuild": return BeginBuild(args);
                case "game.placeBuilding": return PlaceBuilding(args);
                case "game.moveBuilding": return MoveBuilding(args);
                case "game.changeBuildingMaterial": return ChangeBuildingMaterial(args);
                case "game.dismantleBuilding": return DismantleBuilding(args);
                case "game.addProductionTask": return AddProductionTask(args);
                case "game.removeProductionTask": return RemoveProductionTask(args);
                case "game.createFormation": return CreateFormation(args);
                case "game.cancelFormation": return CancelFormation(args);
                case "game.createFormationShape": return CreateFormationShape(args);
                case "game.removeFormationShape": return RemoveFormationShape(args);
                case "game.saveFormationScheme": return SaveFormationScheme(args);
                case "game.removeFormationScheme": return RemoveFormationScheme(args);
                case "game.apprentice": return Apprentice(args);
                case "game.teachSecret": return TeachSecret(args);
                case "game.createArea": return CreateArea(args);
                case "game.addAreaGrid": return AddAreaGrid(args);
                case "game.removeAreaGrid": return RemoveAreaGrid(args);
                case "game.configurePlantArea": return ConfigurePlantArea(args);
                case "game.configureStorageArea": return ConfigureStorageArea(args);
                case "game.configureGuardArea": return ConfigureGuardArea(args);
                case "game.removeArea": return RemoveArea(args);
                case "game.openSchoolTrade": return OpenSchoolTrade(args);
                case "game.confirmTrade": return ConfirmTrade(args);
                case "game.openWorldMap": return OpenWorldMap();
                case "game.startExplore": return StartExplore(args);
                case "game.recallExplorer": return RecallExplorer(args);
                case "game.chooseStoryOption": return ChooseStoryOption(args);
                case "game.openOutspreadRegion": return OpenOutspreadRegion(args);
                case "game.setOutspreadPolicy": return SetOutspreadPolicy(args);
                case "game.setOutspreadBuild": return SetOutspreadBuild(args);
                case "game.cancelOutspreadBuild": return CancelOutspreadBuild(args);
                case "game.setOutspreadChief": return SetOutspreadChief(args);
                case "game.sendOutspreadMember": return SendOutspreadMember(args);
                case "game.removeOutspreadMember": return RemoveOutspreadMember(args);
                case "game.startOutspreadSupervision": return StartOutspreadSupervision(args);
                case "game.collectOutspreadProducts": return CollectOutspreadProducts(args);
                case "game.selectCommand": return SelectCommand(args);
                case "game.addThingCommand": return AddThingCommand(args);
                case "game.addGridCommand": return AddGridCommand(args);
                case "game.removeThingCommand": return RemoveThingCommand(args);
                case "map.click": return MapClick(args);
                case "map.drag": return MapDrag(args);
                case "ui.snapshot": return UiSnapshot(args);
                case "ui.input": return UiInput(args);
                case "runtime.pause": {
                    MainManager manager = MainManager.Instance;
                    if (manager == null) throw new RpcException("GAME_NOT_READY", "MainManager is unavailable");
                    manager.Pause(false);
                    return new { path = "MainManager.Pause", inputProcessed = true, commandAccepted = true, paused = !manager.Runing, gameSpeed = manager.gamespeed };
                }
                case "runtime.speed": {
                    float value = args.Value<float?>("value") ?? 1f;
                    if (value < 0 || value > 10) throw new RpcException("INVALID_ARGUMENT", "value must be between 0 and 10");
                    MainManager manager = MainManager.Instance;
                    if (manager == null) throw new RpcException("GAME_NOT_READY", "MainManager is unavailable");
                    if (value == 0) manager.Pause(false);
                    else {
                        manager.ChangeSpeed(value);
                        if (!manager.Runing) manager.Play(value, false);
                    }
                    return new { path = value == 0 ? "MainManager.Pause" : "MainManager.ChangeSpeed", inputProcessed = true, commandAccepted = true, paused = !manager.Runing, requestedSpeed = value, gameSpeed = manager.gamespeed };
                }
                default: throw new RpcException("UNKNOWN_METHOD", "Unknown method: " + method);
            }
        }

        private static object Meta()
        {
            World world = World.Instance;
            return new {
                sessionId = sessionId,
                worldEpoch = worldEpoch,
                worldSaveId = world == null ? (int?)null : world.SaveID,
                frame = Time.frameCount,
                realtime = Time.realtimeSinceStartup,
                dayCount = world == null ? (int?)null : world.DayCount
            };
        }

        private static object Status()
        {
            World world = World.Instance;
            return new {
                bridge = "connected",
                gameLoaded = world != null,
                mapLoaded = world != null && world.map != null,
                transportError = lastTransportError,
                activeSequenceId = SequenceActive() ? sequenceId : null,
                methods = new[] { "session.status", "session.sequence.begin", "session.sequence.end", "session.result", "events.poll", "state.overview", "state.world", "state.map", "state.npcs", "state.npc", "state.npcProfile", "state.breakthrough", "state.bodyCultivation", "state.esotericaCatalog", "state.spellCatalog", "state.godCultivation", "state.interactions", "state.npcCultivation", "state.npcSkills", "state.npcHealth", "state.npcInventory", "state.item", "state.combat", "state.buildCatalog", "state.building", "state.productionCatalog", "state.recipes", "state.formations", "state.formationCatalog", "state.areas", "state.areaCatalog", "state.trade", "state.tradeWindow", "state.worldMap", "state.storyChoice", "state.grid", "state.qiField", "state.fengShui", "state.population", "state.things", "state.resources", "state.school", "state.selection", "state.enums", "state.alerts", "state.commandTypes", "state.commands", "state.input", "game.focusNpc", "game.focusThing", "game.lookGrid", "game.openPanel", "game.save", "game.setWorkPriority", "game.setWorkEnabled", "game.setVipWork", "game.interruptNpcJob", "game.setSchedule", "game.setSchoolMaster", "game.setPracticeMode", "game.setPracticeBehaviour", "game.changeGong", "game.promoteInnerDisciple", "game.beginBreakthrough", "game.useItem", "game.equipItem", "game.unequipItem", "game.assignBed", "game.assignPracticePlace", "game.huntAnimal", "game.slaughterAnimal", "game.talkToNpc", "game.performTalkAction", "game.openGodCity", "game.refineArtifact", "game.changeBodyAttitude", "game.beginBodyQuenching", "game.cancelBodyQuenching", "game.attackTarget", "game.stopCombat", "game.castSkill", "game.setAutoSkills", "game.castArtifacts", "game.beginBuild", "game.placeBuilding", "game.moveBuilding", "game.changeBuildingMaterial", "game.dismantleBuilding", "game.addProductionTask", "game.removeProductionTask", "game.createFormation", "game.cancelFormation", "game.createFormationShape", "game.removeFormationShape", "game.saveFormationScheme", "game.removeFormationScheme", "game.apprentice", "game.teachSecret", "game.createArea", "game.addAreaGrid", "game.removeAreaGrid", "game.configurePlantArea", "game.configureStorageArea", "game.configureGuardArea", "game.removeArea", "game.openSchoolTrade", "game.confirmTrade", "game.openWorldMap", "game.startExplore", "game.recallExplorer", "game.chooseStoryOption", "game.selectCommand", "game.addThingCommand", "game.addGridCommand", "game.removeThingCommand", "map.click", "map.drag", "ui.snapshot", "ui.input", "runtime.pause", "runtime.speed" }
            };
        }

        private static bool IsMutation(string method)
        {
            if (String.IsNullOrEmpty(method)) return false;
            return method.StartsWith("game.", StringComparison.Ordinal) || method.StartsWith("map.", StringComparison.Ordinal) || method == "ui.input" || method.StartsWith("runtime.", StringComparison.Ordinal);
        }

        private static bool SequenceActive()
        {
            if (String.IsNullOrEmpty(sequenceId)) return false;
            if (Time.realtimeSinceStartup <= sequenceExpiresAt) return true;
            sequenceId = null;
            return false;
        }

        private static void CheckSequence(string method, JObject args)
        {
            if (!IsMutation(method) || !SequenceActive()) return;
            if ((string)args["sequenceId"] != sequenceId) throw new RpcException("SEQUENCE_LOCKED", "A UI/action sequence is active; pass its sequenceId or wait for the lease to expire");
            sequenceExpiresAt = Time.realtimeSinceStartup + 15f;
        }

        private static object BeginSequence(JObject args)
        {
            string requested = (string)args["sequenceId"];
            if (SequenceActive() && requested != sequenceId) throw new RpcException("SEQUENCE_LOCKED", "Another sequence lease is active");
            int leaseMs = Math.Max(1000, Math.Min(60000, args.Value<int?>("leaseMs") ?? 15000));
            if (String.IsNullOrEmpty(sequenceId)) sequenceId = Guid.NewGuid().ToString("N");
            sequenceExpiresAt = Time.realtimeSinceStartup + leaseMs / 1000f;
            return new { sequenceId = sequenceId, leaseMs = leaseMs, expiresAtRealtime = sequenceExpiresAt };
        }

        private static object EndSequence(JObject args)
        {
            string requested = (string)args["sequenceId"];
            if (!SequenceActive()) return new { released = false, reason = "no-active-sequence" };
            if (requested != sequenceId) throw new RpcException("SEQUENCE_MISMATCH", "sequenceId does not own the active lease");
            string released = sequenceId;
            sequenceId = null;
            sequenceExpiresAt = 0;
            return new { released = true, sequenceId = released };
        }

        private static void CacheCompleted(string requestId, JObject response)
        {
            JObject stored = (JObject)response.DeepClone();
            stored.Remove("id");
            if (!CompletedRequests.ContainsKey(requestId)) CompletedRequestOrder.Enqueue(requestId);
            CompletedRequests[requestId] = stored;
            while (CompletedRequestOrder.Count > 256) CompletedRequests.Remove(CompletedRequestOrder.Dequeue());
        }

        private static object SessionResult(JObject args)
        {
            string requestId = ((string)args["requestId"] ?? "").Trim();
            if (requestId.Length == 0) throw new RpcException("INVALID_ARGUMENT", "requestId is required");
            JObject result;
            if (!CompletedRequests.TryGetValue(requestId, out result)) return new { requestId = requestId, found = false };
            return new { requestId = requestId, found = true, result = result };
        }

        private static void RecordEvent(string method, string requestId, bool success, JToken result)
        {
            JObject entry = new JObject();
            entry["sequence"] = ++eventSequence;
            entry["kind"] = "action";
            entry["method"] = method;
            entry["requestId"] = requestId == null ? JValue.CreateNull() : new JValue(requestId);
            entry["success"] = success;
            entry["frame"] = Time.frameCount;
            entry["result"] = result == null ? JValue.CreateNull() : result.DeepClone();
            EventJournal.Add(entry);
            if (EventJournal.Count > 512) EventJournal.RemoveAt(0);
        }

        private static object PollEvents(JObject args)
        {
            long after = args.Value<long?>("after") ?? 0;
            int limit = Math.Max(1, Math.Min(500, args.Value<int?>("limit") ?? 100));
            List<JObject> items = new List<JObject>();
            foreach (JObject entry in EventJournal) {
                if ((long)entry["sequence"] <= after) continue;
                items.Add((JObject)entry.DeepClone());
                if (items.Count >= limit) break;
            }
            return new { items = items, count = items.Count, cursor = items.Count == 0 ? after : (long)items[items.Count - 1]["sequence"], latest = eventSequence };
        }

        private static World RequireWorld()
        {
            World world = World.Instance;
            if (world == null) throw new RpcException("GAME_NOT_READY", "Load or create a world first");
            return world;
        }

        private static Map RequireMap()
        {
            World world = RequireWorld();
            if (world.map == null) throw new RpcException("MAP_NOT_READY", "No active map is loaded");
            return world.map;
        }

        private static object WorldState()
        {
            World world = RequireWorld();
            MainManager manager = MainManager.Instance;
            return new {
                saveId = world.SaveID,
                dayCount = world.DayCount,
                yearDayCount = world.YearDayCount,
                yearCount = world.YearCount,
                dayHour = world.DayHour,
                daySecond = world.DaySecond,
                frameCount = world.FrameCount,
                gameMode = world.GameMode.ToString(),
                paused = manager == null ? Time.timeScale == 0 : !manager.Runing,
                gameSpeed = manager == null ? Time.timeScale : manager.gamespeed,
                unityTimeScale = Time.timeScale
            };
        }

        private static object MapState()
        {
            Map map = RequireMap();
            int npcCount = map.Things == null || map.Things.m_lisNpcs == null ? 0 : map.Things.m_lisNpcs.Count;
            List<Npc> activeNpcs = map.Things == null ? null : map.Things.GetPlayerActiveNpcs(g_emNpcRaceType.Wisdom);
            int activeNpcCount = activeNpcs == null ? 0 : activeNpcs.Count;
            Dictionary<string, int> counts = new Dictionary<string, int>();
            if (map.Things != null && map.Things.m_TypeThings != null) {
                foreach (KeyValuePair<g_emThingType, List<Thing>> pair in map.Things.m_TypeThings)
                    counts[pair.Key.ToString()] = pair.Value == null ? 0 : pair.Value.Count;
            }
            return new { size = map.Size, npcCount = npcCount, activeNpcCount = activeNpcCount, thingsByType = counts };
        }

        private static object OverviewState(JObject args)
        {
            int npcLimit = Math.Max(1, Math.Min(100, args.Value<int?>("npcLimit") ?? 30));
            int alertLimit = Math.Max(1, Math.Min(100, args.Value<int?>("alertLimit") ?? 30));
            JObject resourceArgs = new JObject(); resourceArgs["limit"] = 30;
            JObject npcArgs = new JObject(); npcArgs["limit"] = npcLimit;
            JObject alertArgs = new JObject(); alertArgs["limit"] = alertLimit;
            return new {
                world = WorldState(),
                map = MapState(),
                school = SchoolState(),
                resources = ResourceState(resourceArgs),
                npcs = NpcState(npcArgs),
                alerts = AlertState(alertArgs),
                selection = SelectionState()
            };
        }

        private static int ResolveGridKey(JObject args, Map map)
        {
            int? key = args.Value<int?>("key");
            if (key.HasValue) {
                if (!GridMgr.Inst.KeyVaild(key.Value)) throw new RpcException("INVALID_GRID", "key is outside the active map");
                return key.Value;
            }
            int? x = args.Value<int?>("x");
            int? y = args.Value<int?>("y");
            if (x.HasValue || y.HasValue) {
                if (!x.HasValue || !y.HasValue) throw new RpcException("INVALID_ARGUMENT", "x and y must be supplied together");
                int xyKey = GridMgr.Inst.P2Key(x.Value, y.Value, false);
                if (!GridMgr.Inst.KeyVaild(xyKey)) throw new RpcException("INVALID_GRID", "x/y is outside the active map");
                return xyKey;
            }
            if (UI_WorldLayer.Instance == null) throw new RpcException("UI_NOT_READY", "Pass key or x/y because the world layer is unavailable");
            int mouseKey = UI_WorldLayer.Instance.MouseGridKey;
            if (!GridMgr.Inst.KeyVaild(mouseKey)) throw new RpcException("INVALID_GRID", "The pointer is not over a valid map grid");
            return mouseKey;
        }

        private static object ThingSummary(Thing thing)
        {
            if (thing == null) return null;
            return new {
                reference = sessionId + ":" + worldEpoch + ":thing:" + thing.ID,
                worldEpoch = worldEpoch,
                id = thing.ID,
                name = SafeString(delegate { return thing.GetName(); }),
                defName = SafeString(delegate { return thing.def == null ? null : thing.def.Name; }),
                type = thing.ThingType.ToString(),
                key = thing.Key,
                hp = thing.Hp,
                maxHp = thing.MaxHp,
                count = thing.Count,
                element = SafeString(delegate { return thing.ElementKind.ToString(); }),
                selected = thing.IsSelected,
                playerThing = thing.IsPlayerThing,
                camp = SafeString(delegate { return thing.Camp.ToString(); })
            };
        }

        private static object GridState(JObject args)
        {
            Map map = RequireMap();
            int key = ResolveGridKey(args, map);
            int[] xy = GridMgr.Inst.Key2P(key);
            float[] elements = map.GetElement(key);
            float[] proportions = map.GetElementProportion(key);
            List<object> things = new List<object>();
            List<Thing> atGrid = map.Things.GetThingsAtGrid(key);
            if (atGrid != null) foreach (Thing thing in atGrid) if (thing != null) things.Add(ThingSummary(thing));
            return new {
                key = key,
                x = xy != null && xy.Length > 0 ? (int?)xy[0] : null,
                y = xy != null && xy.Length > 1 ? (int?)xy[1] : null,
                terrain = SafeString(delegate { return map.Terrain.GetTerrainName(key, false); }),
                terrainDetail = SafeString(delegate { return map.Terrain.GetTerrainName(key, true); }),
                ling = map.GetLing(key),
                temperature = map.GetTemperature(key),
                fertility = map.GetFertility(key),
                beauty = map.GetBeauty(key, true),
                light = map.GetLight(key),
                elements = elements,
                elementProportions = proportions,
                things = things
            };
        }

        private static object QiFieldState(JObject args)
        {
            Map map = RequireMap();
            int centerKey = ResolveGridKey(args, map);
            int[] center = GridMgr.Inst.Key2P(centerKey);
            int radius = Math.Max(0, Math.Min(20, args.Value<int?>("radius") ?? 6));
            int maxPoints = Math.Max(1, Math.Min(1681, args.Value<int?>("limit") ?? 1681));
            List<JObject> points = new List<JObject>();
            double totalLing = 0;
            float minLing = Single.MaxValue;
            float maxLing = Single.MinValue;
            double[] elementTotals = null;
            for (int y = center[1] - radius; y <= center[1] + radius && points.Count < maxPoints; y++) {
                for (int x = center[0] - radius; x <= center[0] + radius && points.Count < maxPoints; x++) {
                    int key = GridMgr.Inst.P2Key(x, y, false);
                    if (!GridMgr.Inst.KeyVaild(key)) continue;
                    float ling = map.GetLing(key);
                    float[] elements = map.GetElement(key);
                    if (elements != null) {
                        if (elementTotals == null) elementTotals = new double[elements.Length];
                        for (int i = 0; i < elements.Length && i < elementTotals.Length; i++) elementTotals[i] += elements[i];
                    }
                    List<Thing> atGrid = map.Things.GetThingsAtGrid(key);
                    JArray occupants = new JArray();
                    if (atGrid != null) foreach (Thing thing in atGrid) if (thing != null) occupants.Add(JToken.FromObject(ThingSummary(thing)));
                    JObject point = new JObject();
                    point["key"] = key; point["x"] = x; point["y"] = y; point["distance"] = Math.Max(Math.Abs(x - center[0]), Math.Abs(y - center[1]));
                    point["ling"] = ling; point["elements"] = elements == null ? JValue.CreateNull() : JToken.FromObject(elements);
                    point["elementProportions"] = JToken.FromObject(map.GetElementProportion(key)); point["things"] = occupants;
                    points.Add(point);
                    totalLing += ling; minLing = Math.Min(minLing, ling); maxLing = Math.Max(maxLing, ling);
                }
            }
            points.Sort(delegate(JObject left, JObject right) { return right.Value<float>("ling").CompareTo(left.Value<float>("ling")); });
            int topCount = Math.Max(1, Math.Min(points.Count, args.Value<int?>("top") ?? 20));
            List<JObject> top = points.GetRange(0, topCount);
            return new {
                center = new { key = centerKey, x = center[0], y = center[1] }, radius = radius, count = points.Count,
                ling = new { min = points.Count == 0 ? 0 : minLing, max = points.Count == 0 ? 0 : maxLing, average = points.Count == 0 ? 0 : totalLing / points.Count },
                elementTotals = elementTotals, points = points, top = top, truncated = points.Count >= maxPoints
            };
        }

        private static object FengShuiState(JObject args)
        {
            Map map = RequireMap();
            int? centerKey = args.Value<int?>("key");
            if (!centerKey.HasValue && args.Value<int?>("x").HasValue && args.Value<int?>("y").HasValue) centerKey = ResolveGridKey(args, map);
            int[] center = centerKey.HasValue ? GridMgr.Inst.Key2P(centerKey.Value) : null;
            int radius = Math.Max(0, Math.Min(50, args.Value<int?>("radius") ?? 12));
            int limit = Math.Max(1, Math.Min(5000, args.Value<int?>("limit") ?? 1000));
            List<Thing> source = null;
            if (map.Things.m_TypeThings != null) map.Things.m_TypeThings.TryGetValue(g_emThingType.Building, out source);
            List<object> buildings = new List<object>();
            Dictionary<string, int> ranks = new Dictionary<string, int>();
            if (source != null) foreach (Thing thing in source) {
                BuildingThing building = thing as BuildingThing;
                if (building == null || buildings.Count >= limit) continue;
                int[] xy = GridMgr.Inst.Key2P(building.Key);
                if (center != null && (Math.Abs(xy[0] - center[0]) > radius || Math.Abs(xy[1] - center[1]) > radius)) continue;
                string rank = SafeString(delegate { return building.FengShui.ToString(); }) ?? "Unknown";
                ranks[rank] = ranks.ContainsKey(rank) ? ranks[rank] + 1 : 1;
                buildings.Add(new { x = xy[0], y = xy[1], distance = center == null ? (int?)null : Math.Max(Math.Abs(xy[0] - center[0]), Math.Abs(xy[1] - center[1])), summary = BuildingSummary(building), native = NativeScalars(building, 80) });
            }
            List<object> rooms = new List<object>();
            AreaMgr areaManager = AreaMgr.Instance;
            if (areaManager != null && areaManager.m_lisAreas != null) foreach (AreaBase area in areaManager.m_lisAreas) {
                if (area == null) continue;
                string type = area.GetType().Name;
                if (type.IndexOf("Room", StringComparison.OrdinalIgnoreCase) < 0 && type.IndexOf("Aura", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (center != null) {
                    bool nearby = false;
                    if (area.m_lisGrids != null) foreach (int roomKey in area.m_lisGrids) {
                        int[] roomXY = GridMgr.Inst.Key2P(roomKey);
                        if (roomXY != null && Math.Abs(roomXY[0] - center[0]) <= radius && Math.Abs(roomXY[1] - center[1]) <= radius) { nearby = true; break; }
                    }
                    if (!nearby) continue;
                }
                AreaRoom room = area as AreaRoom;
                rooms.Add(new {
                    area = AreaSummary(area),
                    room = room == null ? null : new {
                        connected = room.IsConnected, size = room.RoomSize, kind = room.RoomKind.ToString(),
                        fengShui = room.FengShui.ToString(), fengShuiValue = room.FengshuiValue,
                        fengShuiOutside = room.FengshuiOut, temperature = room.Temperature,
                        impression = room.ImpressionValue, layout = room.LayoutValue, beauty = room.BeautyValue,
                        dragonHolePenalty = room.DragonHoleSub,
                        owners = room.m_lisOwner == null ? new int[0] : room.m_lisOwner.ConvertAll<int>(delegate(Npc npc) { return npc == null ? 0 : npc.ID; }).ToArray(),
                        wallCount = room.m_lisWalls == null ? 0 : room.m_lisWalls.Count,
                        doorCount = room.m_lisDoors == null ? 0 : room.m_lisDoors.Count
                    },
                    native = NativeScalars(area, 120)
                });
            }
            return new { center = center == null ? null : new { key = centerKey, x = center[0], y = center[1] }, radius = center == null ? (int?)null : radius, buildings = buildings, buildingCount = buildings.Count, ranks = ranks, rooms = rooms, roomCount = rooms.Count, truncated = buildings.Count >= limit };
        }

        private static Npc RequireNpc(int id)
        {
            Npc npc = RequireMap().Things.GetNpcByID(id);
            if (npc == null) throw new RpcException("NPC_NOT_FOUND", "No NPC exists with id " + id);
            return npc;
        }

        private static Thing FindThing(int id)
        {
            Map map = RequireMap();
            if (map.Things == null) return null;
            Npc npc = map.Things.GetNpcByID(id);
            if (npc != null) return npc;
            if (map.Things.m_TypeThings != null) {
                foreach (KeyValuePair<g_emThingType, List<Thing>> pair in map.Things.m_TypeThings) {
                    if (pair.Value == null) continue;
                    foreach (Thing thing in pair.Value) if (thing != null && thing.ID == id) return thing;
                }
            }
            return null;
        }

        private static Thing RequireThing(int id)
        {
            Thing thing = FindThing(id);
            if (thing == null) throw new RpcException("THING_NOT_FOUND", "No map object exists with id " + id);
            return thing;
        }

        private static object FocusNpc(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            Npc npc = RequireNpc(id.Value);
            if (MapCamera.Instance == null || Wnd_GameMain.Instance == null) throw new RpcException("UI_NOT_READY", "The main map UI is unavailable");
            MapCamera.Instance.LookKey(npc.Key);
            Wnd_GameMain.Instance.ShowThingInfo(npc, null);
            return new { commandAccepted = true, completion = "dispatched", npc = ThingSummary(npc), action = "camera-and-info-panel" };
        }

        private static object FocusThing(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            Thing thing = RequireThing(id.Value);
            Npc npc = thing as Npc;
            if (npc != null) return FocusNpc(new JObject(new JProperty("id", npc.ID)));
            if (MapCamera.Instance == null || Wnd_GameMain.Instance == null) throw new RpcException("UI_NOT_READY", "The main map UI is unavailable");
            MapCamera.Instance.LookKey(thing.Key);
            bool panelOpened = true;
            string panelError = null;
            try { Wnd_GameMain.Instance.ShowThingInfo(thing, null); }
            catch (Exception ex) { panelOpened = false; panelError = ex.Message; }
            return new { commandAccepted = true, completion = "dispatched", thing = ThingSummary(thing), action = panelOpened ? "camera-and-info-panel" : "camera", panelOpened = panelOpened, panelError = panelError };
        }

        private static object LookGrid(JObject args)
        {
            Map map = RequireMap();
            int key = ResolveGridKey(args, map);
            if (MapCamera.Instance == null) throw new RpcException("UI_NOT_READY", "MapCamera is unavailable");
            MapCamera.Instance.LookKey(key);
            return new { commandAccepted = true, completion = "dispatched", key = key };
        }

        private static object OpenPanel(JObject args)
        {
            string panel = ((string)args["panel"] ?? "").Trim().ToLowerInvariant();
            Wnd_GameMain main = Wnd_GameMain.Instance;
            if (main == null) throw new RpcException("UI_NOT_READY", "The main game window is unavailable");
            switch (panel) {
                case "build": main.OpenBuild(); break;
                case "produce": main.ProducePanelOpen(); break;
                case "search": main.SearchPanelOpen(); break;
                case "main": main.ShowMainMenu(); break;
                case "close": main.Esc(); break;
                case "cancel":
                    main.CancelSelect();
                    if (UILogicMgr.Instance != null && UILogicMgr.Instance.Mode != UILogicMgr.Instance.DefaultMode)
                        UILogicMgr.Instance.ChangeMode(UILogicMgr.Instance.DefaultMode, null, false, new object[0]);
                    break;
                default: throw new RpcException("INVALID_ARGUMENT", "panel must be build, produce, search, main, close, or cancel");
            }
            return new { commandAccepted = true, completion = "dispatched", panel = panel, mode = UILogicMgr.Instance == null ? null : UILogicMgr.Instance.Mode.ToString() };
        }

        private static object SaveGame(JObject args)
        {
            string name = ((string)args["name"] ?? "").Trim();
            if (name.Length == 0 || name.Length > 80 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new RpcException("INVALID_ARGUMENT", "name must be a valid save name of 1-80 characters");
            MainManager manager = MainManager.Instance;
            if (manager == null || World.Instance == null) throw new RpcException("GAME_NOT_READY", "The game manager or world is unavailable");
            manager.DoSave(name, null);
            return new { commandAccepted = true, completion = "started", name = name, path = "MainManager.DoSave" };
        }

        private static object ThingsState(JObject args)
        {
            Map map = RequireMap();
            string requestedType = ((string)args["type"] ?? "").Trim();
            string query = ((string)args["query"] ?? "").Trim();
            bool playerOnly = args.Value<bool?>("playerOnly") ?? false;
            int offset = Math.Max(0, args.Value<int?>("offset") ?? 0);
            int limit = Math.Max(1, Math.Min(1000, args.Value<int?>("limit") ?? 100));
            List<Thing> matches = new List<Thing>();
            foreach (KeyValuePair<g_emThingType, List<Thing>> pair in map.Things.m_TypeThings) {
                if (requestedType.Length > 0 && !String.Equals(pair.Key.ToString(), requestedType, StringComparison.OrdinalIgnoreCase)) continue;
                if (pair.Value == null) continue;
                foreach (Thing thing in pair.Value) {
                    if (thing == null || (playerOnly && !thing.IsPlayerThing)) continue;
                    string name = SafeString(delegate { return thing.GetName(); }) ?? "";
                    string defName = SafeString(delegate { return thing.def == null ? null : thing.def.Name; }) ?? "";
                    if (query.Length > 0 && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 && defName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    matches.Add(thing);
                }
            }
            if (requestedType.Length == 0 || String.Equals(requestedType, g_emThingType.Npc.ToString(), StringComparison.OrdinalIgnoreCase)) {
                if (map.Things.m_lisNpcs != null) foreach (Npc npc in map.Things.m_lisNpcs) {
                    if (npc == null || (playerOnly && !npc.IsPlayerThing)) continue;
                    string name = SafeString(delegate { return npc.GetName(); }) ?? "";
                    string defName = SafeString(delegate { return npc.def == null ? null : npc.def.Name; }) ?? "";
                    if (query.Length > 0 && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 && defName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    matches.Add(npc);
                }
            }
            List<object> items = new List<object>();
            for (int i = offset; i < matches.Count && items.Count < limit; i++) items.Add(ThingSummary(matches[i]));
            int next = offset + items.Count;
            return new { items = items, total = matches.Count, offset = offset, limit = limit, nextOffset = next < matches.Count ? (int?)next : null };
        }

        private static object PopulationState(JObject args)
        {
            Map map = RequireMap();
            int offset = Math.Max(0, args.Value<int?>("offset") ?? 0);
            int limit = Math.Max(1, Math.Min(500, args.Value<int?>("limit") ?? 100));
            string query = ((string)args["query"] ?? "").Trim();
            List<object> items = new List<object>();
            int total = 0, visitors = 0, animals = 0, player = 0;
            if (map.Things.m_lisNpcs != null) foreach (Npc npc in map.Things.m_lisNpcs) {
                if (npc == null || npc.IsDeath) continue;
                if (npc.IsVistor) visitors++;
                if (!npc.IsSmartRace) animals++;
                if (npc.IsPlayerThing) player++;
                string name = SafeString(delegate { return npc.GetName(); }) ?? "";
                if (query.Length > 0 && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (total >= offset && items.Count < limit) items.Add(new { npc = NpcSummary(npc), visitor = npc.IsVistor, animal = !npc.IsSmartRace, canHunt = npc.CanBeHunting, canSlaughter = npc.CanBeSlaughter, canExpel = npc.CanExpel });
                total++;
            }
            int next = offset + items.Count;
            return new { items = items, total = total, offset = offset, limit = limit, nextOffset = next < total ? (int?)next : null, visitors = visitors, animals = animals, player = player };
        }

        private static object ResourceState(JObject args)
        {
            WorldWarehouse warehouse = RequireWorld().Warehouse;
            if (warehouse == null) throw new RpcException("GAME_NOT_READY", "WorldWarehouse is unavailable");
            string query = ((string)args["query"] ?? "").Trim();
            int offset = Math.Max(0, args.Value<int?>("offset") ?? 0);
            int limit = Math.Max(1, Math.Min(1000, args.Value<int?>("limit") ?? 200));
            List<string> names = new List<string>();
            if (warehouse.m_mapItemsAtGround != null) {
                foreach (string name in warehouse.m_mapItemsAtGround.Keys)
                    if (query.Length == 0 || name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) names.Add(name);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            List<object> items = new List<object>();
            for (int i = offset; i < names.Count && items.Count < limit; i++) {
                string name = names[i];
                items.Add(new { defName = name, count = warehouse.GetItemCount(name) });
            }
            int next = offset + items.Count;
            return new {
                food = warehouse.CanUseFoodCount,
                medicine = warehouse.CanUseDrugCount,
                cultivationFood = warehouse.CanUsePracticeFoodCount,
                items = items,
                total = names.Count,
                offset = offset,
                limit = limit,
                nextOffset = next < names.Count ? (int?)next : null
            };
        }

        private static object SchoolState()
        {
            SchoolMgr school = SchoolMgr.Instance;
            if (school == null) throw new RpcException("GAME_NOT_READY", "SchoolMgr is unavailable");
            List<object> schedule = new List<object>();
            if (school.TimePlanning != null) for (int i = 0; i < school.TimePlanning.Length; i++) schedule.Add(new { hour = i, plan = school.TimePlanning[i].ToString() });
            return new {
                created = school.Created,
                name = SafeString(delegate { return school.Name; }),
                level = school.Level.ToString(),
                prestigeRank = school.PrestigeRank.ToString(),
                score = school.Score,
                baseScore = school.BaseScore,
                goodScore = school.GoodScore,
                badScore = school.BadScore,
                fengShui = school.FengShui.ToString(),
                fengShuiValue = school.FengShuiValue,
                discipleCount = school.DiscipleCount,
                discipleLimit = school.GetDiscipleMax(),
                masterNpcId = school.MasterID,
                unlockedBuildings = school.UnLockBds,
                unlockedManuals = school.GongList,
                schedule = schedule
            };
        }

        private static object SelectionState()
        {
            Map map = RequireMap();
            List<object> selected = new List<object>();
            foreach (KeyValuePair<g_emThingType, List<Thing>> pair in map.Things.m_TypeThings) {
                if (pair.Value == null) continue;
                foreach (Thing thing in pair.Value) if (thing != null && thing.IsSelected) selected.Add(ThingSummary(thing));
            }
            if (map.Things.m_lisNpcs != null) foreach (Npc npc in map.Things.m_lisNpcs)
                if (npc != null && npc.IsSelected) selected.Add(ThingSummary(npc));
            return new { items = selected, count = selected.Count };
        }

        private static object EnumState()
        {
            return new {
                thingTypes = Enum.GetNames(typeof(g_emThingType)),
                workKinds = Enum.GetNames(typeof(g_emBehaviourWorkKind)),
                timePlans = Enum.GetNames(typeof(g_emTimePlanning)),
                npcRanks = Enum.GetNames(typeof(g_emNpcRank)),
                elements = Enum.GetNames(typeof(g_emElementKind)),
                practiceBehaviours = Enum.GetNames(typeof(g_emPracticeBehaviourKind)),
                npcSkills = Enum.GetNames(typeof(g_emNpcSkillType)),
                equipTypes = Enum.GetNames(typeof(g_emEquipType)),
                itemLabels = Enum.GetNames(typeof(g_emItemLable)),
                artifactProperties = Enum.GetNames(typeof(XiaWorld.Fight.g_emFaBaoP)),
                needTypes = Enum.GetNames(typeof(g_emNeedType))
            };
        }

        private static object AlertState(JObject args)
        {
            int limit = Math.Max(1, Math.Min(500, args.Value<int?>("limit") ?? 100));
            List<object> items = new List<object>();
            MessageMgr manager = MessageMgr.Instance;
            if (manager != null && manager.m_mapLevelMsg != null) {
                for (int i = manager.m_mapLevelMsg.Count - 1; i >= 0 && items.Count < limit; i--) {
                    MessageMgr.MessageData data = manager.m_mapLevelMsg[i];
                    if (data == null || data._IsRemove) continue;
                    items.Add(new {
                        source = "MessageMgr",
                        id = data._MessageID,
                        importance = data._Level.ToString(),
                        description = SafeString(delegate { return data.GetDesc("", true); }),
                        tip = SafeString(delegate { return data.GetTipDes(); }),
                        targetKey = data._TKey,
                        thingIds = data._ThingID,
                        isNew = data._IsNew,
                        time = data._Time
                    });
                }
            }
            return new { items = items, count = items.Count, limit = limit, truncated = items.Count >= limit };
        }

        private static object CommandTypesState(JObject args)
        {
            CommandMgr manager = CommandMgr.Instance;
            if (manager == null) throw new RpcException("GAME_NOT_READY", "CommandMgr is unavailable");
            Dictionary<string, CommandTypeDef> definitions = PrivateStaticField<Dictionary<string, CommandTypeDef>>(typeof(CommandMgr), "m_mapCMDTypes");
            string query = ((string)args["query"] ?? "").Trim();
            List<object> items = new List<object>();
            Dictionary<string, CommandTypeDef> discovered = new Dictionary<string, CommandTypeDef>(StringComparer.OrdinalIgnoreCase);
            if (definitions != null) foreach (KeyValuePair<string, CommandTypeDef> pair in definitions) discovered[pair.Key] = pair.Value;
            Dictionary<string, List<Command>> active = PrivateField<Dictionary<string, List<Command>>>(manager, "m_mapCommands");
            if (active != null) foreach (KeyValuePair<string, List<Command>> pair in active) {
                if (discovered.ContainsKey(pair.Key) || pair.Value == null) continue;
                foreach (Command command in pair.Value) {
                    if (command == null || command.def == null) continue;
                    discovered[pair.Key] = command.def;
                    break;
                }
            }
            foreach (KeyValuePair<string, CommandTypeDef> pair in discovered) {
                CommandTypeDef def = pair.Value;
                if (query.Length > 0 && pair.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (def == null || ((def.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 && (def.Class ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0))) continue;
                items.Add(new {
                    key = pair.Key,
                    className = def == null ? null : def.Class,
                    name = def == null ? null : def.Name,
                    workKind = def == null ? null : def.Kind.ToString(),
                    single = def == null ? (int?)null : def.Single,
                    lowOrder = def == null ? (int?)null : def.LowOrder
                });
            }
            return new { items = items, count = items.Count };
        }

        private static object CommandSummary(Command command)
        {
            if (command == null) return null;
            return new {
                id = command.ID,
                type = command.CommandType,
                name = command.def == null ? null : command.def.Name,
                ownerThingId = command.OwnerThing == null ? (int?)null : command.OwnerThing.ID,
                gridKey = command.GridKey,
                priority = command.Priority,
                valid = command.m_bValid,
                finished = command.m_bFinished,
                disabled = command.ActDisable,
                createTime = command.CreateTime
            };
        }

        private static object CommandsState(JObject args)
        {
            CommandMgr manager = CommandMgr.Instance;
            Dictionary<string, List<Command>> commands = PrivateField<Dictionary<string, List<Command>>>(manager, "m_mapCommands");
            if (manager == null || commands == null) throw new RpcException("GAME_NOT_READY", "CommandMgr is unavailable");
            string type = ((string)args["type"] ?? "").Trim();
            int limit = Math.Max(1, Math.Min(2000, args.Value<int?>("limit") ?? 500));
            List<object> items = new List<object>();
            foreach (KeyValuePair<string, List<Command>> pair in commands) {
                if (type.Length > 0 && !String.Equals(pair.Key, type, StringComparison.OrdinalIgnoreCase)) continue;
                if (pair.Value == null) continue;
                foreach (Command command in pair.Value) {
                    if (command != null) items.Add(CommandSummary(command));
                    if (items.Count >= limit) return new { items = items, count = items.Count, truncated = true };
                }
            }
            return new { items = items, count = items.Count, truncated = false };
        }

        private static object[] CommandParameters(JObject args)
        {
            JArray values = args["params"] as JArray;
            if (values == null) return new object[0];
            List<object> result = new List<object>();
            foreach (JToken value in values) {
                if (value.Type == JTokenType.Integer) result.Add(value.Value<int>());
                else if (value.Type == JTokenType.Float) result.Add(value.Value<float>());
                else if (value.Type == JTokenType.Boolean) result.Add(value.Value<bool>());
                else if (value.Type == JTokenType.String) result.Add(value.Value<string>());
                else if (value.Type == JTokenType.Null) result.Add(null);
                else throw new RpcException("INVALID_ARGUMENT", "command params support only numbers, booleans, strings, and null");
            }
            return result.ToArray();
        }

        private static string RequireCommandType(JObject args)
        {
            string type = ((string)args["command"] ?? "").Trim();
            if (type.Length == 0) throw new RpcException("INVALID_ARGUMENT", "command is required; use state.commandTypes to discover valid names");
            CommandMgr manager = CommandMgr.Instance;
            if (manager == null || manager.GetDef(type) == null) throw new RpcException("COMMAND_NOT_FOUND", "Unknown command type: " + type);
            return type;
        }

        private static object AddThingCommand(JObject args)
        {
            string type = RequireCommandType(args);
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            Thing thing = RequireThing(id.Value);
            CommandMgr manager = CommandMgr.Instance;
            Command before = FindActiveThingCommand(manager, type, thing.ID);
            Command command = before ?? manager.AddCommand(type, thing, CommandParameters(args));
            if (command == null) throw new RpcException("COMMAND_REJECTED", "The game rejected command " + type + " for thing " + id.Value);
            Command evidence = FindActiveThingCommand(manager, type, thing.ID);
            return new { inputProcessed = true, commandAccepted = true, completion = evidence == null ? "dispatched" : "verified", created = before == null, command = CommandSummary(command), evidence = CommandSummary(evidence) };
        }

        private static object AddGridCommand(JObject args)
        {
            string type = RequireCommandType(args);
            int key = ResolveGridKey(args, RequireMap());
            CommandMgr manager = CommandMgr.Instance;
            Command command = manager.AddSingleCMD(type, key, CommandParameters(args));
            if (command == null) throw new RpcException("COMMAND_REJECTED", "The game rejected command " + type + " at grid " + key);
            return new { inputProcessed = true, commandAccepted = true, completion = "verified", command = CommandSummary(command), evidence = new { gridKey = command.GridKey, valid = command.m_bValid } };
        }

        private static object RemoveThingCommand(JObject args)
        {
            string type = RequireCommandType(args);
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            Thing thing = RequireThing(id.Value);
            CommandMgr manager = CommandMgr.Instance;
            Command before = FindActiveThingCommand(manager, type, thing.ID);
            manager.RemoveCommandByThing(thing, type, false);
            Command after = FindActiveThingCommand(manager, type, thing.ID);
            return new { inputProcessed = true, commandAccepted = true, completion = after == null ? "verified" : "dispatched", removed = before != null && after == null, previous = CommandSummary(before), evidence = CommandSummary(after) };
        }

        private static Command FindActiveThingCommand(CommandMgr manager, string type, int thingId)
        {
            Dictionary<string, List<Command>> commands = PrivateField<Dictionary<string, List<Command>>>(manager, "m_mapCommands");
            if (commands == null) return null;
            foreach (KeyValuePair<string, List<Command>> pair in commands) {
                if (!String.Equals(pair.Key, type, StringComparison.OrdinalIgnoreCase) || pair.Value == null) continue;
                foreach (Command command in pair.Value)
                    if (command != null && command.OwnerThing != null && command.OwnerThing.ID == thingId && command.m_bValid && !command.m_bFinished) return command;
            }
            return null;
        }

        private static object MapInputState()
        {
            UILogicMgr manager = UILogicMgr.Instance;
            UILogicModeBase mode = manager == null ? null : manager.GetCurMode();
            GRoot root = GRoot.inst;
            return new {
                ready = manager != null && mode != null,
                mode = manager == null ? null : manager.Mode.ToString(),
                modeType = mode == null ? null : mode.GetType().FullName,
                popupOpen = root != null && root.hasAnyPopup,
                modalOpen = root != null && root.hasModalWindow
            };
        }

        private static UILogicMgr RequireMapInput(JObject args)
        {
            UILogicMgr manager = UILogicMgr.Instance;
            if (manager == null || manager.GetCurMode() == null) throw new RpcException("UI_NOT_READY", "UILogicMgr map input is unavailable");
            bool allowBlocked = args.Value<bool?>("allowBlocked") ?? false;
            GRoot root = GRoot.inst;
            if (!allowBlocked && root != null && (root.hasModalWindow || root.hasAnyPopup)) throw new RpcException("UI_BLOCKED", "A modal window or popup blocks map input");
            return manager;
        }

        private static object MapClick(JObject args)
        {
            Map map = RequireMap();
            int key = ResolveGridKey(args, map);
            int button = Math.Max(0, Math.Min(2, args.Value<int?>("button") ?? 0));
            UILogicMgr manager = RequireMapInput(args);
            Vector3 position = GridMgr.Inst.Grid2Pos(key);
            string beforeMode = manager.Mode.ToString();
            manager.OnMapBntDown(position, key, button);
            manager.OnMapClick(position, key, button);
            return new { path = "UILogicMgr.OnMapClick", inputProcessed = true, commandAccepted = true, completion = "dispatched", key = key, button = button, modeBefore = beforeMode, modeAfter = manager.Mode.ToString(), evidence = GridState(new JObject(new JProperty("key", key))) };
        }

        private static object MapDrag(JObject args)
        {
            Map map = RequireMap();
            JObject fromArgs = args["from"] as JObject;
            JObject toArgs = args["to"] as JObject;
            if (fromArgs == null || toArgs == null) throw new RpcException("INVALID_ARGUMENT", "from and to grid selectors are required");
            int fromKey = ResolveGridKey(fromArgs, map);
            int toKey = ResolveGridKey(toArgs, map);
            int button = Math.Max(0, Math.Min(2, args.Value<int?>("button") ?? 0));
            UILogicMgr manager = RequireMapInput(args);
            Vector3 fromPosition = GridMgr.Inst.Grid2Pos(fromKey);
            Vector3 toPosition = GridMgr.Inst.Grid2Pos(toKey);
            string beforeMode = manager.Mode.ToString();
            manager.OnMapDragBegin(fromPosition, fromKey, button);
            manager.OnMapDraging(toPosition, toKey, button);
            manager.OnMapDragEnd(toPosition, toKey, button);
            return new { path = "UILogicMgr.OnMapDragBegin/OnMapDraging/OnMapDragEnd", inputProcessed = true, commandAccepted = true, completion = "dispatched", fromKey = fromKey, toKey = toKey, button = button, modeBefore = beforeMode, modeAfter = manager.Mode.ToString() };
        }

        private static T ParseEnum<T>(JObject args, string key) where T : struct
        {
            string raw = ((string)args[key] ?? "").Trim();
            if (raw.Length > 0) {
                try {
                    object parsed = Enum.Parse(typeof(T), raw, true);
                    if (Enum.IsDefined(typeof(T), parsed)) return (T)parsed;
                } catch { }
            }
            throw new RpcException("INVALID_ARGUMENT", key + " is not a valid " + typeof(T).Name);
        }

        private static object SetWorkPriority(JObject args)
        {
            int? id = args.Value<int?>("id");
            int? priority = args.Value<int?>("priority");
            if (!id.HasValue || !priority.HasValue || priority.Value < 0 || priority.Value > 9)
                throw new RpcException("INVALID_ARGUMENT", "id and priority (0-9) are required");
            Npc npc = RequireNpc(id.Value);
            if (npc.JobEngine == null) throw new RpcException("NPC_NOT_READY", "NPC job engine is unavailable");
            g_emBehaviourWorkKind kind = ParseEnum<g_emBehaviourWorkKind>(args, "workKind");
            npc.JobEngine.BehaviouSetting[kind] = priority.Value;
            return new { commandAccepted = true, npcId = npc.ID, workKind = kind.ToString(), priority = priority.Value };
        }

        private static object SetWorkEnabled(JObject args)
        {
            int? id = args.Value<int?>("id");
            bool? enabled = args.Value<bool?>("enabled");
            if (!id.HasValue || !enabled.HasValue) throw new RpcException("INVALID_ARGUMENT", "id and enabled are required");
            Npc npc = RequireNpc(id.Value);
            if (npc.JobEngine == null) throw new RpcException("NPC_NOT_READY", "NPC job engine is unavailable");
            g_emBehaviourWorkKind kind = ParseEnum<g_emBehaviourWorkKind>(args, "workKind");
            npc.JobEngine.ChangeBehaviourEnable(kind, enabled.Value);
            return new { commandAccepted = true, npcId = npc.ID, workKind = kind.ToString(), enabled = enabled.Value };
        }

        private static object SetVipWork(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            Npc npc = RequireNpc(id.Value);
            if (npc.JobEngine == null) throw new RpcException("NPC_NOT_READY", "NPC job engine is unavailable");
            g_emBehaviourWorkKind kind = ParseEnum<g_emBehaviourWorkKind>(args, "workKind");
            bool accepted = npc.JobEngine.SetVipWork(kind);
            return new { commandAccepted = accepted, npcId = npc.ID, workKind = kind.ToString() };
        }

        private static object InterruptNpcJob(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            Npc npc = RequireNpc(id.Value);
            if (npc.JobEngine == null) throw new RpcException("NPC_NOT_READY", "NPC job engine is unavailable");
            npc.JobEngine.InterruptJob("AmazingCultivationMCP", true, false);
            return new { commandAccepted = true, npcId = npc.ID, completion = "dispatched" };
        }

        private static object SetSchedule(JObject args)
        {
            int? hour = args.Value<int?>("hour");
            if (!hour.HasValue || hour.Value < 0 || hour.Value > 23) throw new RpcException("INVALID_ARGUMENT", "hour must be between 0 and 23");
            g_emTimePlanning plan = ParseEnum<g_emTimePlanning>(args, "plan");
            SchoolMgr school = SchoolMgr.Instance;
            if (school == null) throw new RpcException("GAME_NOT_READY", "SchoolMgr is unavailable");
            school.SetPlan(hour.Value, plan);
            return new { commandAccepted = true, hour = hour.Value, plan = plan.ToString() };
        }

        private static object SetSchoolMaster(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue || !(args.Value<bool?>("confirm") ?? false))
                throw new RpcException("CONFIRMATION_REQUIRED", "id and confirm=true are required");
            SchoolMgr school = SchoolMgr.Instance;
            if (school == null || !school.Created) throw new RpcException("SCHOOL_NOT_READY", "Create the sect before assigning its master");
            Npc npc = RequireNpc(id.Value);
            if (npc.IsDeath) throw new RpcException("NPC_DEAD", "A dead NPC cannot become sect master");
            if (!npc.IsDisciple) throw new RpcException("INNER_DISCIPLE_REQUIRED", "Sect master must be an inner disciple");
            int before = school.MasterID;
            school.SetMaster(npc);
            if (school.MasterID != npc.ID) throw new RpcException("MASTER_ASSIGNMENT_FAILED", "The native SchoolMgr.SetMaster call did not assign the requested NPC");
            return new { commandAccepted = true, completion = "verified", before = before, masterNpcId = school.MasterID, masterName = npc.Name };
        }

        private static NpcPractice RequirePractice(int id)
        {
            Npc npc = RequireNpc(id);
            NpcPractice practice = npc.PropertyMgr == null ? null : npc.PropertyMgr.Practice;
            if (practice == null) throw new RpcException("PRACTICE_UNAVAILABLE", "NPC " + id + " has no cultivation practice data");
            return practice;
        }

        private static object SetPracticeMode(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            g_emPracticeBehaviourKind mode = ParseEnum<g_emPracticeBehaviourKind>(args, "mode");
            NpcPractice practice = RequirePractice(id.Value);
            string before = practice.PracticeMode.ToString();
            bool changed = practice.PracticeMode != mode;
            if (changed) practice.ChangeMode(mode);
            return new { commandAccepted = true, id = id.Value, before = before, mode = practice.PracticeMode.ToString(), changed = changed };
        }

        private static object SetPracticeBehaviour(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            g_emPracticeBehaviourKind behaviour = ParseEnum<g_emPracticeBehaviourKind>(args, "behaviour");
            NpcPractice practice = RequirePractice(id.Value);
            string before = practice.BehaviourKind.ToString();
            bool changed = practice.BehaviourKind != behaviour;
            if (changed) practice.SetBehavourKind(behaviour);
            return new { commandAccepted = true, id = id.Value, before = before, behaviour = practice.BehaviourKind.ToString(), changed = changed };
        }

        private static object SelectCommand(JObject args)
        {
            string name = ((string)args["name"] ?? "").Trim();
            if (name.Length == 0 || name.Length > 100) throw new RpcException("INVALID_ARGUMENT", "name is required");
            Wnd_GameMain main = Wnd_GameMain.Instance;
            if (main == null) throw new RpcException("UI_NOT_READY", "The main game window is unavailable");
            main.SelectCMD(name);
            return new { commandAccepted = true, completion = "selection-mode", name = name };
        }

        private static object NpcSummary(Npc npc)
        {
            JobBase job = npc.JobEngine == null ? null : npc.JobEngine.CurJob;
            List<object> needs = new List<object>();
            if (npc.Needs != null && npc.Needs.m_mapNeeds != null) {
                foreach (KeyValuePair<int, NeedBase> pair in npc.Needs.m_mapNeeds) {
                    NeedBase need = pair.Value;
                    if (need == null) continue;
                    needs.Add(new { type = need.NeedType.ToString(), value = need.Value, realValue = need.RealValue, level = need.Level, min = need.MinValue, max = need.MaxValue });
                }
            }
            List<object> workSettings = new List<object>();
            if (npc.JobEngine != null && npc.JobEngine.BehaviouSetting != null) {
                foreach (KeyValuePair<g_emBehaviourWorkKind, int> pair in npc.JobEngine.BehaviouSetting)
                    workSettings.Add(new { workKind = pair.Key.ToString(), priority = pair.Value, banned = npc.JobEngine.BehaviouBan != null && npc.JobEngine.BehaviouBan.Contains((int)pair.Key) });
            }
            return new {
                id = npc.ID,
                name = SafeString(delegate { return npc.Name; }),
                displayName = SafeString(delegate { return npc.GetName(); }),
                key = npc.Key,
                position = new { x = npc.Pos.x, y = npc.Pos.y, z = npc.Pos.z },
                hp = npc.Hp,
                maxHp = npc.MaxHp,
                age = SafeValue(delegate { return (object)npc.Age; }),
                sex = SafeString(delegate { return npc.Sex.ToString(); }),
                rank = SafeString(delegate { return npc.Rank.ToString(); }),
                healthState = SafeString(delegate { return npc.HealthState.ToString(); }),
                camp = SafeString(delegate { return npc.Camp.ToString(); }),
                dead = SafeValue(delegate { return (object)npc.IsDeath; }),
                disciple = SafeValue(delegate { return (object)npc.IsDisciple; }),
                outerDisciple = SafeValue(delegate { return (object)npc.IsWaiMen; }),
                gongKind = SafeString(delegate { return npc.GongKind.ToString(); }),
                bedId = npc.MyBed == null ? (int?)null : npc.MyBed.ID,
                practicePlaceId = npc.MyPractice == null ? (int?)null : npc.MyPractice.ID,
                guardAreaId = npc.GuardAreaId == 0 ? (int?)null : npc.GuardAreaId,
                animal = npc.IsSmartRace ? null : new { breedingAreaId = npc.BrAnimalData == null || npc.BrAnimalData.BAreaID == 0 ? (int?)null : npc.BrAnimalData.BAreaID, tameTime = npc.BrAnimalData == null ? 0d : npc.BrAnimalData.TameTime, pregnant = npc.IsBrPregnant, lingShou = npc.IsLingShou, thinkable = npc.ThinkableAnimal },
                zhenQi = SafeValue(delegate { return (object)npc.ZhenQi; }),
                maxZhenQi = SafeValue(delegate { return (object)npc.MaxZhenQi; }),
                currentJob = job == null ? null : new { id = job.ID, name = job.jobdef == null ? null : job.jobdef.Name, displayName = job.jobdef == null ? null : job.jobdef.DisplayName, workKind = job.BehaviourWorkKind.ToString(), state = job.State.ToString(), commandRequired = job.NeedCMD },
                needs = needs,
                workSettings = workSettings
            };
        }

        private static object NpcDetail(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            return NpcSummary(RequireNpc(id.Value));
        }

        private static object NpcProfile(Npc npc)
        {
            NpcPropertyMgr properties = npc.PropertyMgr;
            if (properties == null) return new { available = false };
            return new {
                available = true,
                race = SafeString(delegate { return properties.Race; }),
                charisma = SafeValue(delegate { return (object)properties.Charisma; }),
                intelligence = SafeValue(delegate { return (object)properties.Intelligence; }),
                luck = SafeValue(delegate { return (object)properties.Luck; }),
                perception = SafeValue(delegate { return (object)properties.Perception; }),
                physique = SafeValue(delegate { return (object)properties.Physique; }),
                mood = properties.MoodData == null ? null : new {
                    current = SafeValue(delegate { return (object)properties.MoodData.CurMoodValue; }),
                    real = SafeValue(delegate { return (object)properties.MoodData.RealMoodValue; }),
                    complexity = SafeValue(delegate { return (object)properties.MoodData.MoodComplexity; })
                },
                features = properties.FeatureList == null ? new string[0] : properties.FeatureList.ConvertAll<string>(delegate(NpcFeatureDef item) { return item == null ? null : item.Name; }).ToArray(),
                experiences = properties.ExperienceList == null ? new string[0] : properties.ExperienceList.ConvertAll<string>(delegate(NpcExperienceDef item) { return item == null ? null : item.Name; }).ToArray()
            };
        }

        private static object NpcProfileState(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            Npc npc = RequireNpc(id.Value);
            return new { id = npc.ID, name = SafeString(delegate { return npc.GetName(); }), profile = NpcProfile(npc) };
        }

        private static object StageSummary(GongStage stage)
        {
            if (stage == null) return null;
            return new { name = stage.Name, displayName = stage.DisplayName, level = stage.Level, requiredValue = stage.Value };
        }

        private static object NeckSummary(GongStageNeck neck)
        {
            if (neck == null) return null;
            return new {
                name = neck.Name,
                displayName = neck.DisplayName,
                kind = neck.Kind.ToString(),
                location = neck.Location,
                requiredValue = neck.Value,
                countdown = neck.NeckCountdown,
                costTime = neck.CostTime,
                thunderValue = neck.ThunderValue
            };
        }

        private static object NpcCultivation(Npc npc)
        {
            NpcPractice practice = npc.PropertyMgr == null ? null : npc.PropertyMgr.Practice;
            if (practice == null) return new { available = false, gongKind = SafeString(delegate { return npc.GongKind.ToString(); }) };
            GongDef gong = SafeValue(delegate { return (object)practice.Gong; }) as GongDef;
            return new {
                available = true,
                gong = gong == null ? null : new { name = gong.Name, displayName = gong.DisplayName, kind = gong.GongKind.ToString(), element = gong.ElementKind.ToString(), level = gong.Level, yinYang = gong.YinYang },
                stageIndex = PrivateValue(practice, "StageIndex"),
                logicStage = SafeValue(delegate { return (object)practice.LogicStage; }),
                stage = StageSummary(SafeValue(delegate { return (object)practice.CurStage; }) as GongStage),
                nextStage = StageSummary(SafeValue(delegate { return (object)practice.NextStage; }) as GongStage),
                stageValue = practice.StageValue,
                stagePercent = practice.StageValueP,
                totalStageValue = SafeValue(delegate { return (object)practice.TotalStageValue; }),
                neckIndex = practice.NeckIndex,
                touchingNeck = practice.TouchNeck,
                neckTryCount = practice.NeckTryCount,
                currentNeck = NeckSummary(SafeValue(delegate { return (object)practice.CurNeck; }) as GongStageNeck),
                nextNeck = NeckSummary(SafeValue(delegate { return (object)practice.NextNeck; }) as GongStageNeck),
                neckCountdown = SafeValue(delegate { return (object)practice.GetNeckCountdown(); }),
                zhuJiValue = practice.ZhuJiValue,
                isZhuJi = SafeValue(delegate { return (object)practice.IsZhuJi; }),
                potential = SafeValue(delegate { return (object)practice.Potential; }),
                potentialBase = SafeValue(delegate { return (object)practice.PotentialBase; }),
                behaviour = practice.BehaviourKind.ToString(),
                practiceMode = practice.PracticeMode.ToString(),
                understanding = practice.UnderstandV,
                experience = practice.ExperiencV,
                goldCore = new { level = practice.GoldLevel, yinLevel = practice.GoldLevelYin, ling = practice.GoldLing, yinLing = practice.GoldLingYin },
                dao = new { total = practice.DaoHang, yang = practice.YangDaoHang, yin = practice.YinDaoHang, other = practice.OtherDaoHang, xuan = practice.XuanDaoHang },
                skillTree = new { active = practice.TreeActived, experience = practice.TreeExp, totalAddedExperience = practice.TreeExp_TotalAdd, learned = CopyStrings(practice.LearnedTreeList) },
                esoterica = CopyStrings(practice.LearnedEsotericaList),
                magics = CopyStrings(practice.Magics),
                spells = CopyStrings(practice.Spells),
                alchemyFormulas = CopyStrings(practice.DanFormulas),
                artifactFormulas = CopyStrings(practice.GetFabaoFormulas),
                masterNpcId = practice.MasterID,
                masterRank = practice.MasterRank,
                apprenticeNpcIds = practice.ApprenticeList == null ? new int[0] : practice.ApprenticeList.ToArray()
            };
        }

        private static object NpcCultivationState(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            Npc npc = RequireNpc(id.Value);
            return new { id = npc.ID, name = SafeString(delegate { return npc.GetName(); }), cultivation = NpcCultivation(npc) };
        }

        private static object BreakthroughState(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            Npc npc = RequireNpc(id.Value);
            NpcPractice practice = RequirePractice(id.Value);
            int failures = Math.Max(0, args.Value<int?>("failureCount") ?? 0);
            g_emElementKind? element = practice.Gong == null ? (g_emElementKind?)null : practice.Gong.ElementKind;
            int key = npc.Key;
            return new {
                id = npc.ID, key = key, touchingNeck = practice.TouchNeck, neck = NeckSummary(practice.CurNeck), conditionMet = SafeValue(delegate { return (object)practice.CheckNeckCondition(); }),
                successRate = SafeValue(delegate { return (object)practice.GetBrokenRate(failures, key); }), failureCount = failures,
                factors = new {
                    season = SafeValue(delegate { return (object)practice.GetSeasonEffectBroken(element); }),
                    light = SafeValue(delegate { return (object)practice.GetLightEffectBroken(element); }),
                    weather = SafeValue(delegate { return (object)practice.GetWeatherEffectBroken(element); }),
                    priorFailures = SafeValue(delegate { return (object)practice.FailedEffectBroken(failures); }),
                    mind = SafeValue(delegate { return (object)practice.MindStateEffectBroken(); }),
                    medicine = SafeValue(delegate { return (object)practice.DanEffectBroken(); }),
                    fengShui = SafeValue(delegate { return (object)practice.FengshuiEffectBroken(); }),
                    qi = SafeValue(delegate { return (object)practice.LingEffectBroken(key); }),
                    fiveElements = SafeValue(delegate { return (object)practice.ElementEffectBroken(key, element); }),
                    luck = SafeValue(delegate { return (object)practice.LuckEffectBroken(); })
                },
                cultivation = NpcCultivation(npc)
            };
        }

        private static object BodyCultivationState(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            Npc npc = RequireNpc(id.Value);
            NpcPractice practice = RequirePractice(id.Value);
            BodyPractice body = practice.BodyPracticeData;
            if (body == null) return new { id = npc.ID, available = false };
            List<object> active = new List<object>();
            if (body.DoQuenchingMap != null) foreach (KeyValuePair<string, BodyPractice.DoQuenchingData> pair in body.DoQuenchingMap) {
                BodyPractice.DoQuenchingData data = pair.Value;
                if (data != null) active.Add(new { key = pair.Key, part = data.part, item = data.item, method = data.method, progress = data.keep, required = data.need, loop = data.loop, source = data.from });
            }
            List<object> superParts = new List<object>();
            if (body.SuperParts != null) foreach (KeyValuePair<string, BodyPractice.SuperPart> pair in body.SuperParts) {
                BodyPractice.SuperPart part = pair.Value;
                if (part != null) superParts.Add(new { key = pair.Key, name = part.Name, displayName = part.DisName, corePart = part.Core, level = part.Level, integrity = part.Integrity, running = part.IsRuning, equipmentKind = part.EKind.ToString(), equipmentIndex = part.EIndex });
            }
            List<object> parts = new List<object>();
            NpcBodyData bodyData = npc.PropertyMgr == null ? null : npc.PropertyMgr.BodyData;
            Dictionary<string, NpcBodyData.NpcBodyPart> partMap = PrivateField<Dictionary<string, NpcBodyData.NpcBodyPart>>(bodyData, "m_mapName2Part");
            if (partMap != null) foreach (KeyValuePair<string, NpcBodyData.NpcBodyPart> pair in partMap) if (pair.Value != null) parts.Add(new { name = pair.Key, integrity = pair.Value.Integrity, weight = pair.Value.GetWeight(), removed = bodyData.RemovedParts != null && bodyData.RemovedParts.Contains(pair.Value), quenchingCount = body.GetPartQuenchingCount(pair.Key) });
            return new {
                id = npc.ID, available = true, qi = body.QiValue, maxQi = body.MaxZhenQi, recovery = body.RecoverZhenQi,
                attitude = body.CurAttitude, attitudeDefinition = body.CurAttitudeDef == null ? null : new { name = body.CurAttitudeDef.Name, displayName = body.CurAttitudeDef.DisplayName, description = body.CurAttitudeDef.Desc },
                waitFull = body.WaitFull, doingPart = body.DoingPart, methods = CopyStrings(body.QuenchingMethod), attitudes = CopyStrings(body.Attitudes), materials = body.QuenchingMaterial,
                activeQuenching = active, superParts = superParts, parts = parts
            };
        }

        private static object EsotericaCatalogState(JObject args)
        {
            EsotericaMgr manager = EsotericaMgr.Instance;
            if (manager == null) throw new RpcException("GAME_NOT_READY", "EsotericaMgr is unavailable");
            int? npcId = args.Value<int?>("npcId");
            Npc npc = npcId.HasValue ? RequireNpc(npcId.Value) : null;
            string query = ((string)args["query"] ?? "").Trim();
            Dictionary<string, EsotericaData> generated = PrivateField<Dictionary<string, EsotericaData>>(manager, "m_mapEsoterica");
            Dictionary<string, EsotericaDef> system = PrivateField<Dictionary<string, EsotericaDef>>(manager, "m_mapSysEsoterica");
            List<object> items = new List<object>();
            if (generated != null) foreach (KeyValuePair<string, EsotericaData> pair in generated) {
                EsotericaData data = pair.Value;
                if (data == null || (query.Length > 0 && ((data.DisplayName ?? "") + " " + (data.Name ?? "") + " " + pair.Key).IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                items.Add(new { id = pair.Key, name = data.Name, displayName = data.DisplayName, description = data.Desc, type = data.Type.ToString(), element = data.Element.ToString(), stage = data.GLevel.ToString(), level = data.LvL, difficulty = data.Difficulty, learned = npc == null ? (bool?)null : RequirePractice(npc.ID).IsLearnedEsoterica(pair.Key), canLearn = npc == null ? (bool?)null : manager.CanLearnEsoterica(pair.Key, npc) });
            }
            if (system != null) foreach (KeyValuePair<string, EsotericaDef> pair in system) {
                EsotericaDef def = pair.Value;
                if (def == null || (query.Length > 0 && ((def.DisplayName ?? "") + " " + (def.Name ?? "") + " " + pair.Key).IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                items.Add(new { id = pair.Key, name = def.Name, displayName = def.DisplayName, description = def.Desc, effect = def.EffectDesc, type = def.Type.ToString(), element = def.Element.ToString(), stage = def.GLevel.ToString(), level = def.Level, difficulty = def.Difficulty, learned = npc == null ? (bool?)null : RequirePractice(npc.ID).IsLearnedEsoterica(pair.Key), canLearn = npc == null ? (bool?)null : manager.CanLearnEsoterica(pair.Key, npc) });
            }
            return new { items = items, count = items.Count, query = query, npcId = npcId };
        }

        private static object GodCultivationState(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            Npc npc = RequireNpc(id.Value);
            NpcPractice practice = RequirePractice(id.Value);
            GodPractice god = practice.GodPracticeData;
            if (god == null) return new { id = npc.ID, available = false, daoTribulation = new { completed = practice.DaoHangJieCount, due = practice.GetDaoHangNeckCount(), element = practice.DaoHangJieElement.ToString() }, ageTribulation = new { completed = practice.AgeJieCount, due = practice.GetAgeNeckCount(), element = practice.AgeJieElement.ToString() } };
            List<object> buildings = new List<object>();
            Dictionary<int, GCityBuilding> godBuildings = PrivateField<Dictionary<int, GCityBuilding>>(god, "Buildings");
            if (godBuildings != null) foreach (KeyValuePair<int, GCityBuilding> pair in godBuildings) {
                GCityBuilding building = pair.Value;
                if (building != null) buildings.Add(new { id = building.id, name = building.name, level = building.level, closed = building.close != 0, rootCellId = building.root, stringParameter = building.sParam, numericParameter = building.nParam });
            }
            List<object> cells = new List<object>();
            if (god.CityCells != null) foreach (KeyValuePair<int, GCityCell> pair in god.CityCells) {
                GCityCell cell = pair.Value;
                if (cell != null) cells.Add(new { id = cell.ID, buildingId = cell.BuildingID, root = cell.Root, ring = GodPractice.GetRing(cell.ID) });
            }
            List<object> guards = new List<object>();
            if (god.GodGuards != null) foreach (KeyValuePair<int, GCityGuardData> pair in god.GodGuards) {
                GCityGuardData guard = pair.Value;
                if (guard != null) guards.Add(new { id = guard.id, name = guard.name, className = guard.cname, used = guard.used != 0 });
            }
            List<object> believers = new List<object>();
            if (god.lisCoreBelievers != null) foreach (GCityCoreBeliever believer in god.lisCoreBelievers) if (believer != null) believers.Add(new { name = believer.name, description = believer.desc, belief = believer.belive, age = believer.age, dailyGain = believer.dayadd, role = believer.rolep, guard = believer.guard, wishCount = believer.wcount, lastWishDay = believer.lwday });
            return new {
                id = npc.ID, available = true, faith = god.Faith, population = god.Population, populationCost = god.CostPopulation, maxPopulation = god.MaxPopulation, safePopulation = god.SafePopulation,
                mindState = god.MindStateLevel.ToString(), mindLevelUp = god.MindLevelUpValue, obsession = god.Obsession,
                faithTribulationCooldown = god.FaithJieColdDown, faithTribulationCount = god.FaithJieCount, ascensionCooldown = god.FeishengJieColdDown, ascensionNeeded = god.NeedFeisheng,
                godCityScore = god.GodCityScore, godCityArea = god.GodCityArea, statueCount = god.StatueCount, unlockedBuildings = god.UnLockBuildings == null ? new string[0] : god.UnLockBuildings.ToArray(),
                buildings = buildings, cells = cells, guards = guards, coreBelievers = believers,
                daoTribulation = new { completed = practice.DaoHangJieCount, due = practice.GetDaoHangNeckCount(), element = practice.DaoHangJieElement.ToString() },
                ageTribulation = new { completed = practice.AgeJieCount, due = practice.GetAgeNeckCount(), element = practice.AgeJieElement.ToString() }
            };
        }

        private static object SpellCatalogState(JObject args)
        {
            PracticeMgr manager = PracticeMgr.Instance;
            if (manager == null) throw new RpcException("GAME_NOT_READY", "PracticeMgr is unavailable");
            int? npcId = args.Value<int?>("npcId");
            NpcPractice practice = npcId.HasValue ? RequirePractice(npcId.Value) : null;
            string query = ((string)args["query"] ?? "").Trim();
            List<object> spells = new List<object>();
            if (manager.m_mapSpellDefs != null) foreach (KeyValuePair<string, SpellDef> pair in manager.m_mapSpellDefs) {
                SpellDef def = pair.Value;
                if (def == null || (query.Length > 0 && (pair.Key + " " + (def.Name ?? "") + " " + (def.DisplayName ?? "")).IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                spells.Add(new { key = pair.Key, name = def.Name, displayName = def.DisplayName, description = def.Desc, parent = def.Parent, element = def.BaseFive, unlocked = def.UnLock != 0, learned = practice == null ? (bool?)null : practice.Spells.Contains(def.Name) });
            }
            Dictionary<string, MagicDef> magicDefs = PrivateField<Dictionary<string, MagicDef>>(manager, "m_mapMagicDefs");
            List<object> magics = new List<object>();
            if (magicDefs != null) foreach (KeyValuePair<string, MagicDef> pair in magicDefs) {
                MagicDef def = pair.Value;
                if (def == null || (query.Length > 0 && (pair.Key + " " + (def.Name ?? "") + " " + (def.DisplayName ?? "")).IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                magics.Add(new {
                    key = pair.Key, name = def.Name, displayName = def.DisplayName, description = def.Desc, type = def.Type.ToString(), kind = def.Kind.ToString(), command = def.CMD,
                    costLing = practice == null ? def.CostLing : def.RealCostLing(RequireNpc(npcId.Value)), costMaxLing = practice == null ? def.CostMaxLing : def.RealCostMaxLing(RequireNpc(npcId.Value)), costAge = practice == null ? def.CostAge : def.RealCostAge(RequireNpc(npcId.Value)), costFaith = practice == null ? def.CostFaith : def.RealCostFaith(RequireNpc(npcId.Value)),
                    learned = practice == null ? (bool?)null : practice.Magics.Contains(def.Name)
                });
            }
            return new { npcId = npcId, query = query, spells = spells, spellCount = spells.Count, magics = magics, magicCount = magics.Count };
        }

        private static object SkillTreeState(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            NpcPractice practice = RequirePractice(id.Value);
            PracticeMgr manager = PracticeMgr.Instance;
            if (manager == null || manager.SkillTree == null) throw new RpcException("GAME_NOT_READY", "Native skill tree is unavailable");
            GongDef gong = practice.Gong;
            List<string> names = gong == null ? null : manager.SkillTree.GetGongTree(gong.Name);
            List<object> nodes = new List<object>();
            if (names != null) foreach (string name in names) {
                NpcSkillTreeDef def = manager.SkillTree.GetDef(name);
                if (def == null) continue;
                int canLearn = practice.CheckTreeUnitCanLearn(def.Name, false);
                nodes.Add(new {
                    name = def.Name, displayName = def.DisplayName, gong = def.Gong, esoterica = def.Esoterica,
                    level = def.Level.ToString(), line = def.Line, depth = def.Depth, index = def.Index,
                    daoCost = def.DaoHang, experienceCost = practice.GetTreeExpNeed(def), lingMax = def.LingMax,
                    goldLevel = def.GoldLevel, godCount = def.GodCount, fiveBase = def.FiveBase,
                    prerequisites = def.PreUnit == null ? new string[0] : def.PreUnit.ToArray(),
                    mutex = def.MutexUnit == null ? new string[0] : def.MutexUnit.ToArray(),
                    layers = def.Layers == null ? new string[0] : def.Layers.ToArray(),
                    learned = practice.IsLearnedTreeUnit(def.Name), canLearnCode = canLearn, canLearn = canLearn > 0
                });
            }
            return new { id = id.Value, gong = gong == null ? null : gong.Name, active = practice.TreeActived, experience = practice.TreeExp, learned = CopyStrings(practice.LearnedTreeList), nodes = nodes, count = nodes.Count };
        }

        private static object LearnSkillTreeNode(JObject args)
        {
            int? id = args.Value<int?>("id");
            string name = ((string)args["name"] ?? "").Trim();
            if (!id.HasValue || name.Length == 0 || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "id, name, and confirm=true are required");
            NpcPractice practice = RequirePractice(id.Value);
            PracticeMgr manager = PracticeMgr.Instance;
            NpcSkillTreeDef def = manager == null || manager.SkillTree == null ? null : manager.SkillTree.GetDef(name);
            if (def == null) throw new RpcException("SKILL_TREE_NODE_NOT_FOUND", "Skill-tree node was not found");
            GongDef gong = practice.Gong;
            List<string> currentTree = gong == null ? null : manager.SkillTree.GetGongTree(gong.Name);
            if (currentTree == null || !currentTree.Contains(def.Name)) throw new RpcException("SKILL_TREE_NODE_WRONG_GONG", "Skill-tree node does not belong to the NPC current cultivation art");
            int canLearn = practice.CheckTreeUnitCanLearn(def.Name, false);
            if (canLearn <= 0) throw new RpcException("SKILL_TREE_NODE_BLOCKED", "Native skill-tree validation rejected the node (code " + canLearn + ")");
            float before = practice.TreeExp;
            practice.LearnTreeNode(def, 1f, true, false);
            if (!practice.IsLearnedTreeUnit(def.Name)) throw new RpcException("SKILL_TREE_LEARN_FAILED", "Native learning call completed but the node is not learned");
            return new { commandAccepted = true, id = id.Value, name = def.Name, displayName = def.DisplayName, experienceBefore = before, experienceAfter = practice.TreeExp, learned = true, spells = CopyStrings(practice.Spells), magics = CopyStrings(practice.Magics) };
        }

        private static GodPractice RequireGodPractice(int id)
        {
            GodPractice god = RequirePractice(id).GodPracticeData;
            if (god == null) throw new RpcException("GOD_CULTIVATION_UNAVAILABLE", "NPC has no god-cultivation data");
            return god;
        }

        private static GCityBuilding RequireGodBuilding(GodPractice god, int buildingId)
        {
            GCityBuilding building = god.GetBuilding(buildingId);
            if (building == null) throw new RpcException("GOD_BUILDING_NOT_FOUND", "Divine-city building was not found");
            return building;
        }

        private static object GodBuildingCatalogState(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            GodPractice god = RequireGodPractice(id.Value);
            PracticeMgr manager = PracticeMgr.Instance;
            Dictionary<string, GodCityBuildingDef> definitions = PrivateField<Dictionary<string, GodCityBuildingDef>>(manager, "s_mapGodCityBuildingDefs");
            List<object> items = new List<object>();
            if (definitions != null) foreach (KeyValuePair<string, GodCityBuildingDef> pair in definitions) {
                GodCityBuildingDef def = pair.Value;
                if (def == null || def.Hide != 0) continue;
                List<object> levels = new List<object>();
                if (def.Levels != null) for (int level = 0; level < def.Levels.Count; level++) {
                    GodCityBuildingDef.LevelData data = def.Levels[level];
                    if (data != null) levels.Add(new { level = level, population = data.Population, faithCost = data.Cost == null ? 0f : data.Cost.Faith });
                }
                items.Add(new { key = pair.Key, name = def.Name, displayName = def.DisplayName, description = def.Desc, kind = def.Kind.ToString(), size = def.Size, maxCount = def.MaxCount, ringMin = def.RingMin, ringMax = def.RingMax, unlocked = god.IsBuildingUnLock(def.Name), levels = levels });
            }
            return new { id = id.Value, items = items, count = items.Count };
        }

        private static object CreateGodBuilding(JObject args)
        {
            int? id = args.Value<int?>("id"); int? cellId = args.Value<int?>("cellId"); string name = ((string)args["name"] ?? "").Trim();
            if (!id.HasValue || !cellId.HasValue || name.Length == 0 || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "id, cellId, name, and confirm=true are required");
            GodPractice god = RequireGodPractice(id.Value);
            if (!god.IsBuildingUnLock(name)) throw new RpcException("GOD_BUILDING_LOCKED", "Divine-city building is not unlocked");
            GCityBuilding building = god.AddBuilding(name, cellId.Value);
            if (building == null) throw new RpcException("GOD_BUILDING_REJECTED", "Native placement/cost/condition checks rejected the building");
            return new { commandAccepted = true, buildingId = building.id, state = GodCultivationState(new JObject(new JProperty("id", id.Value))) };
        }

        private static object UpgradeGodBuilding(JObject args)
        {
            int? id = args.Value<int?>("id"); int? buildingId = args.Value<int?>("buildingId");
            if (!id.HasValue || !buildingId.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "id, buildingId, and confirm=true are required");
            GodPractice god = RequireGodPractice(id.Value); GCityBuilding building = RequireGodBuilding(god, buildingId.Value); int before = building.level;
            god.BuildingLevelUp(building);
            if (building.level == before) throw new RpcException("GOD_BUILDING_UPGRADE_REJECTED", "Native level or resource checks rejected the upgrade");
            return new { commandAccepted = true, beforeLevel = before, afterLevel = building.level, state = GodCultivationState(new JObject(new JProperty("id", id.Value))) };
        }

        private static object RemoveGodBuilding(JObject args)
        {
            int? id = args.Value<int?>("id"); int? buildingId = args.Value<int?>("buildingId");
            if (!id.HasValue || !buildingId.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "id, buildingId, and confirm=true are required");
            GodPractice god = RequireGodPractice(id.Value); GCityBuilding building = RequireGodBuilding(god, buildingId.Value); int root = building.root;
            god.RemoveBuilding(root);
            return new { commandAccepted = true, removed = god.GetBuilding(buildingId.Value) == null, state = GodCultivationState(new JObject(new JProperty("id", id.Value))) };
        }

        private static object ToggleGodBuilding(JObject args)
        {
            int? id = args.Value<int?>("id"); int? buildingId = args.Value<int?>("buildingId");
            if (!id.HasValue || !buildingId.HasValue) throw new RpcException("INVALID_ARGUMENT", "id and buildingId are required");
            GodPractice god = RequireGodPractice(id.Value); GCityBuilding building = RequireGodBuilding(god, buildingId.Value);
            bool desiredClosed = args.Value<bool?>("closed") ?? building.close == 0;
            if ((building.close != 0) != desiredClosed) god.CloseOrOpenBuilding(building, 1);
            return new { commandAccepted = true, closed = building.close != 0, state = GodCultivationState(new JObject(new JProperty("id", id.Value))) };
        }

        private static object SetGodBuildingGuard(JObject args)
        {
            int? id = args.Value<int?>("id"); int? buildingId = args.Value<int?>("buildingId"); int guardId = args.Value<int?>("guardId") ?? 0;
            if (!id.HasValue || !buildingId.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "id, buildingId, and confirm=true are required");
            GodPractice god = RequireGodPractice(id.Value); GCityBuilding building = RequireGodBuilding(god, buildingId.Value);
            if (guardId > 0 && (god.GodGuards == null || !god.GodGuards.ContainsKey(guardId) || god.GodGuards[guardId].used != 0)) throw new RpcException("GOD_GUARD_UNAVAILABLE", "Guard does not exist or is already assigned");
            god.SetGodGuard(building, guardId, null);
            return new { commandAccepted = true, guardId = building.nParam, state = GodCultivationState(new JObject(new JProperty("id", id.Value))) };
        }

        private static object InteractionState(JObject args)
        {
            int? actorId = args.Value<int?>("actorId");
            int? targetId = args.Value<int?>("targetId");
            if (!actorId.HasValue || !targetId.HasValue || actorId.Value == targetId.Value) throw new RpcException("INVALID_ARGUMENT", "distinct actorId and targetId are required");
            Npc actor = RequireNpc(actorId.Value);
            Npc target = RequireNpc(targetId.Value);
            JianghuMgr manager = JianghuMgr.Instance;
            List<string> actions = manager == null ? new List<string>() : manager.GetTalkActionList(actor, target);
            JianghuMgr.JHNpcData known = manager == null ? null : manager.GetKnowNpcData(target.JiangHuSeed);
            return new {
                actor = NpcSummary(actor), target = NpcSummary(target), canTalk = actor.IsPlayerThing && target.IsSmartRace && !target.IsDeath,
                availableActions = actions == null ? new string[0] : actions.ToArray(), fate = manager == null ? (float?)null : manager.GetFateBetween(actor, target),
                targetCanExpel = target.CanExpel, targetIsVisitor = target.IsVistor,
                known = known == null ? null : new { favour = known.favour, vigilance = known.Vigilance, vigilant = known.IsVigilance, heartLocked = known.HeartIsLock, place = known.place, knownPlace = known.knowplace, knownDataCount = manager.GetKnowNpcDataCount(target.JiangHuSeed) }
            };
        }

        private static object NpcSkills(Npc npc)
        {
            NpcSkillData skillData = npc.PropertyMgr == null ? null : npc.PropertyMgr.SkillData;
            List<object> skills = new List<object>();
            if (skillData != null) foreach (g_emNpcSkillType type in Enum.GetValues(typeof(g_emNpcSkillType))) {
                NpcSkillData.NpcSkillUnit unit = null;
                try { unit = skillData.GetSkill(type); } catch { }
                if (unit == null) continue;
                skills.Add(new { type = type.ToString(), level = unit.Level, baseLevel = unit.BaseLevel, maxLevel = skillData.GetSkillMaxLevel(type), love = unit.Love, experience = unit.Exp, maxExperience = unit.MaxExp, banned = unit.Ban });
            }
            return new { available = skillData != null, items = skills, count = skills.Count };
        }

        private static object NpcSkillsState(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            Npc npc = RequireNpc(id.Value);
            return new { id = npc.ID, name = SafeString(delegate { return npc.GetName(); }), skills = NpcSkills(npc) };
        }

        private static object NpcHealth(Npc npc)
        {
            NpcBodyData body = npc.PropertyMgr == null ? null : npc.PropertyMgr.BodyData;
            List<object> injuries = new List<object>();
            if (body != null && body.m_DamageID != null) foreach (KeyValuePair<int, NpcBodyData.NpcDamage> pair in body.m_DamageID) {
                NpcBodyData.NpcDamage damage = pair.Value;
                if (damage == null || !damage.IsVaild) continue;
                injuries.Add(new {
                    id = damage.ID,
                    name = damage.def == null ? null : damage.def.Name,
                    displayName = damage.def == null ? null : damage.def.DisplayName,
                    label = damage.def == null ? null : damage.def.Lable.ToString(),
                    part = damage.PartName,
                    progress = damage.Progress,
                    value = SafeValue(delegate { return (object)damage.GetValue(); }),
                    treatment = damage.Treat,
                    treatmentDuration = damage.TreatDuration,
                    source = damage.From
                });
            }
            return new {
                available = body != null,
                state = SafeString(delegate { return npc.HealthState.ToString(); }),
                healthValue = body == null ? null : SafeValue(delegate { return (object)body.HealthValue; }),
                dying = body == null ? null : SafeValue(delegate { return (object)body.Dying; }),
                dead = body == null ? null : SafeValue(delegate { return (object)body.Dead; }),
                injuries = injuries,
                injuryCount = injuries.Count
            };
        }

        private static object NpcHealthState(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            Npc npc = RequireNpc(id.Value);
            return new { id = npc.ID, name = SafeString(delegate { return npc.GetName(); }), health = NpcHealth(npc) };
        }

        private static object ItemSummary(ItemThing item)
        {
            if (item == null) return null;
            return new {
                id = item.ID,
                name = SafeString(delegate { return item.GetName(); }),
                defName = SafeString(delegate { return item.def == null ? null : item.def.Name; }),
                count = item.Count,
                quality = SafeValue(delegate { return (object)item.GetQuality(); }),
                element = SafeString(delegate { return item.ElementKind.ToString(); }),
                hp = item.Hp,
                maxHp = item.MaxHp,
                ling = item.LingV,
                maxLing = item.MaxLing,
                equippedByNpcId = item.EquipByWho,
                inBagOfNpcId = item.InWhoseBag,
                isArtifact = item.IsFaBao,
                isArtifactEmbryo = item.IsFabaoEmbryo,
                isManual = item.IsEsoterica,
                manualId = item.EsotericaID,
                gongName = item.GongName
            };
        }

        private static object ItemState(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            ItemThing item = RequireThing(id.Value) as ItemThing;
            if (item == null) throw new RpcException("ITEM_NOT_FOUND", "Thing is not an item");
            List<object> properties = new List<object>();
            if (item.Fabao != null) foreach (XiaWorld.Fight.g_emFaBaoP property in Enum.GetValues(typeof(XiaWorld.Fight.g_emFaBaoP)))
                properties.Add(new { name = property.ToString(), value = SafeValue(delegate { return (object)item.Fabao.GetProperty(property); }) });
            return new {
                item = ItemSummary(item),
                fengShui = item.FengshuiItem == null ? null : new {
                    active = item.FengshuiActive, state = item.FSItemState,
                    description = SafeString(delegate { return item.GetFengshuiItemDesc(); }),
                    addedValue = item.FengshuiItem.AddV,
                    element = item.FengshuiItem.ElementKind.ToString(),
                    elementPower = item.FengshuiItem.ElementPower,
                    roomKind = item.FengshuiItem.RoomKind.ToString(),
                    roomRank = item.FengshuiItem.RoomFengshui.ToString()
                },
                artifact = item.Fabao == null ? null : new {
                    kind = item.Fabao.Kind.ToString(), sourceItem = item.Fabao.Oitem, sourceName = item.Fabao.OName,
                    godCount = item.Fabao.GodCount, fighting = item.Fabao.IsFighting,
                    properties = properties, abilityCount = item.Fabao.AbilityDatas == null ? 0 : item.Fabao.AbilityDatas.Count,
                    sourceDefinition = item.Fabao.OitemDef == null ? null : item.Fabao.OitemDef.Name
                },
                embryo = item.FabaoAcriveData == null ? null : new { canActivate = item.FabaoAcriveData.CanActive(), activeCount = item.FabaoAcriveData.Actives == null ? 0 : item.FabaoAcriveData.Actives.Count }
            };
        }

        private static object RefiningForecastState(JObject args)
        {
            int? npcId = args.Value<int?>("npcId"); int? itemId = args.Value<int?>("itemId");
            if (!npcId.HasValue || !itemId.HasValue) throw new RpcException("INVALID_ARGUMENT", "npcId and itemId are required");
            Npc npc = RequireNpc(npcId.Value); ItemThing item = RequireThing(itemId.Value) as ItemThing;
            if (item == null) throw new RpcException("ITEM_NOT_FOUND", "Thing is not an item");
            Dictionary<int, float> mindSuccess = PrivateStaticField<Dictionary<int, float>>(typeof(ToilRefining), "MindState2Refining");
            Dictionary<int, float> mindInherit = PrivateStaticField<Dictionary<int, float>>(typeof(ToilRefining), "MindState2RefiningRate");
            List<int> fengShui = PrivateStaticField<List<int>>(typeof(ToilRefining), "FengShuiRefining");
            int mindLevel = npc.Needs == null ? 0 : npc.Needs.GetNeedLevel(g_emNeedType.MindState);
            float mindValue = npc.Needs == null ? 0f : npc.Needs.GetNeedValue(g_emNeedType.MindState);
            float mindSuccessValue = mindSuccess != null && mindSuccess.ContainsKey(mindLevel) ? mindSuccess[mindLevel] : 0f;
            float mindInheritValue = mindInherit != null && mindInherit.ContainsKey(mindLevel) ? mindInherit[mindLevel] : 0f;
            int skillLevel = npc.PropertyMgr == null || npc.PropertyMgr.SkillData == null ? 0 : npc.PropertyMgr.SkillData.GetSkillLevel(g_emNpcSkillType.Manual);
            float successRate = 0.6f - 0.4f * item.Rate + 0.1f * skillLevel + mindSuccessValue + npc.GetProperty("FabaoMake_SuccessRateAddV");
            bool forcedFailure = npc.CheckSpecialFlag((g_emNpcSpecailFlag)70001) > 0;
            bool forcedSuccess = npc.CheckSpecialFlag((g_emNpcSpecailFlag)70002) > 0;
            if (forcedFailure) successRate = 0f;
            if (forcedSuccess) successRate = 1f;
            BuildingThing building = null; int? buildingId = args.Value<int?>("buildingId");
            if (buildingId.HasValue) building = RequireThing(buildingId.Value) as BuildingThing; else building = npc.InBuilding;
            float? fengShuiFactor = null;
            if (building != null && fengShui != null) {
                int rank = (int)building.FengShui;
                if (rank >= 0 && rank < fengShui.Count) fengShuiFactor = npc.PropertyMgr.Luck + fengShui[rank];
            }
            float qualityBase = npc.GetProperty("FabaoMake_QualityAddV") + npc.GetProperty("MadeQualityAddValue");
            float expectedQuality = npc.GetProperty("ManufactureExpectQuality");
            float qualityPercentMin = npc.GetProperty("ManufactureRealQualityPercentMin");
            float qualityPercentMax = npc.GetProperty("ManufactureRealQualityPercentMax");
            float qualityMin = qualityBase + expectedQuality * Math.Min(qualityPercentMin, qualityPercentMax);
            float qualityMax = qualityBase + expectedQuality * Math.Max(qualityPercentMin, qualityPercentMax);
            float inheritBase = npc.GetProperty("FabaoMake_LingInheritRateAddV") * mindInheritValue;
            float? inheritMin = null; float? inheritMax = null;
            if (fengShuiFactor.HasValue) {
                float randomA = fengShuiFactor.Value * -5f; float randomB = fengShuiFactor.Value * 10f;
                inheritMin = inheritBase * (1f + Math.Min(randomA, randomB) / 100f);
                inheritMax = inheritBase * (1f + Math.Max(randomA, randomB) / 100f);
            }
            float twelveRateChance = npc.GetProperty("FabaoMake_TwelveRateChance") * mindValue / 50f;
            return new {
                npcId = npc.ID, item = ItemSummary(item), buildingId = building == null ? (int?)null : building.ID,
                successRate = successRate, forcedFailure = forcedFailure, forcedSuccess = forcedSuccess,
                inputs = new { itemRate = item.Rate, manualSkillLevel = skillLevel, mindLevel = mindLevel, mindValue = mindValue, mindSuccess = mindSuccessValue, mindInherit = mindInheritValue, fengShuiFactor = fengShuiFactor },
                quality = new { minimum = qualityMin, maximum = qualityMax, baseValue = qualityBase, expected = expectedQuality, percentMinimum = qualityPercentMin, percentMaximum = qualityPercentMax },
                lingInheritance = new { minimum = inheritMin, maximum = inheritMax, baseValue = inheritBase, requiresBuilding = building == null },
                twelveRateChance = twelveRateChance, deterministic = false, source = "ToilRefining.MakeFaBao native formula; random rolls are returned as ranges"
            };
        }

        private static object NpcInventory(Npc npc)
        {
            List<object> equipped = new List<object>();
            if (npc.Equip != null) {
                List<ItemThing> items = null;
                try { items = npc.Equip.GetEquipAll(true, false); } catch { }
                if (items != null) foreach (ItemThing item in items) if (item != null) equipped.Add(ItemSummary(item));
            }
            List<object> carried = new List<object>();
            if (npc.Bag != null && npc.Bag.m_lisItems != null)
                foreach (ItemThing item in npc.Bag.m_lisItems) if (item != null) carried.Add(ItemSummary(item));
            return new { equipped = equipped, equippedCount = equipped.Count, carried = carried, carriedCount = carried.Count };
        }

        private static object NpcInventoryState(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            Npc npc = RequireNpc(id.Value);
            return new { id = npc.ID, name = SafeString(delegate { return npc.GetName(); }), inventory = NpcInventory(npc) };
        }

        private static object CastSummary(XiaWorld.Fight.SkillCastData cast)
        {
            if (cast == null) return null;
            return new { skill = cast.Skill, targetThingId = cast.ThingID, targetKey = cast.TargetKey, parameter = cast.sParam };
        }

        private static object CombatSummary(Npc npc)
        {
            XiaWorld.Fight.FightBody body = npc.FightBody;
            if (body == null) return new { available = false, id = npc.ID, name = SafeString(delegate { return npc.GetName(); }) };
            List<object> skills = new List<object>();
            if (body.Skills != null) foreach (KeyValuePair<string, float> pair in body.Skills)
                skills.Add(new { name = pair.Key, cooldown = pair.Value, castable = SafeValue(delegate { return (object)body.CheckSkillCast(pair.Key); }) });
            return new {
                available = true,
                id = npc.ID,
                name = SafeString(delegate { return npc.GetName(); }),
                fighting = body.IsFighting,
                cantStop = body.CantStop,
                attacker = body.IsAttacker,
                camp = body.Camp.ToString(),
                targetId = body.TargetID,
                target = ThingSummary(body.Target),
                casting = CastSummary(body.Casting),
                waitingCast = CastSummary(body.WaitCast),
                normalAttack = body.NormalAttack,
                autoNext = body.AutoNext,
                autoSkills = CopyStrings(body.AutoSkills),
                skills = skills,
                artifactIds = body.FabaoIDs == null ? new int[0] : body.FabaoIDs.ToArray()
            };
        }

        private static object CombatState(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (id.HasValue) return CombatSummary(RequireNpc(id.Value));
            List<Npc> source = RequireMap().Things.GetPlayerActiveNpcs(g_emNpcRaceType.Wisdom) ?? new List<Npc>();
            List<object> items = new List<object>();
            foreach (Npc npc in source) if (npc != null) items.Add(CombatSummary(npc));
            return new { items = items, count = items.Count };
        }

        private static XiaWorld.Fight.FightBody RequireFightBody(int id)
        {
            Npc npc = RequireNpc(id);
            if (npc.FightBody == null) throw new RpcException("COMBAT_UNAVAILABLE", "NPC " + id + " has no fight body");
            return npc.FightBody;
        }

        private static object AttackTarget(JObject args)
        {
            int? attackerId = args.Value<int?>("attackerId");
            int? targetId = args.Value<int?>("targetId");
            if (!attackerId.HasValue || !targetId.HasValue) throw new RpcException("INVALID_ARGUMENT", "attackerId and targetId are required");
            Npc attacker = RequireNpc(attackerId.Value);
            Thing target = RequireThing(targetId.Value);
            if (attacker.IsDeath || target.IsDeath) throw new RpcException("INVALID_TARGET", "attacker and target must be alive");
            XiaWorld.Fight.FightBody body = RequireFightBody(attackerId.Value);
            body.SetTarget(target);
            body.FightBegin(args.Value<bool?>("cantStop") ?? false);
            return new { commandAccepted = true, attackerId = attackerId.Value, targetId = targetId.Value, combat = CombatSummary(attacker) };
        }

        private static object StopCombat(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            Npc npc = RequireNpc(id.Value);
            XiaWorld.Fight.FightBody body = RequireFightBody(id.Value);
            if (body.CantStop && !(args.Value<bool?>("force") ?? false)) throw new RpcException("COMBAT_LOCKED", "The fight cannot currently be stopped; pass force only if the caller explicitly accepts overriding it");
            body.InterruptFight();
            body.FightEnd();
            return new { commandAccepted = true, id = id.Value, combat = CombatSummary(npc) };
        }

        private static object CastSkill(JObject args)
        {
            int? id = args.Value<int?>("id");
            string skill = ((string)args["skill"] ?? "").Trim();
            if (!id.HasValue || skill.Length == 0) throw new RpcException("INVALID_ARGUMENT", "id and skill are required");
            XiaWorld.Fight.FightBody body = RequireFightBody(id.Value);
            Thing target = args.Value<int?>("targetId").HasValue ? RequireThing(args.Value<int>("targetId")) : body.Target;
            int key = args.Value<int?>("key") ?? (target == null ? RequireNpc(id.Value).Key : target.Key);
            bool accepted = body.CastSkill(skill, target, key, (string)args["parameter"]);
            return new { commandAccepted = accepted, id = id.Value, skill = skill, targetId = target == null ? (int?)null : target.ID, key = key, combat = CombatSummary(RequireNpc(id.Value)) };
        }

        private static object SetAutoSkills(JObject args)
        {
            int? id = args.Value<int?>("id");
            JArray input = args["skills"] as JArray;
            if (!id.HasValue || input == null) throw new RpcException("INVALID_ARGUMENT", "id and skills array are required");
            List<string> skills = new List<string>();
            foreach (JToken token in input) if (!String.IsNullOrEmpty((string)token)) skills.Add((string)token);
            XiaWorld.Fight.FightBody body = RequireFightBody(id.Value);
            foreach (string skill in skills) if (!body.HasFightSkill(skill)) throw new RpcException("SKILL_NOT_KNOWN", "NPC does not have fight skill " + skill);
            body.SetAutoSkill(skills);
            return new { commandAccepted = true, id = id.Value, skills = CopyStrings(body.AutoSkills), combat = CombatSummary(RequireNpc(id.Value)) };
        }

        private static object CastArtifacts(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            XiaWorld.Fight.g_emFaBaoMode mode = ParseEnum<XiaWorld.Fight.g_emFaBaoMode>(args, "mode");
            XiaWorld.Fight.FightBody body = RequireFightBody(id.Value);
            int? targetId = args.Value<int?>("targetId");
            if (targetId.HasValue) body.SetTarget(RequireThing(targetId.Value));
            body.CastFabao(null, mode);
            return new { commandAccepted = true, id = id.Value, mode = mode.ToString(), combat = CombatSummary(RequireNpc(id.Value)) };
        }

        private static object BuildCatalogState(JObject args)
        {
            UILogicMgr manager = UILogicMgr.Instance;
            if (manager == null || manager.MainMenuListDatas == null) throw new RpcException("UI_NOT_READY", "Build catalog is unavailable");
            string query = ((string)args["query"] ?? "").Trim();
            List<object> items = new List<object>();
            Dictionary<string, List<UIMainMenuListDef_Data>> groups;
            if (!manager.MainMenuListDatas.TryGetValue("Builds", out groups) || groups == null)
                return new { items = items, count = 0, query = query, schoolUnlockLevel = SchoolMgr.Instance == null ? 0 : SchoolMgr.Instance.UnLockLevel };
            int schoolUnlockLevel = SchoolMgr.Instance == null ? 0 : SchoolMgr.Instance.UnLockLevel;
            foreach (KeyValuePair<string, List<UIMainMenuListDef_Data>> group in groups) {
                if (group.Value == null) continue;
                foreach (UIMainMenuListDef_Data data in group.Value) {
                    if (data == null || !String.Equals(data.CMDType, "Build", StringComparison.OrdinalIgnoreCase)) continue;
                    ThingDef def = SafeValue(delegate { return (object)ThingMgr.Instance.GetDef(g_emThingType.Building, data.ObjName, true); }) as ThingDef;
                    string searchable = data.Kind + " " + group.Key + " " + data.Name + " " + data.DataName + " " + data.ObjName + " " + data.Display + " " + (def == null ? "" : def.Name + " " + def.ThingName);
                    if (query.Length > 0 && searchable.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    bool levelUnlocked = data.Unlock <= schoolUnlockLevel;
                    bool definitionUnlocked = def == null || def.Building == null || def.Building.AutoUnLock > 0 || (SchoolMgr.Instance != null && SchoolMgr.Instance.IsBuildingInUnLockList(data.ObjName));
                    items.Add(new {
                        key = data.Kind + "/" + group.Key + "/" + data.Name,
                        name = data.Name,
                        dataName = data.DataName,
                        objectName = data.ObjName,
                        displayName = data.Display,
                        description = data.Desc,
                        defName = def == null ? null : def.Name,
                        thingName = def == null ? null : def.ThingName,
                        kind = data.Kind,
                        type = data.Type,
                        group = group.Key,
                        label = data.Label,
                        unlockLevel = data.Unlock,
                        levelUnlocked = levelUnlocked,
                        unlocked = levelUnlocked && definitionUnlocked,
                        unlockVisible = data.UnLockShow,
                        buildMode = data.BuildMode
                    });
                }
            }
            return new { items = items, count = items.Count, query = query, schoolUnlockLevel = schoolUnlockLevel };
        }

        private static object BeginBuild(JObject args)
        {
            string name = ((string)args["name"] ?? "").Trim();
            if (name.Length == 0) throw new RpcException("INVALID_ARGUMENT", "name is required; use state.buildCatalog to discover it");
            UILogicMgr manager = RequireMapInput(args);
            ThingDef stuff = null;
            string stuffName = ((string)args["stuffDefName"] ?? "").Trim();
            if (stuffName.Length > 0) {
                stuff = ThingMgr.Instance.GetDef(g_emThingType.Item, stuffName, true);
                if (stuff == null) throw new RpcException("STUFF_NOT_FOUND", "No item material definition exists with name " + stuffName);
            }
            manager.ChangeMode(g_emUILogicMode.Build, null, false, new object[] { name, stuff });
            UILogicMode_Build buildMode = manager.GetCurMode() as UILogicMode_Build;
            string requestedDirection = ((string)args["direction"] ?? "").Trim();
            if (requestedDirection.Length > 0) {
                g_emThingDir direction = ParseEnum<g_emThingDir>(args, "direction");
                for (int i = 0; buildMode != null && i < 4 && !String.Equals(Convert.ToString(PrivateValue(buildMode, "dir")), direction.ToString(), StringComparison.OrdinalIgnoreCase); i++) buildMode.ChangeDir();
            }
            return new { commandAccepted = true, name = name, stuffDefName = stuff == null ? null : stuff.Name, direction = buildMode == null ? null : Convert.ToString(PrivateValue(buildMode, "dir")), mode = manager.Mode.ToString(), modeType = manager.GetCurMode() == null ? null : manager.GetCurMode().GetType().FullName };
        }

        private static object PlaceBuilding(JObject args)
        {
            object begin = BeginBuild(args);
            object click = MapClick(args);
            return new { commandAccepted = true, completion = "dispatched-and-read-back", build = begin, placement = click };
        }

        private static object MoveBuilding(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "building id, destination grid, and confirm=true are required");
            BuildingThing building = RequireBuilding(id.Value);
            if (!building.IsPlayerThing || building.BuildingState != g_emBuildingState.Working) throw new RpcException("BUILDING_NOT_MOVABLE", "A finished working player building is required");
            Map map = RequireMap();
            int key = ResolveGridKey(args, map);
            if (key == building.Key) throw new RpcException("INVALID_ARGUMENT", "Destination must differ from the current grid");
            UILogicMgr manager = RequireMapInput(args);
            manager.ChangeMode(g_emUILogicMode.Build, null, false, new object[] { building.def.Name, building.StuffDef, building, null });
            UILogicMode_Build mode = manager.GetCurMode() as UILogicMode_Build;
            string requestedDirection = ((string)args["direction"] ?? "").Trim();
            if (requestedDirection.Length > 0) {
                g_emThingDir direction = ParseEnum<g_emThingDir>(args, "direction");
                for (int i = 0; mode != null && i < 4 && !String.Equals(Convert.ToString(PrivateValue(mode, "dir")), direction.ToString(), StringComparison.OrdinalIgnoreCase); i++) mode.ChangeDir();
            }
            object click = MapClick(new JObject(new JProperty("key", key), new JProperty("allowBlocked", args.Value<bool?>("allowBlocked") ?? false)));
            return new { commandAccepted = true, completion = "queued-and-read-back", source = BuildingSummary(building), destination = GridState(new JObject(new JProperty("key", key))), placement = click };
        }

        private static object ProductionTaskSummary(BuildingProduce.BuildingProduceData task)
        {
            if (task == null) return null;
            return new { id = task.ID, name = task.name, productDefName = task.thingDef == null ? null : task.thingDef.Name, stuffDefName = task.StuffDef == null ? null : task.StuffDef.Name, count = task.Count, loop = task.Loop, progress = task.progress, value = task.value, gettingMaterials = task.getting, makerNpcId = task.Maker, parameter = task.Param, menuId = task.MenuId };
        }

        private static object BuildingSummary(BuildingThing building)
        {
            List<object> tasks = new List<object>();
            if (building.ProduceMachine != null && building.ProduceMachine.m_lisProduceList != null)
                foreach (BuildingProduce.BuildingProduceData task in building.ProduceMachine.m_lisProduceList) if (task != null) tasks.Add(ProductionTaskSummary(task));
            return new {
                thing = ThingSummary(building),
                buildingState = building.BuildingState.ToString(),
                buildPercentage = building.BuildPercentage,
                workerCount = building.WorkerCount,
                permission = building.Permission.ToString(),
                fengShui = SafeString(delegate { return building.FengShui.ToString(); }),
                fengShuiValue = SafeValue(delegate { return (object)building.FengShuiValue; }),
                productionAvailable = building.ProduceMachine != null,
                productionTasks = tasks,
                productionTaskCount = tasks.Count
            };
        }

        private static BuildingThing RequireBuilding(int id)
        {
            BuildingThing building = RequireThing(id) as BuildingThing;
            if (building == null) throw new RpcException("BUILDING_NOT_FOUND", "Thing " + id + " is not a building");
            return building;
        }

        private static object BuildingState(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            return BuildingSummary(RequireBuilding(id.Value));
        }

        private static object AddProductionTask(JObject args)
        {
            int? id = args.Value<int?>("id");
            string name = ((string)args["name"] ?? "").Trim();
            if (!id.HasValue || name.Length == 0) throw new RpcException("INVALID_ARGUMENT", "id and recipe/task name are required");
            BuildingThing building = RequireBuilding(id.Value);
            if (building.ProduceMachine == null) throw new RpcException("PRODUCTION_UNAVAILABLE", "Building has no production machine");
            BuildingProduce.BuildingProduceData task = building.ProduceMachine.AddTask(name);
            if (task == null) throw new RpcException("PRODUCTION_REJECTED", "The game rejected production task " + name);
            int loop = Math.Max(0, args.Value<int?>("loop") ?? 0);
            if (loop > 0) building.ProduceMachine.LoopTask(building.ProduceMachine.m_lisProduceList.IndexOf(task), loop);
            return new { commandAccepted = true, task = ProductionTaskSummary(task), building = BuildingSummary(building) };
        }

        private static object RemoveProductionTask(JObject args)
        {
            int? id = args.Value<int?>("id");
            int? taskId = args.Value<int?>("taskId");
            if (!id.HasValue || !taskId.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "id, taskId, and confirm=true are required");
            BuildingThing building = RequireBuilding(id.Value);
            if (building.ProduceMachine == null) throw new RpcException("PRODUCTION_UNAVAILABLE", "Building has no production machine");
            building.ProduceMachine.RemoveTaskByID(taskId.Value, true);
            return new { commandAccepted = true, removedTaskId = taskId.Value, building = BuildingSummary(building) };
        }

        private static object ProductionCatalogState(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            BuildingThing building = RequireBuilding(id.Value);
            if (building.ProduceMachine == null) throw new RpcException("PRODUCTION_UNAVAILABLE", "Building has no production machine");
            string query = ((string)args["query"] ?? "").Trim();
            List<object> items = new List<object>();
            if (building.ProduceMachine.m_lisProduceMenu != null) foreach (BuildingProduce.BuildingProduceData task in building.ProduceMachine.m_lisProduceMenu) {
                if (task == null) continue;
                string searchable = (task.name ?? "") + " " + (task.thingDef == null ? "" : task.thingDef.Name + " " + task.thingDef.ThingName);
                if (query.Length > 0 && searchable.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                items.Add(ProductionTaskSummary(task));
            }
            return new { buildingId = id.Value, items = items, count = items.Count, query = query };
        }

        private static object RecipeState(JObject args)
        {
            PracticeMgr manager = PracticeMgr.Instance;
            if (manager == null) throw new RpcException("GAME_NOT_READY", "PracticeMgr is unavailable");
            int? npcId = args.Value<int?>("npcId");
            NpcPractice practice = npcId.HasValue ? RequirePractice(npcId.Value) : null;
            string kind = ((string)args["kind"] ?? "all").Trim().ToLowerInvariant();
            string query = ((string)args["query"] ?? "").Trim();
            List<object> items = new List<object>();
            if (kind == "all" || kind == "alchemy" || kind == "dan") foreach (KeyValuePair<string, DanFormulaDef> pair in manager.DanFormulaDefs) {
                DanFormulaDef def = pair.Value;
                if (def == null) continue;
                string searchable = pair.Key + " " + def.Name + " " + def.ItemName;
                if (query.Length > 0 && searchable.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                items.Add(new { kind = "alchemy", key = pair.Key, name = def.Name, itemName = def.ItemName, level = def.Level, successRate = def.SuccessRate, count = def.Count, normal = def.Normal, learned = practice == null ? (bool?)null : practice.DanFormulas.Contains(def.Name) });
            }
            if (kind == "all" || kind == "artifact" || kind == "fabao") foreach (KeyValuePair<string, FabaoFormulaDef> pair in manager.FabaoFormulaDefs) {
                FabaoFormulaDef def = pair.Value;
                if (def == null) continue;
                string searchable = pair.Key + " " + def.Name + " " + def.ItemName;
                if (query.Length > 0 && searchable.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                items.Add(new { kind = "artifact", key = pair.Key, name = def.Name, itemName = def.ItemName, level = def.Level, successRate = def.SuccessRate, count = def.Count, normal = def.Normal, learned = practice == null ? (bool?)null : practice.GetFabaoFormulas.Contains(def.Name) });
            }
            return new { items = items, count = items.Count, kind = kind, query = query, npcId = npcId };
        }

        private static object FormationSummary(ZhenMgr.Zhen z)
        {
            if (z == null) return null;
            return new {
                id = z.ID, name = z.szZhenName, enemy = z.Enemy != 0, coreNpcId = z.nCoreNpcId,
                npcIds = z.aryZhenNpcId ?? new int[0], activeNpcIds = z.npcInZhen == null ? new int[0] : z.npcInZhen.ConvertAll(delegate(Npc npc) { return npc == null ? 0 : npc.ID; }).ToArray(),
                revealedNpcIds = z.revealNpcIds == null ? new int[0] : new List<int>(z.revealNpcIds).ToArray(),
                brokenNpcIds = z.brokenNpcIds == null ? new int[0] : new List<int>(z.brokenNpcIds).ToArray(),
                stability = z.fTotalStability, baseStability = z.fBaseStability, pressure = z.fPressureValue,
                shield = z.fZhenSheildValue, formationPoints = z.nRPoint, maxFormationPoints = z.MaxZRPoint,
                costLingPerSecond = z.CostLingPerSec, shapeId = z.shape == null ? (int?)null : z.shape.ID, needBroken = z.NeedBroken
            };
        }

        private static object FormationState(JObject args)
        {
            ZhenMgr manager = ZhenMgr.Instance;
            if (manager == null) throw new RpcException("GAME_NOT_READY", "ZhenMgr is unavailable");
            int? requested = args.Value<int?>("id");
            List<object> items = new List<object>();
            foreach (KeyValuePair<int, ZhenMgr.Zhen> pair in manager.m_mapZhen) {
                if (requested.HasValue && pair.Key != requested.Value) continue;
                items.Add(FormationSummary(pair.Value));
            }
            return new { items = items, count = items.Count, unlockedNodeCount = manager.GetActivityZhenNodeCount(), accessibleNodeCount = manager.GetAccessableZhenNodeCount() };
        }

        private static object FormationCatalogState(JObject args)
        {
            ZhenMgr manager = ZhenMgr.Instance;
            if (manager == null) throw new RpcException("GAME_NOT_READY", "ZhenMgr is unavailable");
            List<object> shapes = new List<object>();
            foreach (KeyValuePair<int, ZhenMgr.ZhenShape> pair in manager.m_mapZhenShape) {
                ZhenMgr.ZhenShape shape = pair.Value;
                if (shape == null) continue;
                List<object> nodes = new List<object>();
                if (shape.nodes != null) foreach (ZhenMgr.ZhenShape.Node node in shape.nodes) if (node != null) nodes.Add(new { name = node.nodeName, x = node.x, y = node.y, rotation = node.roti });
                shapes.Add(new { id = shape.ID, name = shape.ShapeName, description = shape.Descript, legal = shape.Legal, enemy = shape.Enemy != 0, system = shape.nSystem, centerX = shape.centerx, centerY = shape.centery, nodes = nodes, nodeCount = nodes.Count, pressure = shape.GetPressure() });
            }
            List<object> schemes = new List<object>();
            foreach (KeyValuePair<int, ZhenMgr.ZhenScheme> pair in manager.m_mapZhenScheme) {
                ZhenMgr.ZhenScheme scheme = pair.Value;
                if (scheme != null) schemes.Add(new { id = scheme.ID, name = scheme.name, shapeId = scheme.nShapeID, npcIds = scheme.npcids == null ? new int[0] : scheme.npcids.ToArray() });
            }
            List<object> nodeDefinitions = new List<object>();
            Dictionary<string, ZhenNodeDef> nodeDefs = PrivateStaticField<Dictionary<string, ZhenNodeDef>>(typeof(ZhenMgr), "s_mapZhenNode");
            if (nodeDefs != null) foreach (KeyValuePair<string, ZhenNodeDef> pair in nodeDefs) {
                ZhenNodeDef def = pair.Value;
                if (def == null || def.HideInUi != 0 || !manager.IsZhenNodeActive(def)) continue;
                List<object> slots = new List<object>();
                if (def.Slots != null) foreach (ZhenSlot slot in def.Slots) if (slot != null) {
                    ZhenSlotDef slotDef = manager.GetSlotDef(slot.SlotName);
                    slots.Add(new { name = slot.SlotName, position = slot.Pos, displayName = slotDef == null ? null : slotDef.DisplayName, type = slotDef == null ? null : slotDef.SlotType, level = slotDef == null ? (int?)null : slotDef.Lv });
                }
                nodeDefinitions.Add(new { key = pair.Key, name = def.Name, displayName = def.DisplayName, main = def.IsMain, level = def.Lv, slotCount = def.SlotCount, slots = slots });
            }
            return new { shapes = shapes, shapeCount = shapes.Count, schemes = schemes, schemeCount = schemes.Count, nodeDefinitions = nodeDefinitions, nodeDefinitionCount = nodeDefinitions.Count };
        }

        private static object CreateFormationShape(JObject args)
        {
            string name = ((string)args["name"] ?? "").Trim();
            string description = ((string)args["description"] ?? "").Trim();
            JArray inputNodes = args["nodes"] as JArray;
            if (name.Length == 0 || inputNodes == null || inputNodes.Count == 0 || inputNodes.Count > 64 || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "name, 1-64 nodes, and confirm=true are required");
            ZhenMgr manager = ZhenMgr.Instance;
            if (manager == null) throw new RpcException("GAME_NOT_READY", "ZhenMgr is unavailable");
            List<ZhenMgr.ZhenShape.Node> nodes = new List<ZhenMgr.ZhenShape.Node>();
            HashSet<string> positions = new HashSet<string>();
            for (int i = 0; i < inputNodes.Count; i++) {
                JObject input = inputNodes[i] as JObject;
                string nodeName = input == null ? "" : ((string)input["name"] ?? "").Trim();
                int? x = input == null ? null : input.Value<int?>("x");
                int? y = input == null ? null : input.Value<int?>("y");
                int rotation = input == null ? 0 : (input.Value<int?>("rotation") ?? 0);
                ZhenNodeDef def = nodeName.Length == 0 ? null : manager.GetNodeDef(nodeName);
                if (def == null || !manager.IsZhenNodeActive(def)) throw new RpcException("FORMATION_NODE_INVALID", "Unknown or locked formation node at index " + i + ": " + nodeName);
                if (!x.HasValue || !y.HasValue || x.Value < -32 || x.Value > 32 || y.Value < -32 || y.Value > 32 || rotation < 0 || rotation > 5) throw new RpcException("FORMATION_NODE_INVALID", "Each node needs x/y in -32..32 and rotation in 0..5");
                string position = x.Value + ":" + y.Value;
                if (!positions.Add(position)) throw new RpcException("FORMATION_NODE_OVERLAP", "Multiple nodes use position " + position);
                if (i == 0 && !def.IsMain) throw new RpcException("FORMATION_CORE_REQUIRED", "The first node must be a main/core node");
                nodes.Add(new ZhenMgr.ZhenShape.Node { nodeName = def.Name, x = x.Value, y = y.Value, roti = rotation });
            }
            ZhenMgr.ZhenShape shape = manager.CreateShape(name, false);
            if (shape == null) throw new RpcException("FORMATION_SHAPE_REJECTED", "The game rejected formation shape creation");
            shape.Descript = description;
            manager.SetShapeNode(shape, nodes);
            if (!shape.Legal) {
                manager.DestoryShape(shape.ID);
                throw new RpcException("FORMATION_SHAPE_ILLEGAL", "The native formation parser rejected the node links or slot orientations");
            }
            return new { commandAccepted = true, shape = new { id = shape.ID, name = shape.ShapeName, description = shape.Descript, legal = shape.Legal, centerX = shape.centerx, centerY = shape.centery, nodeCount = shape.nodes.Count, pressure = shape.GetPressure() } };
        }

        private static object RemoveFormationShape(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "id and confirm=true are required");
            ZhenMgr manager = ZhenMgr.Instance;
            ZhenMgr.ZhenShape shape = manager == null ? null : manager.GetZhenShape(id.Value);
            if (shape == null) throw new RpcException("FORMATION_SHAPE_NOT_FOUND", "Formation shape was not found");
            if (shape.nSystem != 0) throw new RpcException("FORMATION_SHAPE_PROTECTED", "System formation shapes cannot be removed");
            if (manager.IsShapeUseByZhen(id.Value)) throw new RpcException("FORMATION_SHAPE_IN_USE", "Cancel the active formation before removing its shape");
            manager.DestoryShape(id.Value);
            return new { commandAccepted = true, removed = manager.GetZhenShape(id.Value) == null, id = id.Value };
        }

        private static object SaveFormationScheme(JObject args)
        {
            int? shapeId = args.Value<int?>("shapeId");
            JArray ids = args["npcIds"] as JArray;
            string name = ((string)args["name"] ?? "").Trim();
            if (!shapeId.HasValue || ids == null || ids.Count == 0 || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "shapeId, npcIds, and confirm=true are required");
            ZhenMgr manager = ZhenMgr.Instance;
            ZhenMgr.ZhenShape shape = manager == null ? null : manager.GetZhenShape(shapeId.Value);
            if (shape == null || !shape.Legal) throw new RpcException("FORMATION_SHAPE_INVALID", "Formation shape was not found or is not legal");
            if (shape.nodes == null || shape.nodes.Count != ids.Count) throw new RpcException("FORMATION_SIZE_MISMATCH", "npcIds count must match the shape node count");
            List<int> npcIds = new List<int>();
            HashSet<int> unique = new HashSet<int>();
            foreach (JToken token in ids) {
                Npc npc = RequireNpc(token.Value<int>());
                if (!npc.CanSelect4Zhen || !unique.Add(npc.ID)) throw new RpcException("FORMATION_NPC_INVALID", "Every scheme member must be unique and selectable for formations");
                npcIds.Add(npc.ID);
            }
            ZhenMgr.ZhenScheme scheme = manager.CreateZhenScheme(shapeId.Value);
            if (scheme == null) throw new RpcException("FORMATION_SCHEME_REJECTED", "The game rejected formation scheme creation");
            scheme.npcids = npcIds;
            if (name.Length > 0) scheme.name = name;
            manager.MarkZhenSchemeChange();
            return new { commandAccepted = true, scheme = new { id = scheme.ID, name = scheme.name, shapeId = scheme.nShapeID, npcIds = scheme.npcids.ToArray() } };
        }

        private static object RemoveFormationScheme(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "id and confirm=true are required");
            ZhenMgr manager = ZhenMgr.Instance;
            if (manager == null || manager.GetZhenScheme(id.Value) == null) throw new RpcException("FORMATION_SCHEME_NOT_FOUND", "Formation scheme was not found");
            manager.DestroyZhenScehme(id.Value);
            return new { commandAccepted = true, removed = manager.GetZhenScheme(id.Value) == null, id = id.Value };
        }

        private static object CreateFormation(JObject args)
        {
            int? shapeId = args.Value<int?>("shapeId");
            JArray ids = args["npcIds"] as JArray;
            if (!shapeId.HasValue || ids == null || ids.Count == 0 || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "shapeId, npcIds, and confirm=true are required");
            ZhenMgr manager = ZhenMgr.Instance;
            ZhenMgr.ZhenShape shape = manager == null ? null : manager.GetZhenShape(shapeId.Value);
            if (shape == null || !shape.Legal) throw new RpcException("FORMATION_SHAPE_INVALID", "Formation shape was not found or is not legal");
            List<Npc> npcs = new List<Npc>();
            foreach (JToken token in ids) npcs.Add(RequireNpc(token.Value<int>()));
            if (shape.nodes != null && shape.nodes.Count != npcs.Count) throw new RpcException("FORMATION_SIZE_MISMATCH", "npcIds count must match the shape node count");
            ZhenMgr.Zhen formation = manager.CreateZhen(false, shape, npcs);
            if (formation == null) throw new RpcException("FORMATION_REJECTED", "The game rejected formation creation");
            return new { commandAccepted = true, formation = FormationSummary(formation) };
        }

        private static object CancelFormation(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "id and confirm=true are required");
            ZhenMgr manager = ZhenMgr.Instance;
            ZhenMgr.Zhen formation = manager == null ? null : manager.GetZhen(id.Value);
            if (formation == null) throw new RpcException("FORMATION_NOT_FOUND", "Formation was not found");
            object before = FormationSummary(formation);
            manager.CancelZhen(formation, false);
            return new { commandAccepted = true, previous = before, removed = manager.GetZhen(id.Value) == null };
        }

        private static object Apprentice(JObject args)
        {
            int? studentId = args.Value<int?>("studentId");
            int? masterId = args.Value<int?>("masterId");
            if (!studentId.HasValue || !masterId.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "studentId, masterId, and confirm=true are required");
            if (studentId.Value == masterId.Value) throw new RpcException("INVALID_ARGUMENT", "student and master must be different NPCs");
            Npc student = RequireNpc(studentId.Value);
            Npc master = RequireNpc(masterId.Value);
            NpcPractice practice = RequirePractice(studentId.Value);
            practice.Apprentice(master);
            return new { commandAccepted = true, studentId = studentId.Value, masterId = masterId.Value, cultivation = NpcCultivation(student) };
        }

        private static object TeachSecret(JObject args)
        {
            int? teacherId = args.Value<int?>("teacherId");
            int? studentId = args.Value<int?>("studentId");
            string secret = ((string)args["secret"] ?? "").Trim();
            if (!teacherId.HasValue || !studentId.HasValue || secret.Length == 0 || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "teacherId, studentId, secret, and confirm=true are required");
            Npc teacher = RequireNpc(teacherId.Value);
            Npc student = RequireNpc(studentId.Value);
            NpcPractice teacherPractice = RequirePractice(teacherId.Value);
            if (!teacherPractice.LearnedEsotericaList.Contains(secret) && !teacherPractice.LearnedTreeList.Contains(secret)) throw new RpcException("SECRET_NOT_KNOWN", "Teacher has not learned the requested secret");
            if (!RequirePractice(studentId.Value).CanBeTeach(secret)) throw new RpcException("SECRET_NOT_TEACHABLE", "Student cannot be taught the requested secret");
            Command command = teacher.AddCommand("TeachHim", new object[] { student.ID, secret });
            if (command == null) throw new RpcException("COMMAND_REJECTED", "The game rejected the teaching command");
            return new { commandAccepted = true, teacherId = teacher.ID, studentId = student.ID, secret = secret, command = CommandSummary(command) };
        }

        private static AreaBase RequireArea(int id)
        {
            AreaMgr manager = AreaMgr.Instance;
            AreaBase area = null;
            Dictionary<int, AreaBase> areas = PrivateField<Dictionary<int, AreaBase>>(manager, "m_Id2Areas");
            if (areas != null) areas.TryGetValue(id, out area);
            if (area == null) throw new RpcException("AREA_NOT_FOUND", "No area exists with id " + id);
            return area;
        }

        private static object AreaSummary(AreaBase area)
        {
            if (area == null) return null;
            AreaPlant plant = area as AreaPlant;
            AreaStorage storage = area as AreaStorage;
            AreaGuard guard = area as AreaGuard;
            AreaBreeding breeding = area as AreaBreeding;
            HashSet<Npc> breedingNpcs = breeding == null ? null : PrivateField<HashSet<Npc>>(breeding, "hsNpcInArea");
            HashSet<string> breedingPlants = breeding == null ? null : PrivateField<HashSet<string>>(breeding, "hsPlants");
            return new {
                id = area.ID, name = area.Name, type = area.GetType().Name, defName = area.def == null ? null : area.def.Name,
                gridCount = area.m_lisGrids == null ? 0 : area.m_lisGrids.Count,
                grids = area.m_lisGrids == null ? new int[0] : area.m_lisGrids.ToArray(),
                center = new { x = area.Center.x, y = area.Center.y, z = area.Center.z }, camp = area.Camp.ToString(), visible = area.show,
                plant = plant == null ? null : new { crop = plant.PlantName, discipleOnly = plant.IsDisciple },
                storage = storage == null ? null : new {
                    priority = storage.Priority, canSell = storage.CanSale, onlyFengShuiItems = storage.onlyFSItem, onlyBigFish = storage.onlyBigFish,
                    itemRate = new { min = storage.IncludeItemRate.x, max = storage.IncludeItemRate.y },
                    allowedElements = storage.IncludeElement, allowedQualities = storage.IncludeItemQ,
                    excludedLabels = storage.ExcludeItemLable == null ? new int[0] : new List<int>(storage.ExcludeItemLable).ToArray()
                },
                guard = guard == null ? null : new { npcIds = guard.GuardIds == null ? new int[0] : new List<int>(guard.GuardIds).ToArray(), enemyArea = guard.IsEnemyArea, locked = guard.IsLock },
                breeding = breeding == null ? null : new { npcIds = breedingNpcs == null ? new int[0] : new List<Npc>(breedingNpcs).ConvertAll(delegate(Npc npc) { return npc == null ? 0 : npc.ID; }).ToArray(), npcCount = breeding.GetNpcCount(), plants = breedingPlants == null ? new string[0] : new List<string>(breedingPlants).ToArray() }
            };
        }

        private static object AreaState(JObject args)
        {
            AreaMgr manager = AreaMgr.Instance;
            if (manager == null || manager.m_lisAreas == null) throw new RpcException("GAME_NOT_READY", "AreaMgr is unavailable");
            int? requested = args.Value<int?>("id");
            string type = ((string)args["type"] ?? "").Trim();
            List<object> items = new List<object>();
            foreach (AreaBase area in manager.m_lisAreas) {
                if (area == null || (requested.HasValue && area.ID != requested.Value)) continue;
                if (type.Length > 0 && area.GetType().Name.IndexOf(type, StringComparison.OrdinalIgnoreCase) < 0 && (area.def == null || (area.def.Name ?? "").IndexOf(type, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                items.Add(AreaSummary(area));
            }
            return new { items = items, count = items.Count, total = manager.m_lisAreas.Count };
        }

        private static object AreaCatalogState()
        {
            AreaMgr manager = AreaMgr.Instance;
            Dictionary<string, AreaTypeDef> definitions = PrivateStaticField<Dictionary<string, AreaTypeDef>>(typeof(AreaMgr), "m_mapAreaTypes");
            if (manager == null || definitions == null) throw new RpcException("GAME_NOT_READY", "Area catalog is unavailable");
            List<object> items = new List<object>();
            foreach (KeyValuePair<string, AreaTypeDef> pair in definitions) items.Add(new { key = pair.Key, name = pair.Value == null ? null : pair.Value.Name, className = pair.Value == null ? null : pair.Value.Class });
            return new { items = items, count = items.Count };
        }

        private static object CreateArea(JObject args)
        {
            string type = ((string)args["type"] ?? "").Trim();
            if (type.Length == 0) throw new RpcException("INVALID_ARGUMENT", "type is required; use state.areaCatalog");
            Map map = RequireMap();
            int key = ResolveGridKey(args, map);
            AreaMgr manager = AreaMgr.Instance;
            if (manager == null || manager.GetDef(type) == null) throw new RpcException("AREA_TYPE_NOT_FOUND", "Unknown area type " + type);
            AreaBase area = manager.CreateArea(type, key, map);
            if (area == null) throw new RpcException("AREA_REJECTED", "The game rejected area creation");
            return new { commandAccepted = true, area = AreaSummary(area) };
        }

        private static object AddAreaGrid(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            AreaBase area = RequireArea(id.Value);
            int key = ResolveGridKey(args, RequireMap());
            if (!area.GridInArea(key)) area.AddGrid(key);
            return new { commandAccepted = true, key = key, area = AreaSummary(area) };
        }

        private static object RemoveAreaGrid(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            AreaBase area = RequireArea(id.Value);
            int key = ResolveGridKey(args, RequireMap());
            if (area.GridInArea(key)) area.RemoveGrid(key);
            return new { commandAccepted = true, key = key, area = AreaSummary(area) };
        }

        private static object ConfigurePlantArea(JObject args)
        {
            int? id = args.Value<int?>("id");
            string crop = ((string)args["crop"] ?? "").Trim();
            AreaPlant area = id.HasValue ? RequireArea(id.Value) as AreaPlant : null;
            if (area == null || crop.Length == 0) throw new RpcException("INVALID_ARGUMENT", "a plant-area id and crop are required");
            ThingDef def = ThingMgr.Instance.GetDef(g_emThingType.Plant, crop, true);
            if (def == null) throw new RpcException("PLANT_NOT_FOUND", "No plant definition exists with name " + crop);
            area.PlantName = crop;
            if (args.Value<bool?>("discipleOnly").HasValue) area.IsDisciple = args.Value<bool>("discipleOnly");
            return new { commandAccepted = true, area = AreaSummary(area) };
        }

        private static object ConfigureStorageArea(JObject args)
        {
            int? id = args.Value<int?>("id");
            AreaStorage area = id.HasValue ? RequireArea(id.Value) as AreaStorage : null;
            if (area == null) throw new RpcException("INVALID_ARGUMENT", "a storage-area id is required");
            if (args.Value<int?>("priority").HasValue) area.Priority = args.Value<int>("priority");
            if (args.Value<bool?>("canSell").HasValue) area.CanSale = args.Value<bool>("canSell");
            if (args.Value<bool?>("onlyFengShuiItems").HasValue) area.onlyFSItem = args.Value<bool>("onlyFengShuiItems");
            if (args.Value<bool?>("onlyBigFish").HasValue) area.onlyBigFish = args.Value<bool>("onlyBigFish");
            float minRate = args.Value<float?>("minItemRate") ?? area.IncludeItemRate.x;
            float maxRate = args.Value<float?>("maxItemRate") ?? area.IncludeItemRate.y;
            area.IncludeItemRate = new Vector2(minRate, maxRate);
            area.UpdateStorageCharValue();
            return new { commandAccepted = true, area = AreaSummary(area) };
        }

        private static object ConfigureGuardArea(JObject args)
        {
            int? id = args.Value<int?>("id");
            AreaGuard area = id.HasValue ? RequireArea(id.Value) as AreaGuard : null;
            if (area == null) throw new RpcException("INVALID_ARGUMENT", "a guard-area id is required");
            JArray add = args["addNpcIds"] as JArray;
            JArray remove = args["removeNpcIds"] as JArray;
            if (args.Value<bool?>("enemyArea").HasValue) area.IsEnemyArea = args.Value<bool>("enemyArea");
            if (args.Value<bool?>("locked").HasValue) area.IsLock = args.Value<bool>("locked");
            if (remove != null) foreach (JToken token in remove) area.RemoveGuard(RequireNpc(token.Value<int>()));
            if (add != null) foreach (JToken token in add) {
                Npc npc = RequireNpc(token.Value<int>());
                if (!npc.IsPlayerThing || npc.IsDeath) throw new RpcException("GUARD_INVALID", "Guards must be living player NPCs");
                if (npc.GuardAreaId != 0 && npc.GuardAreaId != area.ID) {
                    AreaGuard previous = RequireArea(npc.GuardAreaId) as AreaGuard;
                    if (previous != null) previous.RemoveGuard(npc);
                }
                area.AddGuard(npc);
            }
            return new { commandAccepted = true, area = AreaSummary(area) };
        }

        private static object RemoveArea(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "id and confirm=true are required");
            AreaMgr manager = AreaMgr.Instance;
            AreaBase area = RequireArea(id.Value);
            object before = AreaSummary(area);
            manager.RemoveArea(area);
            Dictionary<int, AreaBase> areas = PrivateField<Dictionary<int, AreaBase>>(manager, "m_Id2Areas");
            return new { commandAccepted = true, previous = before, removed = areas == null || !areas.ContainsKey(id.Value) };
        }

        private static object TradeItemSummary(TradeItem item, SchoolTradeData.Data data)
        {
            if (item == null) return null;
            return new { key = item.Key, itemName = item.ItemName, displayName = SafeString(delegate { return item.GetDisplayName(); }), count = data == null ? (float?)null : data.GetCount(item), rate = SafeValue(delegate { return PrivateValue(item, "_ItemRate"); }), type = item.GetTradeType(), tooltip = SafeString(delegate { return item.GetToolTips(); }) };
        }

        private static object TradeState(JObject args)
        {
            TradeMgr manager = TradeMgr.Instance;
            if (manager == null) throw new RpcException("GAME_NOT_READY", "TradeMgr is unavailable");
            int? requested = args.Value<int?>("schoolId");
            List<object> schools = new List<object>();
            Dictionary<int, SchoolTradeDef> definitions = PrivateField<Dictionary<int, SchoolTradeDef>>(manager, "m_dicSchoolTradeDef");
            if (definitions == null) throw new RpcException("GAME_NOT_READY", "School trade definitions are unavailable");
            foreach (KeyValuePair<int, SchoolTradeDef> pair in definitions) {
                if (requested.HasValue && pair.Key != requested.Value) continue;
                SchoolTradeData.Data data = manager.GetSchoolTrade(pair.Key);
                List<object> items = new List<object>();
                if (data != null && data.GetTradeItems() != null) foreach (TradeItem item in data.GetTradeItems()) items.Add(TradeItemSummary(item, data));
                SchoolTendency tendency = manager.GetTendency(pair.Key);
                schools.Add(new {
                    schoolId = pair.Key, relation = SchoolMgr.Instance == null ? 0 : SchoolMgr.Instance.GetSchoolRelation(pair.Key),
                    relationRank = SchoolMgr.Instance == null ? null : SchoolMgr.Instance.GetSchoolRelationRank(pair.Key).ToString(),
                    friendPoints = data == null ? 0 : data.FriendPoint, usableFriendPoints = data == null ? 0 : data.CanUseFriendPoint,
                    borderOpen = data != null && data.OpenBorder != 0, goods = items, goodsCount = items.Count,
                    tendency = tendency == null ? null : tendency.Tendency
                });
            }
            PlayerTradeItemInfo player = manager.GetPlayerTradeInfo();
            return new { schools = schools, schoolCount = schools.Count, playerTradeAvailable = player != null, priceScale = manager.Price_Global_Scale, friendPointScale = manager.FriendPoint_Global_Scale };
        }

        private static object OpenSchoolTrade(JObject args)
        {
            int? schoolId = args.Value<int?>("schoolId");
            if (!schoolId.HasValue) throw new RpcException("INVALID_ARGUMENT", "schoolId is required");
            if (TradeMgr.Instance == null || !TradeMgr.Instance.IsSchoolValid(schoolId.Value)) throw new RpcException("SCHOOL_NOT_FOUND", "School is not available for trade");
            Wnd_SchoolTrade.Instance.ShowSchool(schoolId.Value);
            return new { commandAccepted = true, completion = "dispatched", schoolId = schoolId.Value };
        }

        private static object TradePriceSummary(TradePrice price)
        {
            return price == null ? null : new { value = price.Value, friendPoints = price.FriendPoint };
        }

        private static object TradeWindowState()
        {
            Wnd_SchoolTrade window = Wnd_SchoolTrade.Instance;
            if (window == null || !window.isShowing) return new { available = false };
            TradePrice left = SafeValue(delegate { return InvokePrivateMethod(window, "GetLeftSelectValue", new object[0]); }) as TradePrice;
            TradePrice right = SafeValue(delegate { return InvokePrivateMethod(window, "GetRightSelectValue", new object[0]); }) as TradePrice;
            return new {
                available = true,
                leftEmpty = SafeValue(delegate { return InvokePrivateMethod(window, "IsSelectEmpty", new object[] { true }); }),
                rightEmpty = SafeValue(delegate { return InvokePrivateMethod(window, "IsSelectEmpty", new object[] { false }); }),
                offeredByPlayer = TradePriceSummary(right),
                requestedFromTrader = TradePriceSummary(left),
                noFriendPointsRequired = SafeValue(delegate { return InvokePrivateMethod(window, "get_NoNeedFp", new object[0]); })
            };
        }

        private static object ConfirmTrade(JObject args)
        {
            if (!(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "confirm=true is required because this finalizes a trade");
            Wnd_SchoolTrade window = Wnd_SchoolTrade.Instance;
            if (window == null || !window.isShowing) throw new RpcException("TRADE_WINDOW_NOT_OPEN", "Open and configure the school trade window first");
            object before = TradeWindowState();
            bool leftEmpty = Convert.ToBoolean(InvokePrivateMethod(window, "IsSelectEmpty", new object[] { true }));
            bool rightEmpty = Convert.ToBoolean(InvokePrivateMethod(window, "IsSelectEmpty", new object[] { false }));
            if (leftEmpty && rightEmpty) throw new RpcException("TRADE_EMPTY", "No trade items are selected");
            InvokePrivateMethod(window, "__selectyes", new object[] { null });
            return new { commandAccepted = true, completion = "dispatched-and-read-back", before = before, after = TradeWindowState() };
        }

        private static object OpenWorldMap()
        {
            if (Wnd_World.Instance == null) throw new RpcException("UI_NOT_READY", "World-map window is unavailable");
            Wnd_World.Instance.Show();
            return new { commandAccepted = true, completion = "dispatched" };
        }

        private static object WorldMapState(JObject args)
        {
            PlacesMgr manager = PlacesMgr.Instance;
            if (manager == null) throw new RpcException("GAME_NOT_READY", "PlacesMgr is unavailable");
            string query = ((string)args["query"] ?? "").Trim();
            List<object> places = new List<object>();
            foreach (KeyValuePair<string, PlacesMgr.PlaceData> pair in manager.Places) {
                PlaceDef def = manager.GetPlaceDef(pair.Key);
                PlacesMgr.PlaceData data = pair.Value;
                string searchable = pair.Key + " " + (def == null ? "" : def.DisplayName + " " + def.Desc);
                if (query.Length > 0 && searchable.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
                places.Add(new { name = pair.Key, displayName = def == null ? null : def.DisplayName, description = def == null ? null : def.Desc, level = def == null ? 0 : def.Level, schoolId = data == null ? 0 : data.School, unlocked = data != null && data.UnLock, arrived = data != null && data.Arrived, arrivedCount = data == null ? 0 : data.ArrivedCount, forecast = data == null ? null : data.Forecast.ToString(), distance = SafeValue(delegate { return (object)manager.GetDistance(pair.Key); }), lingCost = SafeValue(delegate { return (object)manager.GetLingCost(pair.Key); }), hasFight = def != null && def.HasFight });
            }
            List<object> explorations = new List<object>();
            if (manager.MapExplors != null) foreach (PlacesMgr.MapExploreData data in manager.MapExplors) if (data != null) explorations.Add(new { npcId = data.NpcID, place = data.Place, stage = data.Stage, progress = data.StageP, requiredTime = data.NeedTime, staying = data.IsStay, punishmentTimeLeft = data.PunishTimeLeft, fightMapTimeLeft = data.FightMapLeftTime });
            return new { places = places, placeCount = places.Count, explorations = explorations, explorationCount = explorations.Count, query = query };
        }

        private static OutspreadMgr RequireOutspread()
        {
            OutspreadMgr manager = OutspreadMgr.Instance;
            if (manager == null) throw new RpcException("OUTSPREAD_UNAVAILABLE", "Outspread manager is unavailable in the current game state");
            return manager;
        }

        private static object DlcState()
        {
            List<object> items = new List<object>();
            foreach (DLC dlc in Enum.GetValues(typeof(DLC))) {
                if (dlc == DLC._COUNT) continue;
                bool installed = DLControl.IsDlcInstalled(dlc);
                items.Add(new { id = (int)dlc, name = dlc.ToString(), displayName = SafeString(delegate { return DLControl.GetDLCName(dlc); }), luaFlag = SafeString(delegate { return DLControl.GetDlcLuaFlag(dlc); }), installed = installed });
            }
            return new { items = items, count = items.Count, installedPaths = DLControl.GetAllInstallDlcPath() };
        }

        private static OutspreadMgr.Region RequireOutspreadRegion(string name)
        {
            OutspreadMgr.Region region = RequireOutspread().GetRegion(name);
            if (region == null) throw new RpcException("OUTSPREAD_REGION_NOT_FOUND", "Opened outspread region was not found: " + name);
            return region;
        }

        private static bool OutspreadBuildUnlocked(OutspreadMgr manager, OutspreadMgr.Region region, OutspreadBuildDef def)
        {
            return Convert.ToBoolean(InvokePrivateMethod(manager, "IsBuildUnlock", new object[] { region, def }));
        }

        private static bool OutspreadPolicyUnlocked(OutspreadMgr manager, OutspreadMgr.Region region, OutspreadPolicyDef def)
        {
            return Convert.ToBoolean(InvokePrivateMethod(manager, "IsPolicyUnlock", new object[] { region, def }));
        }

        private static bool OutspreadPolicyCondition(OutspreadMgr manager, OutspreadMgr.Region region, OutspreadPolicyDef def)
        {
            return Convert.ToBoolean(InvokePrivateMethod(manager, "CheckPolicyCondition", new object[] { region, def }));
        }

        private static object OutspreadCostSummary(OutspreadCost cost)
        {
            if (cost == null) return null;
            List<object> materials = new List<object>();
            if (cost.Mats != null) foreach (OutspreadBuildMaterial material in cost.Mats) if (material != null)
                materials.Add(new { item = material.ItemName, count = material.Count, realCount = material.RealCostCount });
            return new { materials = materials, faith = cost.Faith, stability = cost.Stability, population = cost.Population, influence = cost.Influence };
        }

        private static object OutspreadRegionSummary(OutspreadMgr manager, OutspreadMgr.Region region)
        {
            List<object> builds = new List<object>();
            if (region.Builds != null) foreach (KeyValuePair<int, Dictionary<int, string>> typePair in region.Builds)
                if (typePair.Value != null) foreach (KeyValuePair<int, string> pair in typePair.Value) {
                    OutspreadBuildDef def = manager.GetBuildDef(pair.Value);
                    builds.Add(new { type = ((OutsBuildType)typePair.Key).ToString(), index = pair.Key, name = pair.Value, displayName = def == null ? null : def.DisplayName });
                }
            List<object> waitingBuilds = new List<object>();
            if (region.WaitingBuilds != null) foreach (KeyValuePair<int, Dictionary<int, OutspreadMgr.Region.WaitingBuild>> typePair in region.WaitingBuilds)
                if (typePair.Value != null) foreach (KeyValuePair<int, OutspreadMgr.Region.WaitingBuild> pair in typePair.Value) {
                    OutspreadMgr.Region.WaitingBuild waiting = pair.Value;
                    if (waiting != null) waitingBuilds.Add(new { type = ((OutsBuildType)typePair.Key).ToString(), index = pair.Key, name = waiting.Build, finishTime = waiting.FinishTime, duration = waiting.Duration });
                }
            List<object> members = new List<object>();
            OutspreadMgr.Region.SchoolUnion union = region.UnionData;
            if (union != null && union.members != null) foreach (OutspreadMgr.Region.Member member in union.members) if (member != null)
                members.Add(new { id = member.memberid, name = member.name, race = member.Race, sex = member.sex.ToString(), age = member.age, fight = member.Fight, social = member.Social, mining = member.Mining, farming = member.Farming });
            List<object> religions = new List<object>();
            if (region.SchoolReligion != null) foreach (KeyValuePair<int, OutspreadMgr.Region.Religion> pair in region.SchoolReligion)
                if (pair.Value != null) religions.Add(new { schoolId = pair.Key, attractive = pair.Value.Attractive, population = pair.Value.Population });
            object policy = region.RegionPolicy == null ? null : new { name = region.RegionPolicy.Policy, previous = region.RegionPolicy.prevPolicy, story = region.RegionPolicy.PolicyStory, storyFinishTime = region.RegionPolicy.StoryFinishTime, solveWay = region.RegionPolicy.Way.ToString(), description = region.RegionPolicy.Desc };
            object disaster = region.Disaster == null ? null : new { name = region.Disaster.Disaster, type = region.Disaster.T, startTime = region.Disaster.DisasterStartTime, endTime = region.Disaster.DisasterEndTime, aidNeed = region.Disaster.AidNeed, aidReceived = region.Disaster.AidReceive, storyTriggerTime = region.Disaster.StoryTriggerTime };
            object supervision = region.SupData == null ? null : new { active = region.IsSupervise, nextStoryTime = region.SupData.NextStoryTime, stopTime = region.SupData.StopTime, canStart = manager.CanSupervise(region) };
            return new {
                name = region.RegionName, displayName = region.DisplayName, level = region.Level,
                faith = region.Faith, maxFaith = region.MaxFaith, faithCollectLevel = region.FaithCollectLevel,
                stability = region.Stability, stabilityLevel = region.GetStabilityLevel().ToString(), attractive = region.Attractive, population = region.Population,
                selfReligionPopulation = region.SelfReligionPopulation, unbelieverCount = region.UnbelieverCount,
                storage = region.ProductStorage, storageUsed = region.ProductStorage == null ? 0 : region.ProductStorage.Count, storageCapacity = region.CanStorageSize,
                chiefNpcId = union == null ? 0 : union.PMId, waitingChiefNpcId = union == null ? 0 : union.WaitPMId,
                maxMembers = union == null ? 0 : union.MaxMember, comingMembers = manager.GetComingMemberCount(region.RegionName), members = members,
                buildings = builds, waitingBuildings = waitingBuilds, religions = religions, policy = policy, disaster = disaster, supervision = supervision,
                specialFocus = region.SpecialFocus, faithNpcId = region.FaithNpcId
            };
        }

        private static object OutspreadState(JObject args)
        {
            OutspreadMgr manager = RequireOutspread();
            string requested = ((string)args["name"] ?? "").Trim();
            List<object> regions = new List<object>();
            Dictionary<string, OutspreadMgr.Region> regionMap = PrivateField<Dictionary<string, OutspreadMgr.Region>>(manager, "<Regions>k__BackingField");
            if (regionMap != null) foreach (KeyValuePair<string, OutspreadMgr.Region> pair in regionMap) {
                if (pair.Value == null || (requested.Length > 0 && !string.Equals(pair.Key, requested, StringComparison.OrdinalIgnoreCase))) continue;
                regions.Add(OutspreadRegionSummary(manager, pair.Value));
            }
            List<object> moving = new List<object>();
            if (manager.Moving != null) foreach (OutspreadMgr.MovingMember member in manager.Moving) if (member != null)
                moving.Add(new { npcId = member.NpcId, from = member.From, to = member.To, duration = member.Dur, needed = member.Need, percent = member.Percent });
            return new { influence = manager.Influence, specialFocus = PrivateValue(manager, "<SpecialFocus>k__BackingField"), openRegionCount = manager.GetOpenRegionCount(), regions = regions, count = regions.Count, moving = moving, globalStorage = PrivateField<Dictionary<string, int>>(manager, "GlobalStorage") };
        }

        private static object OutspreadCatalogState(JObject args)
        {
            OutspreadMgr manager = RequireOutspread();
            string regionName = ((string)args["region"] ?? "").Trim();
            OutspreadMgr.Region region = regionName.Length == 0 ? null : manager.GetRegion(regionName);
            Dictionary<string, OutspreadBuildDef> buildDefs = PrivateField<Dictionary<string, OutspreadBuildDef>>(manager, "m_BuildDef");
            Dictionary<string, OutspreadPolicyDef> policyDefs = PrivateField<Dictionary<string, OutspreadPolicyDef>>(manager, "m_PolicyDef");
            Dictionary<string, OutspreadBuildSkillDef> skillDefs = PrivateField<Dictionary<string, OutspreadBuildSkillDef>>(manager, "m_BuildSkillDef");
            List<object> regions = new List<object>();
            foreach (string name in manager.GetAllRegionName()) {
                OutspreadRegionDef def = manager.GetRegionDef(name);
                if (def != null) regions.Add(new { name = name, displayName = def.DisplayName, description = def.Desc, type = def.Type, population = def.Population, stability = def.Stability, clickPlace = def.ClickPlace, opened = manager.GetRegion(name) != null, openCost = OutspreadCostSummary(manager.GetOpenUpCost(name)) });
            }
            List<object> builds = new List<object>();
            if (buildDefs != null) foreach (KeyValuePair<string, OutspreadBuildDef> pair in buildDefs) {
                OutspreadBuildDef def = pair.Value; if (def == null) continue;
                builds.Add(new { name = pair.Key, displayName = def.DisplayName, description = def.Desc, effect = def.Effect, type = def.BuildType, buildDays = def.BuildDay, maxMembers = def.MaxMemberCount, maxStorage = def.MaxStorageSize, upgrade = def.Upgrade, immortal = def.Immortal != 0, cost = OutspreadCostSummary(def.Cost), unlocked = region == null ? (bool?)null : OutspreadBuildUnlocked(manager, region, def) });
            }
            List<object> policies = new List<object>();
            if (policyDefs != null) foreach (KeyValuePair<string, OutspreadPolicyDef> pair in policyDefs) {
                OutspreadPolicyDef def = pair.Value; if (def == null) continue;
                policies.Add(new { name = pair.Key, displayName = def.DisplayName, description = def.Desc, product = def.Product, basePolicy = def.Base != 0, dailyCost = OutspreadCostSummary(def.CostPerDay), unlocked = region == null ? (bool?)null : OutspreadPolicyUnlocked(manager, region, def), conditionMet = region == null ? (bool?)null : OutspreadPolicyCondition(manager, region, def) });
            }
            List<object> skills = new List<object>();
            if (skillDefs != null) foreach (KeyValuePair<string, OutspreadBuildSkillDef> pair in skillDefs) {
                OutspreadBuildSkillDef def = pair.Value; if (def != null) skills.Add(new { name = pair.Key, displayName = def.DisplayName, description = def.Desc, cooldown = def.CD, cost = OutspreadCostSummary(def.Cost) });
            }
            return new { region = regionName.Length == 0 ? null : regionName, regions = regions, builds = builds, policies = policies, buildSkills = skills };
        }

        private static object OpenOutspreadRegion(JObject args)
        {
            string regionName = ((string)args["region"] ?? "").Trim(); int? npcId = args.Value<int?>("npcId"); int resourceLevel = args.Value<int?>("resourceLevel") ?? 0;
            if (regionName.Length == 0 || !npcId.HasValue || resourceLevel < 0 || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "region, npcId, non-negative resourceLevel and confirm=true are required");
            OutspreadMgr manager = RequireOutspread();
            if (manager.GetRegionDef(regionName) == null) throw new RpcException("OUTSPREAD_REGION_UNKNOWN", "Region definition was not found");
            if (manager.GetRegion(regionName) != null) throw new RpcException("OUTSPREAD_REGION_ALREADY_OPEN", "Region is already opened");
            Npc npc = RequireNpc(npcId.Value);
            if (npc.OutsType != 0 || !npc.CanDoAction) throw new RpcException("NPC_NOT_AVAILABLE", "NPC is already assigned outside or cannot act");
            manager.SetNpcToOpenRegion(npc, regionName, resourceLevel);
            return new { commandAccepted = true, region = regionName, npcId = npc.ID, resourceLevel = resourceLevel, openCost = OutspreadCostSummary(manager.GetOpenUpCost(regionName)), completion = "job-dispatched" };
        }

        private static object SetOutspreadPolicy(JObject args)
        {
            string regionName = ((string)args["region"] ?? "").Trim(); string name = ((string)args["name"] ?? "").Trim();
            if (regionName.Length == 0 || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "region and confirm=true are required");
            OutspreadMgr manager = RequireOutspread(); OutspreadMgr.Region region = RequireOutspreadRegion(regionName);
            if (name == "None") name = "";
            if (name.Length > 0) {
                OutspreadPolicyDef def = manager.GetPolicyDef(name);
                if (def == null) throw new RpcException("OUTSPREAD_POLICY_NOT_FOUND", "Policy definition was not found");
                if (!OutspreadPolicyUnlocked(manager, region, def) || !OutspreadPolicyCondition(manager, region, def)) throw new RpcException("OUTSPREAD_POLICY_BLOCKED", "Policy is locked or its native condition is not met");
            }
            manager.SetPolicy(region, name.Length == 0 ? null : name, "AmazingCultivationMCP");
            return new { commandAccepted = true, region = regionName, policy = region.Policy, state = OutspreadRegionSummary(manager, region) };
        }

        private static object SetOutspreadBuild(JObject args)
        {
            string regionName = ((string)args["region"] ?? "").Trim(); string name = ((string)args["name"] ?? "").Trim(); int? index = args.Value<int?>("index");
            if (regionName.Length == 0 || !index.HasValue || index.Value < 0 || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "region, non-negative index, type, name (empty removes) and confirm=true are required");
            OutspreadMgr manager = RequireOutspread(); OutspreadMgr.Region region = RequireOutspreadRegion(regionName); OutsBuildType type = ParseEnum<OutsBuildType>(args, "type");
            if (name.Length > 0) { OutspreadBuildDef def = manager.GetBuildDef(name); if (def == null) throw new RpcException("OUTSPREAD_BUILD_NOT_FOUND", "Build definition was not found"); if (!OutspreadBuildUnlocked(manager, region, def)) throw new RpcException("OUTSPREAD_BUILD_LOCKED", "Build is not unlocked in this region"); }
            bool accepted = manager.SetBuild(region, index.Value, type, name.Length == 0 ? null : name);
            if (!accepted) throw new RpcException("OUTSPREAD_BUILD_REJECTED", "Native SetBuild rejected the requested slot change");
            return new { commandAccepted = true, region = regionName, index = index.Value, type = type.ToString(), name = name, state = OutspreadRegionSummary(manager, region) };
        }

        private static object CancelOutspreadBuild(JObject args)
        {
            string regionName = ((string)args["region"] ?? "").Trim(); int? index = args.Value<int?>("index");
            if (regionName.Length == 0 || !index.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "region, index, type and confirm=true are required");
            OutspreadMgr manager = RequireOutspread(); OutspreadMgr.Region region = RequireOutspreadRegion(regionName); OutsBuildType type = ParseEnum<OutsBuildType>(args, "type");
            if (region.GetWaitingBuild(index.Value, type) == null) throw new RpcException("OUTSPREAD_WAITING_BUILD_NOT_FOUND", "No waiting build exists in that slot");
            manager.CancelWaitBuild(region, index.Value, type);
            return new { commandAccepted = true, region = regionName, index = index.Value, type = type.ToString(), state = OutspreadRegionSummary(manager, region) };
        }

        private static object SetOutspreadChief(JObject args)
        {
            string regionName = ((string)args["region"] ?? "").Trim(); int? npcId = args.Value<int?>("npcId");
            if (regionName.Length == 0 || !npcId.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "region, npcId and confirm=true are required");
            OutspreadMgr manager = RequireOutspread(); OutspreadMgr.Region region = RequireOutspreadRegion(regionName); Npc npc = RequireNpc(npcId.Value);
            if (npc.OutsType != 0 || !npc.CanDoAction) throw new RpcException("NPC_NOT_AVAILABLE", "NPC is already assigned outside or cannot act");
            manager.ChangeRegionPM(npc, regionName);
            return new { commandAccepted = true, region = regionName, npcId = npc.ID, waitingChiefNpcId = region.UnionData == null ? 0 : region.UnionData.WaitPMId, completion = "job-dispatched" };
        }

        private static object SendOutspreadMember(JObject args)
        {
            string regionName = ((string)args["region"] ?? "").Trim(); int? npcId = args.Value<int?>("npcId");
            if (regionName.Length == 0 || !npcId.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "region, npcId and confirm=true are required");
            OutspreadMgr manager = RequireOutspread(); OutspreadMgr.Region region = RequireOutspreadRegion(regionName); Npc npc = RequireNpc(npcId.Value);
            if (npc.OutsType != 0 || !npc.CanDoAction) throw new RpcException("NPC_NOT_AVAILABLE", "NPC is already assigned outside or cannot act");
            if (region.UnionData == null || !region.UnionData.CanAddMember()) throw new RpcException("OUTSPREAD_MEMBER_LIMIT", "Region cannot accept another member");
            manager.SendMember(npc, regionName);
            return new { commandAccepted = true, region = regionName, npcId = npc.ID, completion = "job-dispatched" };
        }

        private static object RemoveOutspreadMember(JObject args)
        {
            string regionName = ((string)args["region"] ?? "").Trim(); int? memberId = args.Value<int?>("memberId");
            if (regionName.Length == 0 || !memberId.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "region, memberId and confirm=true are required");
            OutspreadMgr manager = RequireOutspread(); OutspreadMgr.Region region = RequireOutspreadRegion(regionName);
            OutspreadMgr.Region.Member member = region.UnionData == null || region.UnionData.members == null ? null : region.UnionData.members.Find(delegate(OutspreadMgr.Region.Member value) { return value != null && value.memberid == memberId.Value; });
            if (member == null) throw new RpcException("OUTSPREAD_MEMBER_NOT_FOUND", "Region member was not found");
            manager.RemoveMember(region, member);
            return new { commandAccepted = true, region = regionName, memberId = memberId.Value, state = OutspreadRegionSummary(manager, region) };
        }

        private static object StartOutspreadSupervision(JObject args)
        {
            string regionName = ((string)args["region"] ?? "").Trim();
            if (regionName.Length == 0 || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "region and confirm=true are required");
            OutspreadMgr manager = RequireOutspread(); OutspreadMgr.Region region = RequireOutspreadRegion(regionName);
            if (!manager.CanSupervise(region)) throw new RpcException("OUTSPREAD_SUPERVISION_BLOCKED", "Native supervision requirements are not met");
            manager.StartSupervise(region);
            return new { commandAccepted = true, region = regionName, state = OutspreadRegionSummary(manager, region) };
        }

        private static object CollectOutspreadProducts(JObject args)
        {
            string regionName = ((string)args["region"] ?? "").Trim();
            if (regionName.Length == 0 || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "region and confirm=true are required");
            OutspreadMgr manager = RequireOutspread(); OutspreadMgr.Region region = RequireOutspreadRegion(regionName);
            Dictionary<string, int> before = region.ProductStorage == null ? new Dictionary<string, int>() : new Dictionary<string, int>(region.ProductStorage);
            manager.TakeProductFromStorage(region);
            return new { commandAccepted = true, region = regionName, before = before, after = region.ProductStorage, state = OutspreadRegionSummary(manager, region) };
        }

        private static Wnd_StorySelect CurrentStoryChoice()
        {
            Wnd_StorySelect window = PrivateStaticField<Wnd_StorySelect>(typeof(Wnd_StorySelect), "Newest");
            return window != null && window.isShowing ? window : null;
        }

        private static object StoryChoiceState()
        {
            Wnd_StorySelect window = CurrentStoryChoice();
            if (window == null) return new { available = false };
            List<string> choices = PrivateField<List<string>>(window, "selectlist") ?? new List<string>();
            List<string> tips = PrivateField<List<string>>(window, "tips") ?? new List<string>();
            Npc npc = PrivateField<Npc>(window, "fromnpc");
            List<object> items = new List<object>();
            for (int i = 0; i < choices.Count; i++) items.Add(new { index = i, text = choices[i], tip = i < tips.Count ? tips[i] : null });
            return new {
                available = true, title = Convert.ToString(PrivateValue(window, "title")), description = Convert.ToString(PrivateValue(window, "desc")),
                targetKey = SafeValue(delegate { return PrivateValue(window, "targetkey"); }), paused = SafeValue(delegate { return PrivateValue(window, "pause"); }),
                npcId = npc == null ? (int?)null : npc.ID, npcName = npc == null ? null : SafeString(delegate { return npc.GetName(); }),
                choices = items, choiceCount = items.Count, alreadyClicked = SafeValue(delegate { return PrivateValue(window, "clicked"); })
            };
        }

        private static object ChooseStoryOption(JObject args)
        {
            int? index = args.Value<int?>("index");
            if (!index.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "index and confirm=true are required because story choices are consequential");
            Wnd_StorySelect window = CurrentStoryChoice();
            if (window == null) throw new RpcException("STORY_CHOICE_NOT_OPEN", "No story choice is currently open");
            List<string> choices = PrivateField<List<string>>(window, "selectlist") ?? new List<string>();
            if (index.Value < 0 || index.Value >= choices.Count) throw new RpcException("INVALID_ARGUMENT", "index is outside the current story choice list");
            object before = StoryChoiceState();
            InvokePrivateMethod(window, "ClickIndex", new object[] { index.Value });
            return new { commandAccepted = true, completion = "dispatched-and-read-back", selectedIndex = index.Value, selectedText = choices[index.Value], before = before, after = StoryChoiceState() };
        }

        private static object StartExplore(JObject args)
        {
            int? id = args.Value<int?>("id");
            string place = ((string)args["place"] ?? "").Trim();
            if (!id.HasValue || place.Length == 0 || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "id, place, and confirm=true are required");
            PlacesMgr places = PlacesMgr.Instance;
            Npc npc = RequireNpc(id.Value);
            if (places == null || places.GetPlaceDef(place) == null || places.IsLocked(place)) throw new RpcException("PLACE_UNAVAILABLE", "Place was not found or is locked");
            if (places.IsInExplore(id.Value)) throw new RpcException("ALREADY_EXPLORING", "NPC is already exploring");
            bool stay = args.Value<bool?>("stay") ?? false;
            Command command = npc.AddCommand("GoMapExplore", new object[] { place, stay });
            if (command == null) throw new RpcException("COMMAND_REJECTED", "The game rejected the exploration command");
            return new { commandAccepted = true, id = id.Value, place = place, stay = stay, expectedLingCost = places.GetLingCost(place), expectedTravelTime = places.GetCost(npc, place), command = CommandSummary(command) };
        }

        private static object RecallExplorer(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "id and confirm=true are required");
            Npc npc = RequireNpc(id.Value);
            PlacesMgr manager = PlacesMgr.Instance;
            bool accepted = manager != null && manager.CallBackNpc(npc);
            return new { commandAccepted = accepted, id = id.Value, stillExploring = manager != null && manager.IsInExplore(id.Value) };
        }

        private static object ChangeGong(JObject args)
        {
            int? id = args.Value<int?>("id");
            string gong = ((string)args["gong"] ?? "").Trim();
            if (!id.HasValue || gong.Length == 0 || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "id, gong, and confirm=true are required because changing cultivation art is consequential");
            Npc npc = RequireNpc(id.Value);
            NpcPractice practice = RequirePractice(id.Value);
            string before = practice.Gong == null ? null : practice.Gong.Name;
            practice.ChangeGong(gong);
            return new { commandAccepted = true, id = id.Value, before = before, cultivation = NpcCultivation(npc) };
        }

        private static object PromoteInnerDisciple(JObject args)
        {
            int? id = args.Value<int?>("id");
            string gong = ((string)args["gong"] ?? "").Trim();
            if (!id.HasValue || gong.Length == 0 || !(args.Value<bool?>("confirm") ?? false))
                throw new RpcException("CONFIRMATION_REQUIRED", "id, gong, and confirm=true are required because promotion is consequential");
            Npc npc = RequireNpc(id.Value);
            NpcPractice practice = RequirePractice(id.Value);
            if (!practice.IsZhuJi) throw new RpcException("FOUNDATION_NOT_READY", "The NPC has not completed foundation building");
            int beforeCount = SchoolMgr.Instance == null ? 0 : SchoolMgr.Instance.DiscipleCount;
            practice.Apprentice(null);
            practice.Up2Disciple(gong, args.Value<int?>("difficulty") ?? 0);
            int afterCount = SchoolMgr.Instance == null ? 0 : SchoolMgr.Instance.DiscipleCount;
            return new {
                commandAccepted = npc.IsDisciple && !npc.IsWaiMen,
                id = npc.ID,
                name = SafeString(delegate { return npc.GetName(); }),
                disciple = npc.IsDisciple,
                outerDisciple = npc.IsWaiMen,
                rank = npc.Rank.ToString(),
                schoolDiscipleCountBefore = beforeCount,
                schoolDiscipleCountAfter = afterCount,
                cultivation = NpcCultivation(npc),
                completion = npc.IsDisciple && !npc.IsWaiMen && afterCount >= beforeCount ? "verified" : "dispatched"
            };
        }

        private static object BeginBreakthrough(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "id and confirm=true are required because breakthrough can fail and injure the NPC");
            Npc npc = RequireNpc(id.Value);
            NpcPractice practice = RequirePractice(id.Value);
            if (!practice.CheckNeckCondition()) throw new RpcException("BREAKTHROUGH_NOT_READY", "The game's breakthrough conditions are not satisfied");
            practice.BeginBroken(args.Value<bool?>("heavenlyTribulation") ?? false);
            return new { commandAccepted = true, id = id.Value, cultivation = NpcCultivation(npc) };
        }

        private static object UseItem(JObject args)
        {
            int? npcId = args.Value<int?>("npcId");
            int? itemId = args.Value<int?>("itemId");
            if (!npcId.HasValue || !itemId.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "npcId, itemId and confirm=true are required because using an item may consume it");
            Npc npc = RequireNpc(npcId.Value);
            ItemThing item = RequireThing(itemId.Value) as ItemThing;
            if (item == null) throw new RpcException("ITEM_NOT_FOUND", "The requested thing is not an item");
            if (!item.CheckCanUse(npc)) throw new RpcException("ITEM_NOT_USABLE", "The game does not allow this NPC to use the item");
            object before = ItemSummary(item);
            bool accepted = item.UseItem(npc);
            return new { commandAccepted = accepted, itemBefore = before, itemAfter = FindThing(itemId.Value) == null ? null : ItemSummary(FindThing(itemId.Value) as ItemThing), cultivation = NpcCultivation(npc) };
        }

        private static object EquipItem(JObject args)
        {
            int? npcId = args.Value<int?>("npcId");
            int? itemId = args.Value<int?>("itemId");
            if (!npcId.HasValue || !itemId.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "npcId, itemId and confirm=true are required because equipment can replace an existing item");
            Npc npc = RequireNpc(npcId.Value);
            ItemThing item = RequireThing(itemId.Value) as ItemThing;
            if (item == null || !item.IsPlayerThing) throw new RpcException("ITEM_NOT_AVAILABLE", "A player-owned item is required");
            if (item.InWhoseBag != npc.ID && item.InWhoseHand != npc.ID && item.Key != npc.Key) throw new RpcException("ITEM_NOT_REACHABLE", "Item must be carried by this NPC or at the NPC grid; use the native carry command first");
            g_emEquipType slot = args["slot"] == null ? npc.CheckEquipCell(item, false) : ParseEnum<g_emEquipType>(args, "slot");
            object before = NpcInventory(npc);
            bool accepted = npc.EquipItem(item, slot);
            return new { commandAccepted = accepted, npcId = npc.ID, itemId = itemId.Value, slot = slot.ToString(), before = before, after = NpcInventory(npc) };
        }

        private static object UnequipItem(JObject args)
        {
            int? npcId = args.Value<int?>("npcId");
            if (!npcId.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "npcId, slot and confirm=true are required");
            g_emEquipType slot = ParseEnum<g_emEquipType>(args, "slot");
            Npc npc = RequireNpc(npcId.Value);
            if (npc.Equip == null || npc.Equip.GetEquip(slot) == null) throw new RpcException("EQUIPMENT_NOT_FOUND", "Nothing is equipped in that slot");
            object before = NpcInventory(npc);
            ItemThing removed = npc.Equip.UnEquipItem(slot, args.Value<bool?>("drop") ?? false, npc.Key);
            return new { commandAccepted = removed != null, npcId = npc.ID, slot = slot.ToString(), removed = ItemSummary(removed), before = before, after = NpcInventory(npc) };
        }

        private static object AssignBed(JObject args)
        {
            int? npcId = args.Value<int?>("npcId");
            int? buildingId = args.Value<int?>("buildingId");
            if (!npcId.HasValue) throw new RpcException("INVALID_ARGUMENT", "npcId is required; omit buildingId to clear assignment");
            Npc npc = RequireNpc(npcId.Value);
            BuildingThing building = buildingId.HasValue ? RequireBuilding(buildingId.Value) : null;
            int? before = npc.MyBed == null ? (int?)null : npc.MyBed.ID;
            npc.SetBed(building);
            return new { commandAccepted = true, npcId = npc.ID, before = before, after = npc.MyBed == null ? (int?)null : npc.MyBed.ID };
        }

        private static object AssignPracticePlace(JObject args)
        {
            int? npcId = args.Value<int?>("npcId");
            int? buildingId = args.Value<int?>("buildingId");
            if (!npcId.HasValue) throw new RpcException("INVALID_ARGUMENT", "npcId is required; omit buildingId to clear assignment");
            Npc npc = RequireNpc(npcId.Value);
            BuildingThing building = buildingId.HasValue ? RequireBuilding(buildingId.Value) : null;
            int? before = npc.MyPractice == null ? (int?)null : npc.MyPractice.ID;
            npc.SetPracticePlace(building);
            return new { commandAccepted = true, npcId = npc.ID, before = before, after = npc.MyPractice == null ? (int?)null : npc.MyPractice.ID };
        }

        private static object HuntAnimal(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "animal id and confirm=true are required");
            Npc animal = RequireNpc(id.Value);
            if (animal.IsSmartRace || !animal.CanBeHunting) throw new RpcException("ANIMAL_NOT_HUNTABLE", "The game does not allow hunting this NPC");
            if (CommandMgr.Instance == null || CommandMgr.Instance.GetDef("Hunting") == null) throw new RpcException("COMMAND_NOT_FOUND", "Native Hunting command is unavailable");
            Command command = animal.AddCommand("Hunting", new object[0]);
            if (command == null) throw new RpcException("COMMAND_REJECTED", "The game rejected the hunting command");
            return new { commandAccepted = true, animal = ThingSummary(animal), command = CommandSummary(command) };
        }

        private static object SlaughterAnimal(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "animal id and confirm=true are required");
            Npc animal = RequireNpc(id.Value);
            if (animal.IsSmartRace || !animal.CanBeSlaughter) throw new RpcException("ANIMAL_NOT_SLAUGHTERABLE", "The game does not allow slaughtering this NPC");
            if (CommandMgr.Instance == null || CommandMgr.Instance.GetDef("Slaughter") == null) throw new RpcException("COMMAND_NOT_FOUND", "Native Slaughter command is unavailable");
            Command command = animal.AddCommand("Slaughter", new object[0]);
            if (command == null) throw new RpcException("COMMAND_REJECTED", "The game rejected the slaughter command");
            return new { commandAccepted = true, animal = ThingSummary(animal), command = CommandSummary(command) };
        }

        private static object MoveNpcTo(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "npc id, target grid, and confirm=true are required");
            Npc target = RequireNpc(id.Value);
            if (target.IsDeath || !target.IsPlayerThing) throw new RpcException("NPC_NOT_MOVABLE", "A living player-controlled NPC or animal is required");
            int key = ResolveGridKey(args, RequireMap());
            if (CommandMgr.Instance == null || CommandMgr.Instance.GetDef("MoveNpc") == null) throw new RpcException("COMMAND_NOT_FOUND", "Native MoveNpc command is unavailable");
            Command command = target.AddCommand("MoveNpc", new object[] { key });
            if (command == null) throw new RpcException("COMMAND_REJECTED", "The game rejected the NPC move command");
            return new { commandAccepted = true, target = NpcSummary(target), targetKey = key, command = CommandSummary(command) };
        }

        private static object TalkToNpc(JObject args)
        {
            int? actorId = args.Value<int?>("actorId");
            int? targetId = args.Value<int?>("targetId");
            if (!actorId.HasValue || !targetId.HasValue || actorId.Value == targetId.Value) throw new RpcException("INVALID_ARGUMENT", "distinct actorId and targetId are required");
            Npc actor = RequireNpc(actorId.Value);
            Npc target = RequireNpc(targetId.Value);
            if (!actor.IsPlayerThing || !target.IsSmartRace || target.IsDeath) throw new RpcException("TALK_NOT_AVAILABLE", "A player actor and living intelligent target are required");
            if (CommandMgr.Instance == null || CommandMgr.Instance.GetDef("TryTalk") == null) throw new RpcException("COMMAND_NOT_FOUND", "Native TryTalk command is unavailable");
            Command command = actor.AddCommand("TryTalk", new object[] { target });
            if (command == null) throw new RpcException("COMMAND_REJECTED", "The game rejected the talk command");
            return new { commandAccepted = true, actorId = actor.ID, target = ThingSummary(target), command = CommandSummary(command) };
        }

        private static object PerformTalkAction(JObject args)
        {
            int? actorId = args.Value<int?>("actorId");
            int? targetId = args.Value<int?>("targetId");
            string action = ((string)args["action"] ?? "").Trim();
            if (!actorId.HasValue || !targetId.HasValue || actorId.Value == targetId.Value || action.Length == 0 || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "distinct actorId/targetId, action, and confirm=true are required");
            Npc actor = RequireNpc(actorId.Value);
            Npc target = RequireNpc(targetId.Value);
            JianghuMgr manager = JianghuMgr.Instance;
            if (!actor.IsPlayerThing || !target.IsSmartRace || target.IsDeath || manager == null) throw new RpcException("TALK_NOT_AVAILABLE", "A player actor and living intelligent target are required");
            List<string> actions = manager.GetTalkActionList(actor, target);
            if (actions == null || !actions.Contains(action)) throw new RpcException("TALK_ACTION_UNAVAILABLE", "The requested action is not in the native available action list");
            Wnd_JianghuTalk window = Wnd_JianghuTalk.Instance;
            if (window == null) throw new RpcException("UI_NOT_READY", "Jianghu talk window is unavailable");
            window.Talk(actor, target);
            InvokePrivateMethod(window, "DoTalkAction", new object[] { action });
            return new { commandAccepted = true, completion = "dispatched", action = action, interaction = InteractionState(new JObject(new JProperty("actorId", actor.ID), new JProperty("targetId", target.ID))) };
        }

        private static object OpenGodCity(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue) throw new RpcException("INVALID_ARGUMENT", "id is required");
            Npc npc = RequireNpc(id.Value);
            if (RequirePractice(id.Value).GodPracticeData == null) throw new RpcException("GOD_CULTIVATION_UNAVAILABLE", "NPC has no god-cultivation data");
            Wnd_GodCity window = Wnd_GodCity.Instance;
            if (window == null) throw new RpcException("UI_NOT_READY", "God-city window is unavailable");
            window.ShowCity(npc);
            return new { commandAccepted = true, completion = "window-opened", state = GodCultivationState(new JObject(new JProperty("id", id.Value))) };
        }

        private static object RefineArtifact(JObject args)
        {
            int? npcId = args.Value<int?>("npcId");
            int? itemId = args.Value<int?>("itemId");
            int? refiningType = args.Value<int?>("refiningType");
            if (!npcId.HasValue || !itemId.HasValue || !refiningType.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "npcId, itemId, refiningType and confirm=true are required");
            Npc npc = RequireNpc(npcId.Value);
            ItemThing item = RequireThing(itemId.Value) as ItemThing;
            if (item == null || !item.IsPlayerThing || item.FreeCount <= 0) throw new RpcException("ITEM_NOT_AVAILABLE", "A free player-owned item is required");
            if (CommandMgr.Instance == null || CommandMgr.Instance.GetDef("RefiningFabao") == null) throw new RpcException("COMMAND_NOT_FOUND", "Native RefiningFabao command is unavailable");
            g_emItemLable kind = ParseEnum<g_emItemLable>(args, "kind");
            int helianDef = args.Value<int?>("helianDef") ?? 0;
            string helianParam = (string)args["helianParam"] ?? "";
            Command command = npc.AddCommand("RefiningFabao", new object[] { kind, item, refiningType.Value, helianDef, helianParam });
            if (command == null) throw new RpcException("COMMAND_REJECTED", "The game rejected artifact refinement");
            return new { commandAccepted = true, npcId = npc.ID, input = ItemSummary(item), kind = kind.ToString(), refiningType = refiningType.Value, command = CommandSummary(command) };
        }

        private static BodyPractice RequireBodyPractice(int id)
        {
            BodyPractice body = RequirePractice(id).BodyPracticeData;
            if (body == null) throw new RpcException("BODY_PRACTICE_UNAVAILABLE", "NPC has no body cultivation data");
            return body;
        }

        private static object ChangeBodyAttitude(JObject args)
        {
            int? id = args.Value<int?>("id");
            string name = ((string)args["name"] ?? "").Trim();
            if (!id.HasValue || name.Length == 0) throw new RpcException("INVALID_ARGUMENT", "id and attitude name are required");
            BodyPractice body = RequireBodyPractice(id.Value);
            if (body.Attitudes == null || !body.Attitudes.Contains(name)) throw new RpcException("ATTITUDE_NOT_KNOWN", "NPC has not unlocked this body attitude");
            body.ChangeAttitude(name);
            return new { commandAccepted = true, body = BodyCultivationState(new JObject(new JProperty("id", id.Value))) };
        }

        private static object BeginBodyQuenching(JObject args)
        {
            int? id = args.Value<int?>("id");
            string partName = ((string)args["part"] ?? "").Trim();
            string itemName = ((string)args["item"] ?? "").Trim();
            string methodName = ((string)args["method"] ?? "").Trim();
            if (!id.HasValue || partName.Length == 0 || itemName.Length == 0 || methodName.Length == 0 || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "id, part, item, method and confirm=true are required");
            Npc npc = RequireNpc(id.Value);
            BodyPractice body = RequireBodyPractice(id.Value);
            NpcBodyData.NpcBodyPart part = npc.PropertyMgr == null || npc.PropertyMgr.BodyData == null ? null : npc.PropertyMgr.BodyData.GetPart(partName);
            if (part == null || (npc.PropertyMgr.BodyData.RemovedParts != null && npc.PropertyMgr.BodyData.RemovedParts.Contains(part))) throw new RpcException("BODY_PART_NOT_FOUND", "An intact body part with that name is required");
            if (!body.CheckQuenchingMethod(methodName)) throw new RpcException("QUENCHING_METHOD_NOT_KNOWN", "NPC has not unlocked this quenching method");
            if (body.GetQuenchingItemCount(itemName) <= 0) throw new RpcException("QUENCHING_ITEM_UNAVAILABLE", "NPC has no quenching material with that name");
            body.BeginBodyQuenching(part, itemName, methodName, args.Value<bool?>("loop") ?? false, "AmazingCultivationMCP");
            return new { commandAccepted = true, body = BodyCultivationState(new JObject(new JProperty("id", id.Value))) };
        }

        private static object CancelBodyQuenching(JObject args)
        {
            int? id = args.Value<int?>("id");
            string part = ((string)args["part"] ?? "").Trim();
            if (!id.HasValue || part.Length == 0) throw new RpcException("INVALID_ARGUMENT", "id and part are required");
            BodyPractice body = RequireBodyPractice(id.Value);
            if (body.DoQuenchingMap == null || !body.DoQuenchingMap.ContainsKey(part)) throw new RpcException("QUENCHING_NOT_FOUND", "No active quenching for that part");
            body.CancelBodyQuenching(part);
            return new { commandAccepted = true, body = BodyCultivationState(new JObject(new JProperty("id", id.Value))) };
        }

        private static object ChangeBuildingMaterial(JObject args)
        {
            int? id = args.Value<int?>("id");
            string material = ((string)args["material"] ?? "").Trim();
            if (!id.HasValue || material.Length == 0 || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "id, material and confirm=true are required");
            BuildingThing building = RequireBuilding(id.Value);
            ThingDef def = ThingMgr.Instance.GetDef(g_emThingType.Item, material, true);
            if (def == null || def.Item == null || def.Item.BeMaterial == null) throw new RpcException("MATERIAL_NOT_FOUND", "The selected item is not a building material");
            object before = BuildingSummary(building);
            building.AddChangeMatsNeed(material, false);
            return new { commandAccepted = true, completion = "queued", before = before, after = BuildingSummary(building) };
        }

        private static object DismantleBuilding(JObject args)
        {
            int? id = args.Value<int?>("id");
            if (!id.HasValue || !(args.Value<bool?>("confirm") ?? false)) throw new RpcException("CONFIRMATION_REQUIRED", "id and confirm=true are required");
            BuildingThing building = RequireBuilding(id.Value);
            object before = BuildingSummary(building);
            Command command = building.AddCommand("Dismantle", new object[0]);
            if (command == null) throw new RpcException("COMMAND_REJECTED", "The game rejected the dismantle command");
            return new { commandAccepted = true, completion = "queued", before = before, command = CommandSummary(command) };
        }

        private static string[] CopyStrings(List<string> values)
        {
            return values == null ? new string[0] : values.ToArray();
        }

        private static object NpcState(JObject args)
        {
            Map map = RequireMap();
            List<Npc> source = map.Things.GetPlayerActiveNpcs(g_emNpcRaceType.Wisdom) ?? new List<Npc>();
            int offset = Math.Max(0, args.Value<int?>("offset") ?? 0);
            int limit = Math.Max(1, Math.Min(500, args.Value<int?>("limit") ?? 100));
            List<object> items = new List<object>();
            for (int i = offset; i < source.Count && items.Count < limit; i++) {
                Npc npc = source[i];
                if (npc == null) continue;
                items.Add(NpcSummary(npc));
            }
            int next = offset + items.Count;
            return new { items = items, total = source.Count, offset = offset, limit = limit, nextOffset = next < source.Count ? (int?)next : null };
        }

        private static string SafeString(Func<string> value)
        {
            try { return value(); } catch { return null; }
        }

        private static T PrivateField<T>(object target, string name) where T : class
        {
            if (target == null) return null;
            try {
                FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field == null ? null : field.GetValue(target) as T;
            } catch { return null; }
        }

        private static object PrivateValue(object target, string name)
        {
            if (target == null) return null;
            try {
                FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field == null ? null : field.GetValue(target);
            } catch { return null; }
        }

        private static object InvokePrivateMethod(object target, string name, object[] args)
        {
            if (target == null) return null;
            try {
                MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null) throw new MissingMethodException(target.GetType().FullName, name);
                return method.Invoke(target, args);
            } catch (TargetInvocationException ex) {
                throw ex.InnerException ?? ex;
            }
        }

        private static T PrivateStaticField<T>(Type type, string name) where T : class
        {
            if (type == null) return null;
            try {
                FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return field == null ? null : field.GetValue(null) as T;
            } catch { return null; }
        }

        private static object SafeValue(Func<object> value)
        {
            try { return value(); } catch { return null; }
        }

        private static object ScalarForJson(object value)
        {
            if (value == null) return null;
            Type type = value.GetType();
            if (type.IsEnum) return value.ToString();
            if (type.IsPrimitive || value is decimal || value is string) return value;
            if (value is Vector2) { Vector2 vector = (Vector2)value; return new { x = vector.x, y = vector.y }; }
            if (value is Vector3) { Vector3 vector = (Vector3)value; return new { x = vector.x, y = vector.y, z = vector.z }; }
            int[] ints = value as int[];
            if (ints != null && ints.Length <= 64) return ints;
            float[] floats = value as float[];
            if (floats != null && floats.Length <= 64) return floats;
            return null;
        }

        private static Dictionary<string, object> NativeScalars(object target, int limit)
        {
            Dictionary<string, object> values = new Dictionary<string, object>();
            if (target == null) return values;
            for (Type type = target.GetType(); type != null && type != typeof(object) && values.Count < limit; type = type.BaseType) {
                FieldInfo[] fields;
                try { fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch { continue; }
                foreach (FieldInfo field in fields) {
                    if (values.Count >= limit || field.IsStatic) break;
                    object value;
                    try { value = ScalarForJson(field.GetValue(target)); } catch { continue; }
                    if (value == null) continue;
                    string name = values.ContainsKey(field.Name) ? type.Name + "." + field.Name : field.Name;
                    values[name] = value;
                }
            }
            return values;
        }

        private static object UiSnapshot(JObject args)
        {
            GRoot root = GRoot.inst;
            if (root == null) throw new RpcException("UI_NOT_READY", "FairyGUI root is unavailable");
            int limit = Math.Max(1, Math.Min(10000, args.Value<int?>("limit") ?? 3000));
            List<object> nodes = new List<object>();
            UiTargets.Clear();
            Walk(root, null, nodes, limit);
            snapshotSequence++;
            return new { snapshotId = sessionId + ":" + worldEpoch + ":" + snapshotSequence, frame = Time.frameCount, nodes = nodes, truncated = nodes.Count >= limit };
        }

        private static void Walk(GObject obj, string parentId, List<object> nodes, int limit)
        {
            if (obj == null || nodes.Count >= limit) return;
            string targetId = "ui:" + worldEpoch + ":" + RuntimeHelpers.GetHashCode(obj);
            UiTargets[targetId] = new WeakReference(obj);
            Rect rect;
            try { rect = obj.LocalToGlobal(new Rect(0, 0, obj.width, obj.height)); }
            catch { rect = new Rect(obj.x, obj.y, obj.width, obj.height); }
            string text = null;
            try { text = obj.text; } catch { }
            bool actionable = false;
            try { actionable = obj.onClick != null && !obj.onClick.isEmpty; } catch { }
            string role = Role(obj);
            object value = null;
            object min = null;
            object max = null;
            object selectedIndex = null;
            object items = null;
            GComboBox combo = obj as GComboBox;
            GSlider slider = obj as GSlider;
            GProgressBar progress = obj as GProgressBar;
            GList list = obj as GList;
            GButton button = obj as GButton;
            if (combo != null) {
                value = combo.value;
                selectedIndex = combo.selectedIndex;
                items = combo.items;
            } else if (slider != null) {
                value = slider.value;
                min = slider.min;
                max = slider.max;
            } else if (progress != null) {
                value = progress.value;
                min = progress.min;
                max = progress.max;
            } else if (list != null) {
                selectedIndex = list.selectedIndex;
                items = new { count = list.numItems };
            } else if (button != null) value = button.selected;
            nodes.Add(new {
                targetId = targetId,
                parentTargetId = parentId,
                id = obj.id,
                name = obj.name,
                text = text,
                role = role,
                type = obj.GetType().FullName,
                visible = IsEffectivelyVisible(obj),
                touchable = obj.touchable,
                enabled = obj.enabled,
                grayed = obj.grayed,
                actionable = actionable || obj is GTextInput || obj is GComboBox || obj is GSlider || obj is GList,
                value = value,
                min = min,
                max = max,
                selectedIndex = selectedIndex,
                items = items,
                rect = new { x = rect.x, y = rect.y, width = rect.width, height = rect.height }
            });
            GComponent component = obj as GComponent;
            if (component == null) return;
            for (int i = 0; i < component.numChildren && nodes.Count < limit; i++) Walk(component.GetChildAt(i), targetId, nodes, limit);
        }

        private static string Role(GObject obj)
        {
            if (obj is GTextInput) return "text_field";
            if (obj is GComboBox) return "combo_box";
            if (obj is GSlider) return "slider";
            if (obj is GProgressBar) return "progress_bar";
            if (obj is GButton) return "button";
            if (obj is GList) return "list";
            if (obj is GTextField) return "label";
            if (obj is GComponent) return "container";
            return "object";
        }

        private static bool IsEffectivelyVisible(GObject obj)
        {
            GObject current = obj;
            while (current != null) {
                if (!current.visible || !current.onStage) return false;
                current = current.parent;
            }
            return true;
        }

        private static GObject ResolveUi(JObject args)
        {
            string targetId = (string)args["targetId"];
            if (!String.IsNullOrEmpty(targetId)) {
                WeakReference reference;
                if (!UiTargets.TryGetValue(targetId, out reference) || !reference.IsAlive) throw new RpcException("STALE_UI_REFERENCE", "UI target expired; request ui.snapshot again");
                return reference.Target as GObject;
            }
            JObject selector = args["selector"] as JObject;
            if (selector == null) throw new RpcException("INVALID_ARGUMENT", "targetId or selector is required");
            UiSnapshot(new JObject());
            string expectedName = (string)selector["name"];
            string expectedText = (string)selector["text"];
            string contains = (string)selector["contains"];
            string expectedType = (string)selector["type"];
            string expectedRole = (string)selector["role"];
            int nth = selector.Value<int?>("nth") ?? 0;
            int found = 0;
            foreach (WeakReference reference in UiTargets.Values) {
                GObject obj = reference.Target as GObject;
                if (obj == null) continue;
                string text = null; try { text = obj.text; } catch { }
                if (!String.IsNullOrEmpty(expectedName) && obj.name != expectedName) continue;
                if (!String.IsNullOrEmpty(expectedText) && text != expectedText) continue;
                if (!String.IsNullOrEmpty(contains) &&
                    (String.IsNullOrEmpty(obj.name) || obj.name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) &&
                    (String.IsNullOrEmpty(text) || text.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                if (!String.IsNullOrEmpty(expectedType) && obj.GetType().FullName != expectedType) continue;
                if (!String.IsNullOrEmpty(expectedRole) && Role(obj) != expectedRole) continue;
                if (found++ == nth) return obj;
            }
            throw new RpcException("TARGET_NOT_FOUND", "No UI control matched the selector");
        }

        private static object UiInput(JObject args)
        {
            GObject obj = ResolveUi(args);
            if (obj == null) throw new RpcException("STALE_UI_REFERENCE", "UI target no longer exists");
            if (!obj.onStage) throw new RpcException("STALE_UI_REFERENCE", "UI target is no longer on stage; request ui.snapshot again");
            if (!IsEffectivelyVisible(obj) || !obj.enabled || obj.grayed) throw new RpcException("CONTROL_DISABLED", "UI control is hidden or disabled");
            string action = (string)args["action"] ?? "click";
            if (action == "click" || action == "activate") {
                if (!obj.touchable) throw new RpcException("CONTROL_DISABLED", "UI control is not touchable");
                if (obj.onClick == null || obj.onClick.isEmpty) throw new RpcException("CONTROL_NOT_ACTIONABLE", "UI control has no click handler");
                obj.onClick.Call();
            } else if (action == "rightClick") {
                if (obj.onRightClick == null || obj.onRightClick.isEmpty) throw new RpcException("CONTROL_NOT_ACTIONABLE", "UI control has no right-click handler");
                obj.onRightClick.Call();
            } else if (action == "fill") {
                GTextInput input = obj as GTextInput;
                if (input == null || !input.editable) throw new RpcException("UNSUPPORTED_INPUT", "fill requires an editable text field");
                input.text = (string)args["text"] ?? "";
                if (input.onChanged != null) input.onChanged.Call();
            } else if (action == "setChecked") {
                GButton button = obj as GButton;
                if (button == null) throw new RpcException("UNSUPPORTED_INPUT", "setChecked requires a button control");
                button.selected = args.Value<bool?>("value") ?? false;
                if (button.onChanged != null) button.onChanged.Call();
            } else if (action == "selectIndex") {
                int? index = args.Value<int?>("index");
                if (!index.HasValue) throw new RpcException("INVALID_ARGUMENT", "selectIndex requires index");
                GComboBox combo = obj as GComboBox;
                GList list = obj as GList;
                if (combo != null) {
                    if (index.Value < -1 || combo.items == null || index.Value >= combo.items.Length) throw new RpcException("INVALID_ARGUMENT", "combo box index is out of range");
                    combo.selectedIndex = index.Value;
                    if (combo.onChanged != null) combo.onChanged.Call();
                } else if (list != null) {
                    if (index.Value < -1 || index.Value >= list.numItems) throw new RpcException("INVALID_ARGUMENT", "list index is out of range");
                    if (index.Value < 0) list.ClearSelection();
                    else list.AddSelection(index.Value, true, true);
                } else throw new RpcException("UNSUPPORTED_INPUT", "selectIndex requires a combo box or list");
            } else if (action == "setValue") {
                double? requested = args.Value<double?>("value");
                if (!requested.HasValue) throw new RpcException("INVALID_ARGUMENT", "setValue requires a numeric value");
                GSlider slider = obj as GSlider;
                if (slider != null) {
                    slider.value = Math.Max(slider.min, Math.Min(slider.max, requested.Value));
                    if (slider.onChanged != null) slider.onChanged.Call();
                } else throw new RpcException("UNSUPPORTED_INPUT", "setValue requires a slider");
            } else if (action == "selectValue") {
                GComboBox combo = obj as GComboBox;
                if (combo == null) throw new RpcException("UNSUPPORTED_INPUT", "selectValue requires a combo box");
                JToken token = args["value"];
                if (token == null) throw new RpcException("INVALID_ARGUMENT", "selectValue requires value");
                combo.value = token.ToString();
                    if (combo.onChanged != null) combo.onChanged.Call();
            } else throw new RpcException("UNSUPPORTED_INPUT", "Unsupported UI action: " + action);
            return new { path = "ui-control", inputProcessed = true, commandAccepted = true, completion = "dispatched", target = new { id = obj.id, name = obj.name, role = Role(obj) }, frame = Time.frameCount };
        }
    }

    public sealed class BridgeRunner : MonoBehaviour
    {
        public void Update()
        {
            Bridge.Update();
        }
    }

    internal sealed class RpcException : Exception
    {
        public readonly string Code;
        public RpcException(string code, string message) : base(message) { Code = code; }
    }
}
