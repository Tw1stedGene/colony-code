using System;
using System.Collections.Generic;
using System.Globalization;
using ColonyCode.Http;
using RimWorld;
using Verse;

namespace ColonyCode.Agents
{
    /// <summary>One git worktree of a session (worktree.list).</summary>
    public class WorktreeInfo
    {
        /// <summary>The worktree folder name; what diff.request / merge.request send as `worktree`.</summary>
        public string id;
        public string path;
        public string branch;
        public int ahead;
        public int files;
        public int additions;
        public int deletions;
        public string agentId;
    }

    public class DiffLine
    {
        /// <summary>"add" | "del" | "ctx"</summary>
        public string kind;
        public string text;
        /// <summary>Line numbers, 0 when absent.</summary>
        public int oldNo;
        public int newNo;
    }

    public class DiffHunk
    {
        public string header;
        public List<DiffLine> lines = new List<DiffLine>();
    }

    public class DiffFile
    {
        public string path;
        /// <summary>"added" | "modified" | "deleted" | "renamed"</summary>
        public string status;
        public List<DiffHunk> hunks = new List<DiffHunk>();
        public int additions;
        public int deletions;
    }

    /// <summary>A diff.request the game sent, and its diff.result once it arrived.</summary>
    public class DiffRequest
    {
        public string requestId;
        public string sessionId;
        /// <summary>Worktree id, or null for the main checkout's uncommitted changes.</summary>
        public string worktree;
        /// <summary>Epoch ms when it was sent.</summary>
        public long sentAt;
        public bool received;
        public List<DiffFile> files = new List<DiffFile>();
        public bool truncated;
        public string error;
    }

    /// <summary>A merge.request the game sent, and its merge.result once it arrived.</summary>
    public class MergeRequest
    {
        public string requestId;
        public string sessionId;
        public string worktree;
        /// <summary>"merge" | "request_changes" | "reject"</summary>
        public string action;
        public long sentAt;
        public bool received;
        /// <summary>Given up on: no merge.result within MergeTimeoutMs, or the session (re)started or ended since.</summary>
        public bool abandoned;
        public bool ok;
        public string message;
    }

    /// <summary>Review half of the registry: worktrees, diffs, merges (protocol v0.2). Not saved.</summary>
    public partial class AgentRegistry
    {
        /// <summary>A worktree.list arrived for the session.</summary>
        public static event Action<SessionInfo> WorktreesChanged;
        /// <summary>A diff.result arrived for a request the game made.</summary>
        public static event Action<DiffRequest> DiffReceived;
        /// <summary>A merge.result arrived (also for results the game did not ask for).</summary>
        public static event Action<MergeRequest> MergeResultReceived;

        public const int MaxDiffRequests = 20;
        public const int MaxMergeRequests = 50;

        private readonly List<DiffRequest> diffRequests = new List<DiffRequest>();
        private readonly List<MergeRequest> mergeRequests = new List<MergeRequest>();
        private static int requestCounter;

        public DiffRequest GetDiffRequest(string requestId)
        {
            if (requestId == null) return null;
            return diffRequests.Find(r => r.requestId == requestId);
        }

        public MergeRequest GetMergeRequest(string requestId)
        {
            if (requestId == null) return null;
            return mergeRequests.Find(r => r.requestId == requestId);
        }

        /// <summary>A merge.request with no merge.result after this long stops blocking the review buttons.</summary>
        public const long MergeTimeoutMs = 180000;

        /// <summary>The newest pending (unanswered, not timed out, not abandoned) merge request for that worktree, or null.</summary>
        public MergeRequest PendingMerge(string sessionId, string worktree)
        {
            long now = Json.NowMs();
            for (int i = mergeRequests.Count - 1; i >= 0; i--)
            {
                var m = mergeRequests[i];
                if (m.received || m.sessionId != sessionId || m.worktree != worktree) continue;
                if (m.abandoned || now - m.sentAt > MergeTimeoutMs) return null;
                return m;
            }
            return null;
        }

        /// <summary>The newest request for that worktree if it went unanswered (timed out or abandoned), else null.</summary>
        public MergeRequest UnansweredMerge(string sessionId, string worktree)
        {
            long now = Json.NowMs();
            for (int i = mergeRequests.Count - 1; i >= 0; i--)
            {
                var m = mergeRequests[i];
                if (m.sessionId != sessionId || m.worktree != worktree) continue;
                return !m.received && (m.abandoned || now - m.sentAt > MergeTimeoutMs) ? m : null;
            }
            return null;
        }

        /// <summary>A session (re)started or ended: whatever it was asked before will not be answered.</summary>
        private void AbandonMerges(string sessionId)
        {
            foreach (var m in mergeRequests)
                if (!m.received && m.sessionId == sessionId) m.abandoned = true;
        }

        private static string NewRequestId(string prefix)
        {
            return prefix + "-" + Json.NowMs().ToString(CultureInfo.InvariantCulture) + "-" + (++requestCounter).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Sends diff.request. worktree null = main checkout's uncommitted changes. Returns the request (null if the bridge is down).</summary>
        public DiffRequest RequestDiff(string sessionId, string worktree)
        {
            var r = new DiffRequest
            {
                requestId = NewRequestId("diff"),
                sessionId = NullIfEmpty(sessionId),
                worktree = NullIfEmpty(worktree),
                sentAt = Json.NowMs(),
            };
            var payload = new Dictionary<string, object> { { "requestId", r.requestId } };
            if (r.worktree != null) payload["worktree"] = r.worktree;
            if (Core.Bridge.EnqueueInbox("diff.request", payload, r.sessionId) == null)
            {
                Messages.Message("ColonyCodeW_BridgeNotRunning".Translate(), MessageTypeDefOf.RejectInput, false);
                return null;
            }
            diffRequests.Add(r);
            if (diffRequests.Count > MaxDiffRequests) diffRequests.RemoveAt(0);
            return r;
        }

        /// <summary>
        /// Sends merge.request. action: "merge" | "request_changes" | "reject" ("reject" only after the
        /// player confirmed). Returns the request (null if the bridge is down or the input is invalid).
        /// </summary>
        public MergeRequest RequestMerge(string sessionId, string worktree, string action, string text = null)
        {
            if (string.IsNullOrEmpty(worktree)) return null;
            if (action != "merge" && action != "request_changes" && action != "reject") return null;
            var m = new MergeRequest
            {
                requestId = NewRequestId("merge"),
                sessionId = NullIfEmpty(sessionId),
                worktree = worktree,
                action = action,
                sentAt = Json.NowMs(),
            };
            var payload = new Dictionary<string, object>
            {
                { "requestId", m.requestId },
                { "worktree", worktree },
                { "action", action },
            };
            if (!string.IsNullOrWhiteSpace(text)) payload["text"] = text.Trim();
            if (Core.Bridge.EnqueueInbox("merge.request", payload, m.sessionId) == null)
            {
                Messages.Message("ColonyCodeW_BridgeNotRunning".Translate(), MessageTypeDefOf.RejectInput, false);
                return null;
            }
            mergeRequests.Add(m);
            if (mergeRequests.Count > MaxMergeRequests) mergeRequests.RemoveAt(0);
            return m;
        }

        private void OnWorktreeList(Dictionary<string, object> ev, string session)
        {
            var s = EnsureSession(session);
            if (s == null) return;
            s.hasWorktreeList = true;
            s.baseBranch = Json.GetString(ev, "base") ?? s.baseBranch;
            s.dirty = Json.GetBool(ev, "dirty");
            s.worktrees.Clear();
            var list = Json.GetList(ev, "worktrees");
            if (list != null)
                foreach (var o in list)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string id = Json.GetString(d, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    s.worktrees.Add(new WorktreeInfo
                    {
                        id = id,
                        path = Json.GetString(d, "path"),
                        branch = Json.GetString(d, "branch") ?? id,
                        ahead = (int)Json.GetLong(d, "ahead"),
                        files = (int)Json.GetLong(d, "files"),
                        additions = (int)Json.GetLong(d, "additions"),
                        deletions = (int)Json.GetLong(d, "deletions"),
                        agentId = Json.GetString(d, "agentId"),
                    });
                    if (s.worktrees.Count >= 100) break;
                }
            var handlers = WorktreesChanged;
            if (handlers == null) return;
            foreach (Action<SessionInfo> h in handlers.GetInvocationList())
            {
                try { h(s); }
                catch (Exception e) { Log.Error("[ColonyCode] WorktreesChanged handler failed: " + e); }
            }
        }

        private void OnDiffResult(Dictionary<string, object> ev, string session)
        {
            var r = GetDiffRequest(Json.GetString(ev, "requestId"));
            if (r == null) return; // not ours, or too old
            r.received = true;
            r.truncated = Json.GetBool(ev, "truncated");
            r.error = Clip(Json.GetString(ev, "error"), 2000);
            r.files.Clear();
            var files = Json.GetList(ev, "files");
            int lineBudget = 6000;
            if (files != null)
                foreach (var fo in files)
                {
                    var fd = fo as Dictionary<string, object>;
                    if (fd == null) continue;
                    var f = new DiffFile
                    {
                        path = Json.GetString(fd, "path") ?? "?",
                        status = Json.GetString(fd, "status") ?? "modified",
                    };
                    var hunks = Json.GetList(fd, "hunks");
                    if (hunks != null)
                        foreach (var ho in hunks)
                        {
                            var hd = ho as Dictionary<string, object>;
                            if (hd == null) continue;
                            var h = new DiffHunk { header = Json.GetString(hd, "header") ?? "@@" };
                            var lines = Json.GetList(hd, "lines");
                            if (lines != null)
                                foreach (var lo in lines)
                                {
                                    var ld = lo as Dictionary<string, object>;
                                    if (ld == null) continue;
                                    if (--lineBudget < 0) { r.truncated = true; break; }
                                    string kind = Json.GetString(ld, "kind");
                                    if (kind != "add" && kind != "del") kind = "ctx";
                                    if (kind == "add") f.additions++;
                                    else if (kind == "del") f.deletions++;
                                    h.lines.Add(new DiffLine
                                    {
                                        kind = kind,
                                        text = Json.GetString(ld, "text") ?? "",
                                        oldNo = (int)Json.GetLong(ld, "oldNo"),
                                        newNo = (int)Json.GetLong(ld, "newNo"),
                                    });
                                }
                            f.hunks.Add(h);
                        }
                    r.files.Add(f);
                    if (r.files.Count >= 300) { r.truncated = true; break; }
                }
            var handlers = DiffReceived;
            if (handlers == null) return;
            foreach (Action<DiffRequest> h in handlers.GetInvocationList())
            {
                try { h(r); }
                catch (Exception e) { Log.Error("[ColonyCode] DiffReceived handler failed: " + e); }
            }
        }

        private void OnMergeResult(Dictionary<string, object> ev, string session)
        {
            string requestId = Json.GetString(ev, "requestId");
            var m = GetMergeRequest(requestId);
            if (m == null)
            {
                m = new MergeRequest
                {
                    requestId = requestId,
                    sessionId = session,
                    worktree = Json.GetString(ev, "worktree"),
                    action = Json.GetString(ev, "action"),
                    sentAt = Json.NowMs(),
                };
                mergeRequests.Add(m);
                if (mergeRequests.Count > MaxMergeRequests) mergeRequests.RemoveAt(0);
            }
            else if (m.received) return; // duplicate
            m.received = true;
            m.ok = Json.GetBool(ev, "ok");
            m.message = Clip(Json.GetString(ev, "message"), 3000) ?? "";
            var handlers = MergeResultReceived;
            if (handlers == null) return;
            foreach (Action<MergeRequest> h in handlers.GetInvocationList())
            {
                try { h(m); }
                catch (Exception e) { Log.Error("[ColonyCode] MergeResultReceived handler failed: " + e); }
            }
        }
    }
}
