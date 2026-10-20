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
                case "state.grid": return GridState(args);
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
                methods = new[] { "session.status", "session.sequence.begin", "session.sequence.end", "session.result", "events.poll", "state.overview", "state.world", "state.map", "state.npcs", "state.npc", "state.grid", "state.things", "state.resources", "state.school", "state.selection", "state.enums", "state.alerts", "state.commandTypes", "state.commands", "state.input", "game.focusNpc", "game.focusThing", "game.lookGrid", "game.openPanel", "game.save", "game.setWorkPriority", "game.setWorkEnabled", "game.setVipWork", "game.interruptNpcJob", "game.setSchedule", "game.selectCommand", "game.addThingCommand", "game.addGridCommand", "game.removeThingCommand", "map.click", "map.drag", "ui.snapshot", "ui.input", "runtime.pause", "runtime.speed" }
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
                case "cancel": main.CancelSelect(); break;
                default: throw new RpcException("INVALID_ARGUMENT", "panel must be build, produce, search, main, close, or cancel");
            }
            return new { commandAccepted = true, completion = "dispatched", panel = panel };
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
                elements = Enum.GetNames(typeof(g_emElementKind))
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
