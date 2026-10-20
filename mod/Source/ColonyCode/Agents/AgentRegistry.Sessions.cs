using System;
using System.Collections.Generic;
using ColonyCode.Http;
using RimWorld;
using Verse;

namespace ColonyCode.Agents
{
    /// <summary>Cumulative token usage of one Claude Code session (usage.update). Never money.</summary>
    public class UsageInfo
    {
        public long inputTokens;
        public long outputTokens;
        public long cacheReadTokens;
        public int turns;
        /// <summary>Epoch ms of the last usage.update, 0 when none arrived yet.</summary>
        public long updatedAt;
    }

    /// <summary>One Claude Code session the game has heard of (session.start or any event carrying its id).</summary>
    public class SessionInfo
    {
        public string id;
        /// <summary>Label from session.start (protocol v0.2), may be null; use <see cref="AgentRegistry.SessionLabel"/>.</summary>
        public string label;
        public string cwd;
        public string repo;
        public string branch;
        public string model;
        public bool connected;
        /// <summary>Game tick of session.end, -1 while connected or unknown.</summary>
        public int endedTick = -1;
        /// <summary>First-seen order.</summary>
        public int seq;
        public UsageInfo usage = new UsageInfo();

        // Review state (worktree.list), see AgentRegistry.Review.cs.
        public bool hasWorktreeList;
        public string baseBranch;
        public bool dirty;
        public List<WorktreeInfo> worktrees = new List<WorktreeInfo>();
    }

    /// <summary>Sessions half of the registry: labels, connection, usage. Not saved.</summary>
    public partial class AgentRegistry
    {
        public static event Action SessionsChanged;
        /// <summary>usage.update arrived for a session.</summary>
        public static event Action<SessionInfo> UsageChanged;

        private readonly List<SessionInfo> sessions = new List<SessionInfo>();
        private int nextSessionSeq;

        /// <summary>Every session seen in this game load, first seen first.</summary>
        public IReadOnlyList<SessionInfo> Sessions => sessions;

        public SessionInfo GetSession(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < sessions.Count; i++)
                if (sessions[i].id == id) return sessions[i];
            return null;
        }

        /// <summary>The session, created (as connected) when it is new. Null for an empty id.</summary>
        public SessionInfo EnsureSession(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            var s = GetSession(id);
            if (s != null) return s;
            s = new SessionInfo { id = id, seq = nextSessionSeq++, connected = !endedSessions.Contains(id) };
            sessions.Add(s);
            RaiseSessionsChanged();
            return s;
        }

        /// <summary>
        /// Sessions that count as "known" for the [label] prefixes: connected ones, plus ended ones
        /// whose tasks are still on the board (one in-game day).
        /// </summary>
        public List<SessionInfo> KnownSessions()
        {
            var list = new List<SessionInfo>();
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            foreach (var s in sessions)
                if (s.connected || (s.endedTick >= 0 && now - s.endedTick < TaskExpiryTicks)) list.Add(s);
            return list;
        }

        /// <summary>True when more than one session is known: then agents, tasks, letters and decisions show [label].</summary>
        public bool MultiSession
        {
            get
            {
                // Asked for every row and card each frame: computed once per frame.
                int frame = UnityEngine.Time.frameCount;
                if (frame != multiFrame)
                {
                    multiFrame = frame;
                    multiCached = KnownSessions().Count > 1;
                }
                return multiCached;
            }
        }

        private int multiFrame = -1;
        private bool multiCached;

        /// <summary>Human name of a session: label, else repo, else cwd's last segment, else the id start.</summary>
        public string SessionLabel(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return "?";
            var s = GetSession(sessionId);
            string label = BaseLabel(s, sessionId);
            if (s == null) return label;
            // Two known sessions with the same label (e.g. two terminals in one repo): add the id start.
            foreach (var o in sessions)
            {
                if (o == s || BaseLabel(o, o.id) != label) continue;
                if (!(o.connected || s.connected)) continue;
                return label + " " + ShortId(sessionId, 4);
            }
            return label;
        }

        private static string BaseLabel(SessionInfo s, string id)
        {
            if (s != null)
            {
                if (!string.IsNullOrEmpty(s.label)) return s.label;
                if (!string.IsNullOrEmpty(s.repo)) return s.repo;
                string seg = Privacy.Label(LastSegment(s.cwd));
                if (!string.IsNullOrEmpty(seg)) return seg;
            }
            return ShortId(id, 8);
        }

        private static string ShortId(string id, int n)
        {
            if (string.IsNullOrEmpty(id)) return "?";
            return id.Length > n ? id.Substring(0, n) : id;
        }

        private static string LastSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p = path.TrimEnd('/', '\\');
            int i = Math.Max(p.LastIndexOf('/'), p.LastIndexOf('\\'));
            return i >= 0 ? p.Substring(i + 1) : p;
        }

        /// <summary>"[label] " when several sessions are known, else "". Not escaped (labels come from the plugin).</summary>
        public string SessionPrefix(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) || !MultiSession) return "";
            return "[" + SessionLabel(sessionId) + "] ";
        }

        private void NoteSessionStart(Dictionary<string, object> ev, string session)
        {
            var s = EnsureSession(session);
            AbandonMerges(session);
            s.connected = true;
            s.endedTick = -1;
            s.label = Clip(Privacy.Label(Json.GetString(ev, "label")), 40) ?? s.label;
            s.cwd = Privacy.Path(Json.GetString(ev, "cwd")) ?? s.cwd;
            s.repo = Clip(Privacy.Label(Json.GetString(ev, "repo")), 60) ?? s.repo;
            s.branch = Json.GetString(ev, "branch") ?? s.branch;
            s.model = Json.GetString(ev, "model") ?? s.model;
            RaiseSessionsChanged();
        }

        private void NoteSessionEnd(string session)
        {
            var s = EnsureSession(session);
            AbandonMerges(session);
            s.connected = false;
            s.endedTick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            RaiseSessionsChanged();
        }

        private void OnUsageUpdate(Dictionary<string, object> ev, string session)
        {
            var s = EnsureSession(session);
            if (s == null) return;
            var u = s.usage;
            u.inputTokens = Math.Max(0, Json.GetLong(ev, "inputTokens", u.inputTokens));
            u.outputTokens = Math.Max(0, Json.GetLong(ev, "outputTokens", u.outputTokens));
            u.cacheReadTokens = Math.Max(0, Json.GetLong(ev, "cacheReadTokens", u.cacheReadTokens));
            u.turns = (int)Math.Max(0, Json.GetLong(ev, "turns", u.turns));
            u.updatedAt = Json.NowMs();
            var handlers = UsageChanged;
            if (handlers == null) return;
            foreach (Action<SessionInfo> h in handlers.GetInvocationList())
            {
                try { h(s); }
                catch (Exception e) { Log.Error("[ColonyCode] UsageChanged handler failed: " + e); }
            }
        }

        private static void RaiseSessionsChanged()
        {
            var handlers = SessionsChanged;
            if (handlers == null) return;
            foreach (Action h in handlers.GetInvocationList())
            {
                try { h(); }
                catch (Exception e) { Log.Error("[ColonyCode] SessionsChanged handler failed: " + e); }
            }
        }

        private static long NowTicksAbs()
        {
            return Find.TickManager != null ? Find.TickManager.TicksAbs : -1;
        }
    }
}
