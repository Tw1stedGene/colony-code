using System;
using System.Collections.Generic;
using System.Globalization;
using ColonyCode.Http;
using RimWorld;
using Verse;

namespace ColonyCode.Agents
{
    /// <summary>
    /// One card on the task board (task.snapshot): a task from Claude Code's own task list, or a card
    /// the plugin derived from a prompt (goal), a subagent (agent) or the game's task.create (game).
    /// </summary>
    public class TaskInfo
    {
        public string id;
        public string sessionId;
        public string subject;
        /// <summary>"todo" | "doing" | "done"</summary>
        public string status;
        /// <summary>"goal" | "agent" | "game" | "claude" (missing = "claude").</summary>
        public string source = "claude";
        /// <summary>A subagent card's goal card id, may be null.</summary>
        public string parent;
        /// <summary>A done derived card whose turn did not answer: "failed" | "interrupted", else null.</summary>
        public string result;
        /// <summary>The parent card's subject (same session), resolved on each snapshot; null when unknown.</summary>
        public string parentSubject;

        // UI caches (TaskBoardView): rebuilt only when what they show changes.
        public string uiParentLine;
        public string uiParentLineFor;
        public string description;
        public string activeForm;
        public string owner;
        /// <summary>The agent that last moved the task (lead or worker id), may be null.</summary>
        public string agentId;
        public List<string> blockedBy = new List<string>();
        /// <summary>Creation order (first time the game saw this task), for a stable order.</summary>
        public int seq;
        /// <summary>Order in which tasks became done, higher = newer; 0 while not done.</summary>
        public int doneSeq;

        /// <summary>
        /// A game-created task no Claude Code session has picked up yet: shown by the game itself until
        /// a task.snapshot carries the plugin's own card with the same id.
        /// </summary>
        public bool pending;

        public bool IsBlocked => blockedBy != null && blockedBy.Count > 0;

        // Task details (protocol v0.3.1, all optional; derived cards only).
        /// <summary>When the card was made / its turn first started / it went done (epoch ms), 0 when unknown.</summary>
        public long createdAt, startedAt, doneAt;
        /// <summary>Claude's final answer for this card (markdown, home paths made private), or null.</summary>
        public string resultText;
        /// <summary>Files the card's turn(s) changed, in the plugin's order.</summary>
        public List<TaskChange> changes = new List<TaskChange>();
        /// <summary>Files changed beyond the ones listed.</summary>
        public int changesOmitted;
        /// <summary>Bumped whenever resultText or changes change: the details window rebuilds its caches then.</summary>
        public int detailVersion;
    }

    /// <summary>One file a card's turn changed (task.snapshot "changes").</summary>
    public class TaskChange
    {
        public string path;
        public int additions;
        public int deletions;
        /// <summary>Unified hunks ("@@ -a,b +c,d @@", "+", "-", " " lines), or null when left out.</summary>
        public string diff;
        public bool truncated;
        /// <summary>On a goal card: the subagent cards whose edits are included.</summary>
        public List<string> via = new List<string>();

        private DiffFile parsed;

        /// <summary>The diff as a DiffFile for DiffRowsView, parsed once.</summary>
        public DiffFile Parsed
        {
            get
            {
                if (parsed == null) parsed = ParseUnified(path, diff, additions, deletions);
                return parsed;
            }
        }

        public bool SameAs(TaskChange o)
        {
            if (o == null || o.path != path || o.additions != additions || o.deletions != deletions || o.truncated != truncated || o.diff != diff) return false;
            if (o.via.Count != via.Count) return false;
            for (int i = 0; i < via.Count; i++) if (o.via[i] != via[i]) return false;
            return true;
        }

        /// <summary>
        /// Unified hunk text → DiffFile. Line numbers come from "@@ -a,b +c,d @@" headers; a header
        /// without numbers ("@@", the plugin's fallback) leaves them 0 (not shown). File headers
        /// ("--- a/x", "+++ b/x") are skipped.
        /// </summary>
        public static DiffFile ParseUnified(string path, string diff, int additions, int deletions)
        {
            var f = new DiffFile { path = path ?? "?", status = "modified", additions = additions, deletions = deletions };
            if (string.IsNullOrEmpty(diff)) return f;
            DiffHunk h = null;
            int oldNo = 0, newNo = 0;
            foreach (var raw in diff.Replace("\r", "").Split('\n'))
            {
                if (raw.StartsWith("@@"))
                {
                    h = new DiffHunk { header = raw };
                    f.hunks.Add(h);
                    ParseHeader(raw, out oldNo, out newNo);
                    continue;
                }
                if (raw.StartsWith("+++ ") || raw.StartsWith("--- ")) continue;
                if (h == null)
                {
                    h = new DiffHunk { header = "" };
                    f.hunks.Add(h);
                }
                var l = new DiffLine { text = raw.Length > 0 ? raw.Substring(1) : "" };
                char c = raw.Length > 0 ? raw[0] : ' ';
                if (c == '+') { l.kind = "add"; l.newNo = newNo > 0 ? newNo++ : 0; }
                else if (c == '-') { l.kind = "del"; l.oldNo = oldNo > 0 ? oldNo++ : 0; }
                else
                {
                    l.kind = "ctx";
                    if (c != ' ') l.text = raw; // "\ No newline at end of file" and the like
                    else
                    {
                        l.oldNo = oldNo > 0 ? oldNo++ : 0;
                        l.newNo = newNo > 0 ? newNo++ : 0;
                    }
                }
                h.lines.Add(l);
            }
            return f;
        }

        /// <summary>"@@ -12,3 +12,4 @@ ..." → 12, 12. A side that starts at 0 (a new file) counts from 1.</summary>
        private static void ParseHeader(string header, out int oldNo, out int newNo)
        {
            oldNo = 0;
            newNo = 0;
            int minus = header.IndexOf(" -", StringComparison.Ordinal);
            int plus = header.IndexOf(" +", StringComparison.Ordinal);
            if (minus < 0 || plus < 0) return;
            int a = ReadInt(header, minus + 2), c = ReadInt(header, plus + 2);
            if (a < 0 || c < 0) return;
            oldNo = Math.Max(1, a);
            newNo = Math.Max(1, c);
        }

        private static int ReadInt(string s, int i)
        {
            int n = 0, digits = 0;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9' && digits < 9) { n = n * 10 + (s[i] - '0'); i++; digits++; }
            return digits == 0 ? -1 : n;
        }
    }

    /// <summary>
    /// The task board half of the registry: each session's task list, replaced whole by every
    /// task.snapshot. Not saved: the plugin sends the list again when it reconnects.
    /// A session's tasks stay after session.end and are dropped one in-game day later, unless the
    /// session starts again first.
    /// </summary>
    public partial class AgentRegistry
    {
        /// <summary>Ticks a session's tasks stay on the board after session.end.</summary>
        public static int TaskExpiryTicks => GenDate.TicksPerDay;

        public static event Action TasksChanged;

        /// <summary>sessionId -> tasks in creation order. Sessions in first-seen order.</summary>
        private readonly Dictionary<string, List<TaskInfo>> tasksBySession = new Dictionary<string, List<TaskInfo>>();
        private readonly List<string> taskSessionOrder = new List<string>();
        /// <summary>sessionId -> game tick at which its tasks are dropped (set by session.end).</summary>
        private readonly Dictionary<string, int> taskExpiry = new Dictionary<string, int>();
        private readonly List<TaskInfo> allTasks = new List<TaskInfo>();
        /// <summary>Game-created tasks waiting for a session to take them (see TaskInfo.pending).</summary>
        private readonly List<TaskInfo> pendingTasks = new List<TaskInfo>();
        private readonly HashSet<string> tmpTaskIds = new HashSet<string>();
        private int nextTaskSeq = 1;
        private int nextDoneSeq = 1;

        /// <summary>All sessions' tasks: by session (first seen first), then creation order.</summary>
        public IReadOnlyList<TaskInfo> Tasks => allTasks;

        private void OnTaskSnapshot(Dictionary<string, object> ev, string session)
        {
            if (string.IsNullOrEmpty(session)) return;
            var list = Json.GetList(ev, "tasks");
            if (list == null) return;
            EnsureSession(session);

            List<TaskInfo> old;
            tasksBySession.TryGetValue(session, out old);
            var byId = new Dictionary<string, TaskInfo>();
            if (old != null) foreach (var t in old) byId[t.id] = t;

            var fresh = new List<TaskInfo>();
            var seen = new HashSet<string>();
            foreach (var o in list)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                string id = IdString(d.TryGetValue("id", out var idv) ? idv : null);
                if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                string status = Json.GetString(d, "status");
                if (status != "todo" && status != "doing" && status != "done") status = "todo";

                TaskInfo t;
                if (!byId.TryGetValue(id, out t)) t = new TaskInfo { id = id, sessionId = session, seq = nextTaskSeq++ };
                if (status == "done" && (t.status != "done" || t.doneSeq == 0)) t.doneSeq = nextDoneSeq++;
                else if (status != "done") t.doneSeq = 0;
                t.status = status;
                t.subject = Clip(Json.GetString(d, "subject"), 300) ?? ("#" + id);
                t.description = Clip(Json.GetString(d, "description"), 2000);
                t.activeForm = Clip(Json.GetString(d, "activeForm"), 120);
                t.owner = Clip(Json.GetString(d, "owner"), 80);
                t.agentId = Json.GetString(d, "agentId");
                string source = Json.GetString(d, "source");
                t.source = source == "goal" || source == "agent" || source == "game" ? source : "claude";
                t.parent = Clip(Json.GetString(d, "parent"), 120);
                string result = Json.GetString(d, "result");
                t.result = status == "done" && (result == "failed" || result == "interrupted") ? result : null;
                ReadDetails(t, d);
                t.blockedBy.Clear();
                var blockers = Json.GetList(d, "blockedBy");
                if (blockers != null)
                    foreach (var b in blockers)
                    {
                        string bid = IdString(b);
                        if (!string.IsNullOrEmpty(bid) && !t.blockedBy.Contains(bid)) t.blockedBy.Add(bid);
                    }
                fresh.Add(t);
            }
            fresh.Sort((a, b) => a.seq.CompareTo(b.seq));
            // "↳ for: <goal>" on subagent cards: the parent's subject, from the same snapshot
            Dictionary<string, TaskInfo> freshById = null;
            foreach (var t in fresh)
            {
                if (string.IsNullOrEmpty(t.parent)) { t.parentSubject = null; continue; }
                if (freshById == null)
                {
                    freshById = new Dictionary<string, TaskInfo>();
                    foreach (var f in fresh) freshById[f.id] = f;
                }
                t.parentSubject = freshById.TryGetValue(t.parent, out var p) ? p.subject : null;
            }

            tasksBySession[session] = fresh;
            if (!taskSessionOrder.Contains(session)) taskSessionOrder.Add(session);
            RebuildTasks();
        }

        /// <summary>Most files read per card (the plugin sends at most 20).</summary>
        private const int MaxChangesPerCard = 50;
        /// <summary>Diff text kept per file (the plugin caps it at 12 000 chars).</summary>
        private const int MaxDiffChars = 20000;

        /// <summary>
        /// Task details (v0.3.1): timestamps, resultText and changes. Paths and text go through
        /// Privacy.Path (the plugin scrubs them already; this guards older plugins). Unchanged changes
        /// keep their objects (and their parsed diffs); detailVersion bumps when anything differs.
        /// </summary>
        private static void ReadDetails(TaskInfo t, Dictionary<string, object> d)
        {
            t.createdAt = Json.GetLong(d, "createdAt");
            t.startedAt = Json.GetLong(d, "startedAt");
            t.doneAt = t.status == "done" ? Json.GetLong(d, "doneAt") : 0;
            bool changed = false;

            string result = Privacy.Path(ClipKeepLines(Json.GetString(d, "resultText"), 4100));
            if (result != t.resultText) { t.resultText = result; changed = true; }

            int omitted = (int)Math.Max(0, Math.Min(100000, Json.GetLong(d, "changesOmitted")));
            if (omitted != t.changesOmitted) { t.changesOmitted = omitted; changed = true; }

            var fresh = new List<TaskChange>();
            var list = Json.GetList(d, "changes");
            if (list != null)
                foreach (var o in list)
                {
                    if (fresh.Count >= MaxChangesPerCard) break;
                    var cd = o as Dictionary<string, object>;
                    if (cd == null) continue;
                    string path = Privacy.Path(Clip(Json.GetString(cd, "path"), 300));
                    if (path == null) continue;
                    string diff = Json.GetString(cd, "diff");
                    if (diff != null && diff.Length > MaxDiffChars) diff = diff.Substring(0, MaxDiffChars);
                    var c = new TaskChange
                    {
                        path = path,
                        additions = (int)Math.Max(0, Math.Min(int.MaxValue, Json.GetLong(cd, "additions"))),
                        deletions = (int)Math.Max(0, Math.Min(int.MaxValue, Json.GetLong(cd, "deletions"))),
                        diff = string.IsNullOrEmpty(diff) ? null : Privacy.Path(diff),
                        truncated = Json.GetBool(cd, "truncated"),
                    };
                    var via = Json.GetList(cd, "via");
                    if (via != null)
                        foreach (var v in via)
                            if (v is string vs && vs.Length > 0 && c.via.Count < 10) c.via.Add(vs);
                    fresh.Add(c);
                }
            if (fresh.Count != t.changes.Count) changed = true;
            for (int i = 0; i < fresh.Count; i++)
            {
                // keep the old object (and its parsed diff) when nothing about the file changed
                var old = t.changes.Find(x => x.path == fresh[i].path);
                if (old != null && old.SameAs(fresh[i])) fresh[i] = old;
                else changed = true;
                if (!changed && !ReferenceEquals(t.changes[i], fresh[i])) changed = true;
            }
            t.changes = fresh;
            if (changed) t.detailVersion++;
        }

        /// <summary>Trimmed and clipped like Clip, keeping newlines; null when empty.</summary>
        private static string ClipKeepLines(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Replace("\r", "").Trim();
            if (s.Length == 0) return null;
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        /// <summary>session.start: the session is back, keep its tasks (its snapshot follows).</summary>
        private void KeepSessionTasks(string session)
        {
            taskExpiry.Remove(session);
        }

        /// <summary>session.end: the session's tasks stay visible for one in-game day.</summary>
        private void ExpireSessionTasksLater(string session)
        {
            if (!tasksBySession.ContainsKey(session)) return;
            int now = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            taskExpiry[session] = now + TaskExpiryTicks;
        }

        /// <summary>Drops the tasks of sessions that ended a day ago (called from the tick upkeep).</summary>
        private void ExpireTasks(int now)
        {
            if (taskExpiry.Count == 0) return;
            List<string> gone = null;
            foreach (var kv in taskExpiry)
                if (now >= kv.Value) (gone ?? (gone = new List<string>())).Add(kv.Key);
            if (gone == null) return;
            foreach (var s in gone)
            {
                taskExpiry.Remove(s);
                tasksBySession.Remove(s);
                taskSessionOrder.Remove(s);
            }
            RebuildTasks();
        }

        private void RebuildTasks()
        {
            allTasks.Clear();
            foreach (var s in taskSessionOrder)
            {
                List<TaskInfo> l;
                if (tasksBySession.TryGetValue(s, out l)) allTasks.AddRange(l);
            }
            if (pendingTasks.Count > 0)
            {
                // A waiting card leaves once a session's snapshot carries the plugin's card for it.
                tmpTaskIds.Clear();
                foreach (var t in allTasks) tmpTaskIds.Add(t.id);
                pendingTasks.RemoveAll(p => tmpTaskIds.Contains(p.id));
                allTasks.AddRange(pendingTasks);
            }
            var handlers = TasksChanged;
            if (handlers == null) return;
            foreach (Action h in handlers.GetInvocationList())
            {
                try { h(); }
                catch (Exception e) { Log.Error("[ColonyCode] TasksChanged handler failed: " + e); }
            }
        }

        /// <summary>
        /// task.create (protocol v0.2): asks Claude Code to work on a task. The game shows a waiting
        /// "game-&lt;inbox id&gt;" card at once; the plugin's card with the same id replaces it once a
        /// session takes the item. Returns false when nothing was sent.
        /// </summary>
        public bool CreateTask(string text, string sessionId = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            if (t.Length > 2000) t = t.Substring(0, 2000);
            string target = NullIfEmpty(sessionId);
            string inboxId = Core.Bridge.EnqueueInbox("task.create", new Dictionary<string, object> { { "text", t } }, target);
            if (inboxId == null)
            {
                Messages.Message("ColonyCodeW_BridgeNotRunning".Translate(), MessageTypeDefOf.RejectInput, false);
                return false;
            }
            int nl = t.IndexOf('\n');
            pendingTasks.Add(new TaskInfo
            {
                id = "game-" + inboxId,
                sessionId = target,
                subject = nl < 0 ? t : t.Substring(0, nl),
                description = nl < 0 ? null : t,
                status = "todo",
                source = "game",
                pending = true,
                seq = nextTaskSeq++,
            });
            RebuildTasks();
            return true;
        }

        /// <summary>True when at least one Claude Code session is connected (one can take inbox items).</summary>
        public bool AnySessionConnected()
        {
            foreach (var s in sessions)
                if (s.connected) return true;
            return false;
        }

        /// <summary>Counts for the board building's inspect string: to do, in progress, done.</summary>
        public void CountTasks(out int todo, out int doing, out int done)
        {
            todo = doing = done = 0;
            foreach (var t in allTasks)
            {
                if (t.status == "done") done++;
                else if (t.status == "doing") doing++;
                else todo++;
            }
        }

        private static string IdString(object v)
        {
            if (v == null) return null;
            if (v is string s) return s;
            if (v is double d) return d.ToString("0.###", CultureInfo.InvariantCulture);
            if (v is IConvertible c)
            {
                try { return c.ToString(CultureInfo.InvariantCulture); }
                catch { return null; }
            }
            return null;
        }

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            if (s.Length == 0) return null;
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }
    }
}
