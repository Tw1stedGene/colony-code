using System;
using System.Collections.Generic;
using ColonyCode.Core;
using ColonyCode.Http;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyCode.Agents
{
    public enum AgentState { Idle, Thinking, Reading, Editing, Running, Testing, WaitingUser, Blocked, Done, Error }

    public enum AgentStation { Desk, Terminal, Lounge, User }

    public class LogEntry
    {
        public long ts;
        /// <summary>text | tool | result | error | diff</summary>
        public string kind;
        public string text;
    }

    public class AgentInfo
    {
        public string id;
        public string sessionId;
        /// <summary>"lead" or "worker".</summary>
        public string role;
        /// <summary>Label sent by the plugin, may be null.</summary>
        public string label;
        /// <summary>Name shown in game: label, else the resident's short name, else a role-based fallback.</summary>
        public string displayName;
        public AgentState state;
        public AgentStation station;
        public string activity;
        /// <summary>Monitor log, oldest first, capped at <see cref="AgentRegistry.MaxLogEntries"/>.</summary>
        public List<LogEntry> log = new List<LogEntry>();
        /// <summary>Resident (remote-worker pawn) embodying the agent, or null when it has no body ("working remotely").</summary>
        public Pawn pawn;
        /// <summary>Epoch ms when the agent last became done/idle, 0 while busy.</summary>
        public long idleSinceMs;
        /// <summary>Creation order, for a stable mapping order.</summary>
        public int seq;
        /// <summary>Epoch ms of the last change.</summary>
        public long updatedAt;
    }

    public class DecisionInfo
    {
        public string id;
        public string agentId;
        /// <summary>"permission" or "question".</summary>
        public string kind;
        public string question;
        public List<string> options = new List<string>();
        public string context;
        /// <summary>Session of the decision (envelope sessionId, else the agent's), may be null.</summary>
        public string sessionId;
        /// <summary>v0.2 questions: several options may be picked (answer = labels joined with '\n').</summary>
        public bool multiSelect;
        /// <summary>v0.2 questions: a free-text "Other" answer is allowed.</summary>
        public bool allowText;
        /// <summary>The "Always allow covers: ..." line of a permission context, or null.</summary>
        public string covers;
        public bool answered;
        public string answer;

        public bool IsQuestion => kind == "question";
    }

    public class GoalInfo
    {
        public string id;
        public string sessionId;
        public string text;
        /// <summary>"active" | "done" | "failed"</summary>
        public string status;
        /// <summary>0..1, or -1 when unknown.</summary>
        public float progress = -1f;
        public string summary;
        /// <summary>Find.TickManager.TicksAbs when the game first saw the goal; -1 when unknown.</summary>
        public long startTicksAbs = -1;
        /// <summary>Agents (ids) of the goal's session that were busy while the goal was active.</summary>
        public HashSet<string> agentIds = new HashSet<string>();
    }

    /// <summary>
    /// Live model of the Claude Code agents, fed by bridge events, plus the
    /// remote-worker residents that embody them (AgentRegistry.Residents.cs). Only the residents and
    /// their desks are saved; agent state is rebuilt from the plugin's snapshot.
    /// RimWorld adds this component to every game automatically.
    /// </summary>
    public partial class AgentRegistry : GameComponent
    {
        public const int MaxLogEntries = 500;
        public const int MaxGoals = 100;
        /// <summary>Protocol: `activity` is one line of at most 48 chars.</summary>
        public const int MaxActivityChars = 48;

        /// <summary>Names of the residents, in arrival order; then "Marlow 2", "Juniper 2", ...</summary>
        public static readonly string[] Roster = { "Marlow", "Juniper", "Kit", "Wren", "Rowan", "Tove" };

        private static AgentRegistry instance;

        /// <summary>The registry of the current game, or null at the main menu.</summary>
        public static AgentRegistry Instance => Current.Game != null ? instance : null;

        public static event Action<DecisionInfo> DecisionOpened;
        public static event Action<DecisionInfo> DecisionClosed;
        public static event Action<GoalInfo> GoalChanged;
        /// <summary>An agent was created or changed (agent.upsert, session.end).</summary>
        public static event Action<AgentInfo> AgentChanged;

        private readonly List<AgentInfo> agents = new List<AgentInfo>();
        private readonly Dictionary<string, AgentInfo> byId = new Dictionary<string, AgentInfo>();
        private readonly List<DecisionInfo> openDecisions = new List<DecisionInfo>();
        private readonly List<GoalInfo> goals = new List<GoalInfo>();
        private readonly HashSet<string> endedSessions = new HashSet<string>();

        // Saved: the residents (remote-worker pawns) and their desks. Agent state itself is not saved.
        private List<ResidentRecord> residents = new List<ResidentRecord>();

        private float nextRemap;
        private int nextArrivalTick;
        private int lastBrokenBuildTick = -999999;
        private int nextSeq;

        public const float RemapIntervalSeconds = 1f;
        public const int MaintenanceIntervalTicks = 250;
        public const int ArrivalIntervalTicks = 2500;
        public const int BaseAgentPawnCap = 3;
        public const int AgentPawnsPerServerRack = 2;
        /// <summary>A worker that stays done/idle this long gives its resident back.</summary>
        public const long WorkerReleaseMs = 30000;

        public AgentRegistry() { instance = this; }

        public AgentRegistry(Game game) { instance = this; }

        public IReadOnlyList<AgentInfo> Agents => agents;
        public IReadOnlyList<DecisionInfo> OpenDecisions => openDecisions;
        public IReadOnlyList<GoalInfo> Goals => goals;

        public AgentInfo Get(string id)
        {
            if (id == null) return null;
            AgentInfo a;
            return byId.TryGetValue(id, out a) ? a : null;
        }

        /// <summary>The agent linked to this pawn, or null.</summary>
        public AgentInfo GetForPawn(Pawn pawn)
        {
            if (pawn == null) return null;
            for (int i = 0; i < agents.Count; i++)
                if (agents[i].pawn == pawn) return agents[i];
            return null;
        }

        public static bool IsActive(AgentState s)
        {
            return s == AgentState.Thinking || s == AgentState.Reading || s == AgentState.Editing ||
                   s == AgentState.Running || s == AgentState.Testing;
        }

        // ------------------------------------------------------------------ player actions

        public void AnswerDecision(string decisionId, string option, string text)
        {
            if (decisionId == null) return;
            var d = openDecisions.Find(x => x.id == decisionId);
            bool sent = Bridge.AnswerDecision(decisionId, option, text);
            if (!sent && Bridge.Instance == null)
                Messages.Message("ColonyCodeW_BridgeNotRunning".Translate(), MessageTypeDefOf.RejectInput, false);
            if (d == null) return;
            d.answered = true;
            d.answer = option;
            openDecisions.Remove(d);
            RaiseDecisionClosed(d);
        }

        /// <param name="sessionId">The session picked by the player, or null for "any session".</param>
        public void SubmitGoal(string text, string sessionId = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Send("goal.submit", new Dictionary<string, object> { { "text", text.Trim() } }, NullIfEmpty(sessionId));
        }

        /// <summary>
        /// user.message. To one agent: the agent's session. To "all": one copy for <paramref name="sessionId"/>
        /// when given, else one copy per connected session.
        /// </summary>
        public void SendMessage(string to, string text, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if ((string.IsNullOrEmpty(to) || to == "all") && !string.IsNullOrEmpty(sessionId))
            {
                Send("user.message", new Dictionary<string, object> { { "to", "all" }, { "text", text.Trim() } }, sessionId);
                return;
            }
            SendMessage(to, text);
        }

        private static string NullIfEmpty(string s) { return string.IsNullOrEmpty(s) ? null : s; }

        public void SendMessage(string to, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (string.IsNullOrEmpty(to)) to = "all";
            if (to != "all")
            {
                Send("user.message", new Dictionary<string, object> { { "to", to }, { "text", text.Trim() } }, Get(to)?.sessionId);
                return;
            }
            // "all": one copy per connected session, so every Claude Code session gets it.
            var sessions = new HashSet<string>();
            foreach (var a in agents)
                if (!string.IsNullOrEmpty(a.sessionId) && !endedSessions.Contains(a.sessionId)) sessions.Add(a.sessionId);
            if (sessions.Count == 0)
            {
                Send("user.message", new Dictionary<string, object> { { "to", to }, { "text", text.Trim() } }, null);
                return;
            }
            foreach (var sid in sessions)
                Send("user.message", new Dictionary<string, object> { { "to", to }, { "text", text.Trim() } }, sid);
        }

        public void Interrupt(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return;
            Send("agent.interrupt", new Dictionary<string, object> { { "agentId", agentId } }, Get(agentId)?.sessionId);
        }

        private static void Send(string type, Dictionary<string, object> payload, string sessionId)
        {
            if (Bridge.EnqueueInbox(type, payload, sessionId) == null)
                Messages.Message("ColonyCodeW_BridgeNotRunning".Translate(), MessageTypeDefOf.RejectInput, false);
        }

        // ------------------------------------------------------------------ lifecycle

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving) PruneResidentRecords();
            Scribe_Collections.Look(ref residents, "residents", LookMode.Deep);
            ExposeJournal();
            ExposeOffice();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (residents == null) residents = new List<ResidentRecord>();
                PruneResidentRecords();
            }
        }

        public override void FinalizeInit()
        {
            instance = this;
            try { World.ResidentUtility.EnsureFaction(); }
            catch (Exception e) { Log.Error("[ColonyCode] Could not set up the Remote Workers faction: " + e); }
        }

        public override void GameComponentUpdate()
        {
            instance = this;
            if (Time.realtimeSinceStartup < nextRemap) return;
            nextRemap = Time.realtimeSinceStartup + RemapIntervalSeconds;
            if (Current.ProgramState != ProgramState.Playing) return;
            try { Remap(); }
            catch (Exception e) { Log.ErrorOnce("[ColonyCode] Agent mapping failed: " + e, 0x6c0de01); }
        }

        public override void GameComponentTick()
        {
            int now = Find.TickManager.TicksGame;
            if (now % MaintenanceIntervalTicks != 0) return;
            try
            {
                MaintainResidents(now);
                ExpireTasks(now);
                if (now >= nextArrivalTick)
                {
                    nextArrivalTick = now + ArrivalIntervalTicks;
                    TryArrival(now);
                }
            }
            catch (Exception e) { Log.ErrorOnce("[ColonyCode] Resident upkeep failed: " + e, 0x6c0de03); }
        }

        public override void GameComponentOnGUI()
        {
            World.AgentOverlay.Draw(this);
        }

        // ------------------------------------------------------------------ events

        /// <summary>Apply one bridge event (main thread). Unknown types are ignored.</summary>
        public void HandleEvent(Dictionary<string, object> ev)
        {
            if (ev == null) return;
            string type = Json.GetString(ev, "type");
            string session = Json.GetString(ev, "sessionId");
            switch (type)
            {
                case "session.start": OnSessionStart(ev, session); break;
                case "session.end": OnSessionEnd(session); break;
                case "agent.upsert": OnAgentUpsert(Json.GetObject(ev, "agent"), session); break;
                case "agent.log": OnAgentLog(ev); break;
                case "agent.say": OnAgentSay(ev); break;
                case "decision.open": OnDecisionOpen(Json.GetObject(ev, "decision"), session); break;
                case "decision.closed": OnDecisionClosed(ev); break;
                case "goal.upsert": OnGoalUpsert(Json.GetObject(ev, "goal"), session); break;
                case "task.snapshot": OnTaskSnapshot(ev, session); break;
                case "usage.update": OnUsageUpdate(ev, session); break;
                case "worktree.list": OnWorktreeList(ev, session); break;
                case "diff.result": OnDiffResult(ev, session); break;
                case "merge.result": OnMergeResult(ev, session); break;
            }
        }

        private static string LeadId(string sessionId)
        {
            return "lead-" + (sessionId.Length > 8 ? sessionId.Substring(0, 8) : sessionId);
        }

        private void OnSessionStart(Dictionary<string, object> ev, string session)
        {
            if (string.IsNullOrEmpty(session)) return;
            endedSessions.Remove(session);
            KeepSessionTasks(session);
            NoteSessionStart(ev, session);
            string id = LeadId(session);
            if (Get(id) == null)
            {
                var a = CreateAgent(id, session, "lead", null);
                a.state = AgentState.Idle;
                a.station = AgentStation.Lounge;
                a.activity = "clocked in";
            }
            Messages.Message("ColonyCodeW_SessionConnected".Translate(SessionLabel(session)), MessageTypeDefOf.NeutralEvent, false);
            Remap();
        }

        private void OnSessionEnd(string session)
        {
            if (string.IsNullOrEmpty(session)) return;
            endedSessions.Add(session);
            ExpireSessionTasksLater(session);
            NoteSessionEnd(session);
            foreach (var a in agents)
            {
                if (a.sessionId != session) continue;
                a.state = AgentState.Idle;
                a.station = AgentStation.Lounge;
                if (a.idleSinceMs == 0) a.idleSinceMs = Json.NowMs();
                Touch(a);
                RaiseAgentChanged(a);
            }
            Remap();
        }

        private void OnAgentUpsert(Dictionary<string, object> obj, string session)
        {
            string id = Json.GetString(obj, "id");
            if (string.IsNullOrEmpty(id)) return;
            var a = Get(id);
            bool isNew = a == null;
            if (isNew) a = CreateAgent(id, session, Json.GetString(obj, "role"), Privacy.Label(Json.GetString(obj, "label")));
            if (!string.IsNullOrEmpty(session)) a.sessionId = session;
            EnsureSession(a.sessionId);
            string role = Json.GetString(obj, "role");
            if (role == "lead" || role == "worker") a.role = role;
            string label = Privacy.Label(Json.GetString(obj, "label"));
            if (!string.IsNullOrEmpty(label) && label != a.label)
            {
                a.label = label;
                RefreshDisplayName(a);
            }
            AgentState prev = a.state;
            AgentState st;
            if (TryParseState(Json.GetString(obj, "state"), out st)) a.state = st;
            AgentStation stn;
            if (TryParseStation(Json.GetString(obj, "station"), out stn)) a.station = stn;
            string act = Json.GetString(obj, "activity");
            if (act != null) a.activity = act.Length > MaxActivityChars ? act.Substring(0, MaxActivityChars) : act;
            bool resting = a.state == AgentState.Idle || a.state == AgentState.Done;
            if (!resting) a.idleSinceMs = 0;
            else if (a.idleSinceMs == 0 || isNew) a.idleSinceMs = Json.NowMs();
            Touch(a);

            if (a.state == AgentState.Error && prev != AgentState.Error) World.AgentFlavor.BrokenBuild(ref lastBrokenBuildTick);
            if (IsActive(a.state)) NoteAgentBusy(a);
            Remap();
            if (a.pawn != null && (a.state != prev || isNew)) World.AgentPawnUtility.NudgeToWork(a);
            RaiseAgentChanged(a);
        }

        private AgentInfo CreateAgent(string id, string session, string role, string label)
        {
            var a = new AgentInfo
            {
                id = id,
                sessionId = session,
                role = role == "worker" ? "worker" : (role == "lead" ? "lead" : (id.StartsWith("lead-") ? "lead" : "worker")),
                label = string.IsNullOrEmpty(label) ? null : label,
                state = AgentState.Idle,
                station = AgentStation.Lounge,
                activity = "",
            };
            a.seq = nextSeq++;
            a.idleSinceMs = Json.NowMs();
            RefreshDisplayName(a);
            agents.Add(a);
            byId[id] = a;
            Touch(a);
            return a;
        }

        /// <summary>label, else the resident's name, else the start of the agent id.</summary>
        private static void RefreshDisplayName(AgentInfo a)
        {
            if (!string.IsNullOrEmpty(a.label)) a.displayName = a.label;
            else if (a.pawn != null) a.displayName = a.pawn.LabelShort;
            else a.displayName = a.id.Length > 10 ? a.id.Substring(0, 10) : a.id;
        }

        private void OnAgentLog(Dictionary<string, object> ev)
        {
            var a = Get(Json.GetString(ev, "agentId"));
            var entries = Json.GetList(ev, "entries");
            if (a == null || entries == null) return;
            foreach (var o in entries)
            {
                var e = o as Dictionary<string, object>;
                if (e == null) continue;
                a.log.Add(new LogEntry
                {
                    ts = Json.GetLong(e, "ts", Json.NowMs()),
                    kind = Json.GetString(e, "kind", "text"),
                    text = Json.GetString(e, "text", ""),
                });
            }
            if (a.log.Count > MaxLogEntries) a.log.RemoveRange(0, a.log.Count - MaxLogEntries);
            Touch(a);
        }

        private void OnAgentSay(Dictionary<string, object> ev)
        {
            var a = Get(Json.GetString(ev, "agentId"));
            string text = Json.GetString(ev, "text");
            if (a == null || string.IsNullOrEmpty(text)) return;
            var settings = ColonyCodeMod.Settings;
            if (settings != null && !settings.flavorSpeech) return;
            var p = a.pawn;
            if (p == null || !p.Spawned || p.Map == null) return;
            if (text.Length > 80) text = text.Substring(0, 77) + "...";
            MoteMaker.ThrowText(p.DrawPos + new Vector3(0f, 0f, 0.6f), p.Map, text, Color.white, 4f);
        }

        private void OnDecisionOpen(Dictionary<string, object> obj, string session)
        {
            string id = Json.GetString(obj, "id");
            if (string.IsNullOrEmpty(id)) return;
            if (openDecisions.Exists(x => x.id == id)) return;
            var d = new DecisionInfo
            {
                id = id,
                agentId = Json.GetString(obj, "agentId"),
                kind = Json.GetString(obj, "kind", "permission"),
                question = Json.GetString(obj, "question", ""),
                context = FormatContext(obj.TryGetValue("context", out var ctxVal) ? ctxVal : null),
                multiSelect = Json.GetBool(obj, "multiSelect"),
                allowText = Json.GetBool(obj, "allowText"),
            };
            d.sessionId = !string.IsNullOrEmpty(session) ? session : Get(d.agentId)?.sessionId;
            d.covers = ExtractCovers(d.context);
            // Every view shows the "Always allow covers:" line on its own, so keep it out of the context.
            if (!string.IsNullOrEmpty(d.covers) && d.context != null) d.context = d.context.Replace(d.covers, "").TrimEnd();
            var opts = Json.GetList(obj, "options");
            if (opts != null) foreach (var o in opts) if (o is string s) d.options.Add(s);
            Bridge.RegisterDecision(id);
            openDecisions.Add(d);
            var handlers = DecisionOpened;
            if (handlers != null)
                foreach (Action<DecisionInfo> h in handlers.GetInvocationList())
                {
                    try { h(d); }
                    catch (Exception e) { Log.Error("[ColonyCode] DecisionOpened handler failed: " + e); }
                }
        }

        /// <summary>
        /// decision.open `context` is { tool, input } from the plugin, but any JSON value is accepted:
        /// a string is used as is, an object becomes "tool: X" plus one "key: value" line per input field.
        /// </summary>
        public static string FormatContext(object v)
        {
            if (v == null) return null;
            if (v is string s) return s.Length == 0 ? null : s;
            if (v is Dictionary<string, object> d)
            {
                var sb = new System.Text.StringBuilder();
                string tool = Json.GetString(d, "tool");
                if (!string.IsNullOrEmpty(tool)) sb.Append("tool: ").Append(tool);
                object input;
                d.TryGetValue("input", out input);
                if (input is Dictionary<string, object> fields)
                {
                    foreach (var kv in fields)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(kv.Key).Append(": ").Append(kv.Value is string str ? str : Json.Serialize(kv.Value));
                    }
                }
                else if (input != null)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(input is string istr ? istr : Json.Serialize(input));
                }
                foreach (var kv in d)
                {
                    if (kv.Key == "tool" || kv.Key == "input") continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(kv.Key).Append(": ").Append(kv.Value is string str2 ? str2 : Json.Serialize(kv.Value));
                }
                string text = sb.ToString();
                if (text.Length > 4000) text = text.Substring(0, 4000) + "…";
                return text.Length == 0 ? null : text;
            }
            string json = Json.Serialize(v);
            return json.Length > 4000 ? json.Substring(0, 4000) + "…" : json;
        }

        /// <summary>The "Always allow covers: ..." line of a context (protocol v0.2), or null.</summary>
        public static string ExtractCovers(string context)
        {
            if (string.IsNullOrEmpty(context)) return null;
            const string marker = "Always allow covers:";
            int i = context.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int end = context.IndexOf('\n', i);
            string line = (end < 0 ? context.Substring(i) : context.Substring(i, end - i)).Trim();
            return line.Length > 600 ? line.Substring(0, 600) + "…" : line;
        }

        private void RaiseAgentChanged(AgentInfo a)
        {
            var handlers = AgentChanged;
            if (handlers == null) return;
            foreach (Action<AgentInfo> h in handlers.GetInvocationList())
            {
                try { h(a); }
                catch (Exception e) { Log.Error("[ColonyCode] AgentChanged handler failed: " + e); }
            }
        }

        private void OnDecisionClosed(Dictionary<string, object> ev)
        {
            string id = Json.GetString(ev, "id");
            var d = openDecisions.Find(x => x.id == id);
            if (d == null) return;
            d.answered = true;
            d.answer = Json.GetString(ev, "option");
            openDecisions.Remove(d);
            Bridge.ForgetDecision(id);
            RaiseDecisionClosed(d);
        }

        private static void RaiseDecisionClosed(DecisionInfo d)
        {
            var handlers = DecisionClosed;
            if (handlers == null) return;
            foreach (Action<DecisionInfo> h in handlers.GetInvocationList())
            {
                try { h(d); }
                catch (Exception e) { Log.Error("[ColonyCode] DecisionClosed handler failed: " + e); }
            }
        }

        private void OnGoalUpsert(Dictionary<string, object> obj, string session)
        {
            string id = Json.GetString(obj, "id");
            if (string.IsNullOrEmpty(id)) return;
            var g = goals.Find(x => x.id == id);
            string prevStatus = g?.status;
            if (g == null)
            {
                g = new GoalInfo { id = id, status = "active", startTicksAbs = NowTicksAbs() };
                goals.Add(g);
                if (goals.Count > MaxGoals) goals.RemoveAt(0);
            }
            if (!string.IsNullOrEmpty(session)) g.sessionId = session;
            EnsureSession(g.sessionId);
            g.text = Json.GetString(obj, "text", g.text);
            string status = Json.GetString(obj, "status");
            if (status == "active" || status == "done" || status == "failed") g.status = status;
            if (obj != null && obj.ContainsKey("progress"))
                g.progress = Mathf.Clamp01((float)Json.GetDouble(obj, "progress", 0));
            else if (g.status == "done") g.progress = 1f;
            g.summary = Json.GetString(obj, "summary", g.summary);

            if (g.status == "active") NoteGoalActive(g);
            if (g.status != prevStatus)
            {
                if (g.status == "done") World.AgentFlavor.ShippedIt();
                else if (g.status == "failed") World.AgentFlavor.BrokenBuild(ref lastBrokenBuildTick);
                if (g.status == "done" || g.status == "failed") RecordJournal(g);
            }
            var handlers = GoalChanged;
            if (handlers == null) return;
            foreach (Action<GoalInfo> h in handlers.GetInvocationList())
            {
                try { h(g); }
                catch (Exception e) { Log.Error("[ColonyCode] GoalChanged handler failed: " + e); }
            }
        }

        private static void Touch(AgentInfo a) { a.updatedAt = Json.NowMs(); }

        public static bool TryParseState(string s, out AgentState st)
        {
            switch (s)
            {
                case "idle": st = AgentState.Idle; return true;
                case "thinking": st = AgentState.Thinking; return true;
                case "reading": st = AgentState.Reading; return true;
                case "editing": st = AgentState.Editing; return true;
                case "running": st = AgentState.Running; return true;
                case "testing": st = AgentState.Testing; return true;
                case "waiting_user": st = AgentState.WaitingUser; return true;
                case "blocked": st = AgentState.Blocked; return true;
                case "done": st = AgentState.Done; return true;
                case "error": st = AgentState.Error; return true;
            }
            st = AgentState.Idle;
            return false;
        }

        public static bool TryParseStation(string s, out AgentStation st)
        {
            switch (s)
            {
                case "desk": st = AgentStation.Desk; return true;
                case "terminal": st = AgentStation.Terminal; return true;
                case "lounge": st = AgentStation.Lounge; return true;
                case "user": st = AgentStation.User; return true;
            }
            st = AgentStation.Desk;
            return false;
        }

        // ------------------------------------------------------------------ cap

        /// <summary>Max residents: 3 + 2 per powered server rack on the player's home maps.</summary>
        public static int AgentPawnCap()
        {
            int racks = 0;
            var def = World.ColonyCodeDefOf.ColonyCode_ServerRack;
            if (def != null && Find.Maps != null)
                foreach (var m in Find.Maps)
                {
                    if (!m.IsPlayerHome) continue;
                    foreach (var b in m.listerBuildings.AllBuildingsColonistOfDef(def))
                    {
                        var power = b.GetComp<CompPowerTrader>();
                        if (power == null || power.PowerOn) racks++;
                    }
                }
            return BaseAgentPawnCap + AgentPawnsPerServerRack * racks;
        }
    }

    /// <summary>Subscribes the registry to bridge events once at startup.</summary>
    [StaticConstructorOnStartup]
    public static class AgentRegistryBootstrap
    {
        static AgentRegistryBootstrap()
        {
            GameComponent_ColonyCode.OnBridgeEvent += ev => AgentRegistry.Instance?.HandleEvent(ev);
        }
    }
}
