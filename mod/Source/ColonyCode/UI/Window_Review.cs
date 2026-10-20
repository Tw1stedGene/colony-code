using System.Collections.Generic;
using ColonyCode.Agents;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyCode.UI
{
    /// <summary>
    /// Review window for one session (protocol v0.2): worktrees on the left (plus the main checkout's
    /// uncommitted changes), the selected item's diff on the right, and Merge / Request changes /
    /// Reject for worktrees.
    /// </summary>
    public class Window_Review : Window
    {
        private const float ListWidth = 300f;
        private const float ItemHeight = 50f;
        private const float RowH = 18f;
        private const float NumW = 42f;
        private const int KeepContext = 3;
        /// <summary>After this long without diff.result, a hint is shown.</summary>
        private const long SlowMs = 15000;

        private enum RowKind { File, Hunk, Line, Collapsed }

        private struct Row
        {
            public RowKind kind;
            public string text;
            public string kindCode; // add / del / ctx for lines
            public int oldNo, newNo;
            public string oldStr, newStr; // cached line-number labels (null when absent)
            public string expandKey; // for collapsed rows
            public string tip;
        }

        private string sessionId;
        /// <summary>Selected worktree id; null = main checkout.</summary>
        private string selected;
        private readonly Dictionary<string, string> requestIds = new Dictionary<string, string>();
        private readonly HashSet<string> autoRequested = new HashSet<string>();
        private readonly HashSet<string> expanded = new HashSet<string>();
        private readonly List<Row> rows = new List<Row>();
        private readonly List<KeyValuePair<string, int>> fileRows = new List<KeyValuePair<string, int>>();
        private string lastMergeId;

        // Row cache key.
        private DiffRequest cachedReq;
        private bool cachedReceived;
        private int cachedExpandCount = -1;

        private Vector2 listScroll, diffScroll, fileScroll;

        public Window_Review(string sessionId)
        {
            this.sessionId = sessionId;
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            draggable = true;
            resizeable = true;
            preventCameraMotion = false;
            onlyOneOfTypeAllowed = true;
            forcePause = false;
        }

        // Window does not clamp to the screen: keep the close X and Reject reachable at 1024 wide / UI scale
        public override Vector2 InitialSize => new Vector2(Mathf.Min(1100f, Verse.UI.screenWidth - 20f), Mathf.Min(700f, Verse.UI.screenHeight - 40f));

        public void SelectSession(string id)
        {
            if (id == sessionId) return;
            sessionId = id;
            selected = null;
            diffScroll = Vector2.zero;
        }

        private static string Key(string session, string worktree) => (session ?? "") + "|" + (worktree ?? "");

        private SessionInfo ResolveSession(AgentRegistry reg)
        {
            var s = reg.GetSession(sessionId);
            if (s != null) return s;
            // Fallback: the first connected session, else the first known.
            foreach (var x in reg.Sessions) if (x.connected) { sessionId = x.id; return x; }
            if (reg.Sessions.Count > 0) { sessionId = reg.Sessions[0].id; return reg.Sessions[0]; }
            return null;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var reg = UIUtil.Registry;
            float y = inRect.y;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, y, 300f, 34f), "ColonyCode_ReviewTitle".Translate());
            Text.Font = GameFont.Small;
            if (reg == null)
            {
                Widgets.Label(new Rect(inRect.x, y + 40f, inRect.width, 30f), "ColonyCode_NoRegistry".Translate());
                return;
            }
            var s = ResolveSession(reg);
            if (reg.Sessions.Count > 1 || SessionPicker.Worthwhile())
                SessionPicker.Button(new Rect(inRect.xMax - 300f, y + 2f, 260f, 28f), sessionId, SelectSession, false);
            y += 38f;
            if (s == null)
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 30f), "ColonyCode_NoSessions".Translate());
                GUI.color = Color.white;
                return;
            }

            CheckMergeResult(reg);

            // Session line: label, base branch, dirty.
            GUI.color = UIUtil.ColMuted;
            string line = "ColonyCode_ReviewSessionLine".Translate(UIUtil.SessionLabel(s.id), UIUtil.Safe(s.baseBranch ?? s.branch ?? "?"));
            if (!s.connected) line += "  " + "ColonyCode_SessionEndedTag".Translate();
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 22f), line);
            GUI.color = Color.white;
            y += 24f;

            if (selected != null && s.worktrees.Find(w => w.id == selected) == null) selected = null;

            var listRect = new Rect(inRect.x, y, ListWidth, inRect.yMax - y);
            var right = new Rect(listRect.xMax + 8f, y, inRect.width - ListWidth - 8f, inRect.yMax - y);
            DrawList(listRect, reg, s);
            DrawRight(right, reg, s);
        }

        // ------------------------------------------------------------------ list

        private void DrawList(Rect rect, AgentRegistry reg, SessionInfo s)
        {
            Widgets.DrawMenuSection(rect);
            var outRect = rect.ContractedBy(4f);
            int n = 1 + s.worktrees.Count;
            float extra = s.hasWorktreeList ? 0f : 40f;
            var view = new Rect(0f, 0f, outRect.width - 16f, n * ItemHeight + extra);
            Widgets.BeginScrollView(outRect, ref listScroll, view);
            // Main checkout.
            var main = new Rect(0f, 0f, view.width, ItemHeight - 2f);
            DrawItem(main, selected == null, "ColonyCode_ReviewMain".Translate(),
                s.dirty ? "ColonyCode_ReviewMainDirty".Translate().ToString() : (s.hasWorktreeList ? "ColonyCode_ReviewMainClean".Translate().ToString() : ""),
                s.dirty ? new Color(1f, 0.8f, 0.3f) : UIUtil.ColMuted);
            if (Widgets.ButtonInvisible(main)) Select(null);
            for (int i = 0; i < s.worktrees.Count; i++)
            {
                var w = s.worktrees[i];
                var r = new Rect(0f, (i + 1) * ItemHeight, view.width, ItemHeight - 2f);
                string sub = "ColonyCode_ReviewWorktreeStats".Translate(w.ahead, w.files, w.additions, w.deletions);
                if (!string.IsNullOrEmpty(w.agentId)) sub += "  · " + UIUtil.Safe(UIUtil.AgentName(w.agentId));
                DrawItem(r, selected == w.id, UIUtil.Safe(w.branch ?? w.id), sub, UIUtil.ColMuted);
                TooltipHandler.TipRegion(r, UIUtil.Safe((w.branch ?? "") + "\n" + (w.path ?? w.id)));
                if (Widgets.ButtonInvisible(r)) Select(w.id);
            }
            if (!s.hasWorktreeList)
            {
                GUI.color = UIUtil.ColMuted;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(4f, n * ItemHeight, view.width - 8f, 40f), "ColonyCode_ReviewNoList".Translate());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
            Widgets.EndScrollView();
        }

        private static void DrawItem(Rect r, bool isSelected, string title, string sub, Color subColor)
        {
            if (isSelected) Widgets.DrawHighlightSelected(r);
            else Widgets.DrawHighlightIfMouseover(r);
            var oldWrap = Text.WordWrap;
            Text.WordWrap = false;
            Widgets.Label(new Rect(r.x + 6f, r.y + 4f, r.width - 12f, 22f), title);
            Text.Font = GameFont.Tiny;
            GUI.color = subColor;
            Widgets.Label(new Rect(r.x + 6f, r.y + 26f, r.width - 12f, 18f), sub);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.WordWrap = oldWrap;
        }

        private void Select(string worktree)
        {
            if (selected == worktree) return;
            selected = worktree;
            diffScroll = Vector2.zero;
            fileScroll = Vector2.zero;
        }

        // ------------------------------------------------------------------ right pane

        private void DrawRight(Rect rect, AgentRegistry reg, SessionInfo s)
        {
            float y = rect.y;
            var wt = selected == null ? null : s.worktrees.Find(w => w.id == selected);
            string key = Key(s.id, selected);

            // Header + refresh.
            Text.Font = GameFont.Medium;
            var oldWrap = Text.WordWrap;
            Text.WordWrap = false;
            Widgets.Label(new Rect(rect.x, y, rect.width - 110f, 32f), wt == null ? "ColonyCode_ReviewMain".Translate().ToString() : UIUtil.Safe(wt.branch ?? wt.id));
            Text.WordWrap = oldWrap;
            Text.Font = GameFont.Small;
            string reqId;
            requestIds.TryGetValue(key, out reqId);
            var req = reg.GetDiffRequest(reqId);
            if (Widgets.ButtonText(new Rect(rect.xMax - 100f, y + 2f, 100f, 28f), "ColonyCode_ReviewRefresh".Translate()))
                req = Request(reg, s.id, selected, key);
            y += 36f;

            // Actions for worktrees.
            if (wt != null)
            {
                y = DrawActions(new Rect(rect.x, y, rect.width, 32f), reg, s, wt) + 6f;
            }

            if (req == null && !autoRequested.Contains(key))
            {
                autoRequested.Add(key);
                req = Request(reg, s.id, selected, key);
            }

            var pane = new Rect(rect.x, y, rect.width, rect.yMax - y);
            DrawDiffPane(pane, req);
        }

        private DiffRequest Request(AgentRegistry reg, string session, string worktree, string key)
        {
            autoRequested.Add(key);
            var r = reg.RequestDiff(session, worktree);
            if (r != null) requestIds[key] = r.requestId;
            return r;
        }

        private float DrawActions(Rect r, AgentRegistry reg, SessionInfo s, WorktreeInfo wt)
        {
            var pending = reg.PendingMerge(s.id, wt.id);
            bool busy = pending != null;
            float x = r.x;

            // Merge.
            bool canMerge = !busy && !s.dirty && s.connected;
            var mr = new Rect(x, r.y, 120f, r.height);
            if (Widgets.ButtonText(mr, "ColonyCode_ReviewMerge".Translate(), true, true, canMerge ? UIUtil.ColGood : Color.white, canMerge) && canMerge)
                SendMerge(reg, s, wt, "merge", null);
            string mergeTip = s.dirty ? "ColonyCode_ReviewMergeDirty".Translate().ToString()
                : !s.connected ? "ColonyCode_ReviewSessionEnded".Translate().ToString()
                : busy ? "ColonyCode_ReviewBusy".Translate().ToString()
                : "ColonyCode_ReviewMergeTip".Translate(UIUtil.Safe(wt.branch ?? wt.id), UIUtil.Safe(s.baseBranch ?? "?")).ToString();
            TooltipHandler.TipRegion(mr, mergeTip);
            x += 128f;

            // Request changes.
            bool canAct = !busy && s.connected;
            var cr = new Rect(x, r.y, 170f, r.height);
            if (Widgets.ButtonText(cr, "ColonyCode_ReviewRequestChanges".Translate(), true, true, canAct) && canAct)
            {
                string sid = s.id, wid = wt.id, branch = wt.branch ?? wt.id;
                Find.WindowStack.Add(new Dialog_TextInput(
                    "ColonyCode_ReviewRequestChangesTitle".Translate(UIUtil.Safe(branch)),
                    "ColonyCode_ReviewRequestChangesPrompt".Translate(),
                    "ColonyCode_Send".Translate(), false, sid,
                    (text, _) =>
                    {
                        var rg = UIUtil.Registry;
                        var ss = rg?.GetSession(sid);
                        var ww = ss?.worktrees.Find(w => w.id == wid);
                        if (rg != null && ss != null) SendMerge(rg, ss, ww ?? new WorktreeInfo { id = wid, branch = branch }, "request_changes", text);
                    }));
            }
            if (!canAct) TooltipHandler.TipRegion(cr, busy ? "ColonyCode_ReviewBusy".Translate() : "ColonyCode_ReviewSessionEnded".Translate());
            x += 178f;

            // Reject (destructive, confirmed).
            var rr = new Rect(x, r.y, 110f, r.height);
            if (Widgets.ButtonText(rr, "ColonyCode_ReviewReject".Translate(), true, true, canAct ? UIUtil.ColBad : Color.white, canAct) && canAct)
            {
                string sid = s.id, wid = wt.id, branch = wt.branch ?? wt.id;
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "ColonyCode_ReviewRejectConfirm".Translate(UIUtil.Safe(branch), UIUtil.Safe(wt.path ?? wid)),
                    () =>
                    {
                        var rg = UIUtil.Registry;
                        var ss = rg?.GetSession(sid);
                        var ww = ss?.worktrees.Find(w => w.id == wid);
                        if (rg != null && ss != null) SendMerge(rg, ss, ww ?? new WorktreeInfo { id = wid, branch = branch }, "reject", null);
                    },
                    true, "ColonyCode_ReviewRejectTitle".Translate()));
            }
            TooltipHandler.TipRegion(rr, canAct ? "ColonyCode_ReviewRejectTip".Translate().ToString()
                : (busy ? "ColonyCode_ReviewBusy".Translate().ToString() : "ColonyCode_ReviewSessionEnded".Translate().ToString()));
            x += 118f;

            if (busy)
            {
                GUI.color = UIUtil.ColMuted;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(x, r.y, r.xMax - x, r.height), "ColonyCode_ReviewPending".Translate(("ColonyCode_ReviewAction_" + pending.action).Translate()));
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }
            else if (reg.UnansweredMerge(s.id, wt.id) != null)
            {
                GUI.color = new Color(1f, 0.8f, 0.3f);
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(x, r.y, r.xMax - x, r.height), "ColonyCode_ReviewNoAnswer".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
            else if (s.dirty)
            {
                GUI.color = new Color(1f, 0.8f, 0.3f);
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(x, r.y, r.xMax - x, r.height), "ColonyCode_ReviewMergeDirtyShort".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
            return r.yMax;
        }

        private void SendMerge(AgentRegistry reg, SessionInfo s, WorktreeInfo wt, string action, string text)
        {
            var m = reg.RequestMerge(s.id, wt.id, action, text);
            if (m == null) return;
            lastMergeId = m.requestId;
            UIUtil.Toast("ColonyCode_ReviewSent".Translate(("ColonyCode_ReviewAction_" + action).Translate(), UIUtil.Safe(wt.branch ?? wt.id)));
        }

        /// <summary>After our merge/reject finished, cached diffs of the session are stale: request them again.</summary>
        private void CheckMergeResult(AgentRegistry reg)
        {
            if (lastMergeId == null) return;
            var m = reg.GetMergeRequest(lastMergeId);
            if (m == null) { lastMergeId = null; return; }
            if (!m.received) return;
            lastMergeId = null;
            if (!m.ok || m.action == "request_changes") return;
            string prefix = (m.sessionId ?? "") + "|";
            var keys = new List<string>(requestIds.Keys);
            foreach (var k in keys) if (k.StartsWith(prefix)) { requestIds.Remove(k); autoRequested.Remove(k); }
        }

        // ------------------------------------------------------------------ diff

        private void DrawDiffPane(Rect rect, DiffRequest req)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.06f, 0.06f, 0.07f, 0.9f));
            var inner = rect.ContractedBy(4f);
            if (req == null)
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(inner, Core.Bridge.IsRunning ? "ColonyCode_ReviewPressRefresh".Translate() : "ColonyCode_BridgeDown".Translate());
                GUI.color = Color.white;
                return;
            }
            if (!req.received)
            {
                GUI.color = UIUtil.ColMuted;
                string msg = "ColonyCode_ReviewLoading".Translate();
                if (ColonyCode.Http.Json.NowMs() - req.sentAt > SlowMs) msg += "\n\n" + "ColonyCode_ReviewSlow".Translate();
                Widgets.Label(inner, msg);
                GUI.color = Color.white;
                return;
            }
            float y = inner.y;
            if (!string.IsNullOrEmpty(req.error))
            {
                GUI.color = UIUtil.ColBad;
                float eh = Mathf.Min(80f, Text.CalcHeight(UIUtil.Safe(req.error), inner.width));
                Widgets.Label(new Rect(inner.x, y, inner.width, eh), UIUtil.Safe(req.error));
                GUI.color = Color.white;
                y += eh + 4f;
            }
            if (req.truncated)
            {
                var tr = new Rect(inner.x, y, inner.width, 22f);
                Widgets.DrawBoxSolid(tr, new Color(0.35f, 0.28f, 0.08f, 0.9f));
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(tr.ContractedBy(6f, 0f), "ColonyCode_ReviewTruncated".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                y += 26f;
            }
            if (req.files.Count == 0)
            {
                if (string.IsNullOrEmpty(req.error))
                {
                    GUI.color = UIUtil.ColMuted;
                    Widgets.Label(new Rect(inner.x, y, inner.width, 30f), "ColonyCode_ReviewNoChanges".Translate());
                    GUI.color = Color.white;
                }
                return;
            }

            Rebuild(req);

            // File list.
            float listH = Mathf.Min(req.files.Count * 20f + 22f, 130f);
            var listRect = new Rect(inner.x, y, inner.width, listH);
            DrawFileList(listRect, req);
            y += listH + 4f;

            DrawRows(new Rect(inner.x, y, inner.width, inner.yMax - y));
        }

        private void DrawFileList(Rect rect, DiffRequest req)
        {
            Text.Font = GameFont.Tiny;
            GUI.color = UIUtil.ColMuted;
            int add = 0, del = 0;
            foreach (var f in req.files) { add += f.additions; del += f.deletions; }
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 20f), "ColonyCode_ReviewFiles".Translate(req.files.Count, add, del));
            GUI.color = Color.white;
            var outRect = new Rect(rect.x, rect.y + 20f, rect.width, rect.height - 20f);
            var view = new Rect(0f, 0f, outRect.width - 16f, req.files.Count * 20f);
            Widgets.BeginScrollView(outRect, ref fileScroll, view);
            var oldWrap = Text.WordWrap;
            Text.WordWrap = false;
            for (int i = 0; i < req.files.Count; i++)
            {
                var f = req.files[i];
                var r = new Rect(0f, i * 20f, view.width, 20f);
                Widgets.DrawHighlightIfMouseover(r);
                GUI.color = StatusColor(f.status);
                Widgets.Label(new Rect(r.x + 2f, r.y, 70f, r.height), StatusLabel(f.status));
                GUI.color = UIUtil.ColDiffFile;
                Widgets.Label(new Rect(r.x + 74f, r.y, r.width - 170f, r.height), UIUtil.Safe(f.path));
                GUI.color = UIUtil.ColDiffAdd;
                Widgets.Label(new Rect(r.xMax - 92f, r.y, 46f, r.height), "+" + f.additions);
                GUI.color = UIUtil.ColDiffDel;
                Widgets.Label(new Rect(r.xMax - 46f, r.y, 46f, r.height), "-" + f.deletions);
                GUI.color = Color.white;
                if (Widgets.ButtonInvisible(r))
                {
                    foreach (var kv in fileRows)
                        if (kv.Key == f.path) { diffScroll.y = kv.Value * RowH; break; }
                }
            }
            Text.WordWrap = oldWrap;
            Widgets.EndScrollView();
            Text.Font = GameFont.Small;
        }

        private static Color StatusColor(string status)
        {
            switch (status)
            {
                case "added": return UIUtil.ColDiffAdd;
                case "deleted": return UIUtil.ColDiffDel;
                case "renamed": return UIUtil.ColDiffHunk;
                default: return new Color(1f, 0.8f, 0.3f);
            }
        }

        private static string StatusLabel(string status)
        {
            string k = "ColonyCode_FileStatus_" + (status ?? "modified");
            return k.CanTranslate() ? k.Translate().ToString() : (status ?? "");
        }

        private void Rebuild(DiffRequest req)
        {
            if (ReferenceEquals(req, cachedReq) && req.received == cachedReceived && expanded.Count == cachedExpandCount) return;
            if (!ReferenceEquals(req, cachedReq)) expanded.Clear();
            cachedReq = req;
            cachedReceived = req.received;
            cachedExpandCount = expanded.Count;
            rows.Clear();
            fileRows.Clear();
            for (int fi = 0; fi < req.files.Count; fi++)
            {
                var f = req.files[fi];
                fileRows.Add(new KeyValuePair<string, int>(f.path, rows.Count));
                rows.Add(new Row { kind = RowKind.File, text = StatusLabel(f.status) + "  " + UIUtil.Safe(f.path) + "   +" + f.additions + " -" + f.deletions, tip = UIUtil.Safe(f.path) });
                for (int hi = 0; hi < f.hunks.Count; hi++)
                {
                    var h = f.hunks[hi];
                    rows.Add(new Row { kind = RowKind.Hunk, text = UIUtil.Safe(Clip(h.header)) });
                    AddLines(h, fi + ":" + hi);
                }
            }
            cachedExpandCount = expanded.Count;
        }

        /// <summary>Adds a hunk's lines; long runs of unchanged context are collapsed (click to expand).</summary>
        private void AddLines(DiffHunk h, string hunkKey)
        {
            var lines = h.lines;
            int i = 0;
            while (i < lines.Count)
            {
                if (lines[i].kind != "ctx") { AddLine(lines[i]); i++; continue; }
                int start = i;
                while (i < lines.Count && lines[i].kind == "ctx") i++;
                int end = i; // exclusive
                bool leading = start == 0, trailing = end == lines.Count;
                int keepHead = leading ? 0 : KeepContext;
                int keepTail = trailing ? 0 : KeepContext;
                int len = end - start;
                string key = hunkKey + ":" + start;
                if (len <= keepHead + keepTail + 2 || expanded.Contains(key))
                {
                    for (int k = start; k < end; k++) AddLine(lines[k]);
                    continue;
                }
                for (int k = start; k < start + keepHead; k++) AddLine(lines[k]);
                int hidden = len - keepHead - keepTail;
                rows.Add(new Row { kind = RowKind.Collapsed, text = "ColonyCode_ReviewCollapsed".Translate(hidden), expandKey = key });
                for (int k = end - keepTail; k < end; k++) AddLine(lines[k]);
            }
        }

        private void AddLine(DiffLine l)
        {
            string text = l.text ?? "";
            char sign = l.kind == "add" ? '+' : l.kind == "del" ? '-' : ' ';
            rows.Add(new Row
            {
                kind = RowKind.Line,
                kindCode = l.kind,
                // display text cached with its +/-/space prefix: DrawRows allocates nothing per event
                text = sign + " " + UIUtil.Safe(Clip(text)),
                oldNo = l.oldNo,
                newNo = l.newNo,
                oldStr = l.oldNo > 0 ? l.oldNo.ToString() : null,
                newStr = l.newNo > 0 ? l.newNo.ToString() : null,
                tip = text.Length > 160 ? UIUtil.Safe(text.Length > 2000 ? text.Substring(0, 2000) : text) : null,
            });
        }

        private static string Clip(string s) => s.Length > 400 ? s.Substring(0, 400) + "…" : s;

        private static readonly Color AddBg = new Color(0.1f, 0.3f, 0.1f, 0.55f);
        private static readonly Color DelBg = new Color(0.35f, 0.1f, 0.1f, 0.55f);
        private static readonly Color FileBg = new Color(0.2f, 0.2f, 0.22f, 0.95f);

        private void DrawRows(Rect outRect)
        {
            float contentH = rows.Count * RowH;
            var view = new Rect(0f, 0f, outRect.width - 16f, contentH);
            Widgets.BeginScrollView(outRect, ref diffScroll, view);
            var oldFont = Text.Font;
            var oldWrap = Text.WordWrap;
            var oldAnchor = Text.Anchor;
            Text.Font = GameFont.Tiny;
            Text.WordWrap = false;
            Text.Anchor = TextAnchor.MiddleLeft;
            int first = Mathf.Max(0, Mathf.FloorToInt(diffScroll.y / RowH) - 1);
            int last = Mathf.Min(rows.Count - 1, Mathf.CeilToInt((diffScroll.y + outRect.height) / RowH) + 1);
            string expand = null;
            for (int i = first; i <= last; i++)
            {
                var row = rows[i];
                var r = new Rect(0f, i * RowH, view.width, RowH);
                switch (row.kind)
                {
                    case RowKind.File:
                        Widgets.DrawBoxSolid(r, FileBg);
                        GUI.color = UIUtil.ColDiffFile;
                        Widgets.Label(new Rect(r.x + 4f, r.y, r.width - 8f, r.height), row.text);
                        break;
                    case RowKind.Hunk:
                        GUI.color = UIUtil.ColDiffHunk;
                        Widgets.Label(new Rect(r.x + 2f * NumW + 8f, r.y, r.width - 2f * NumW - 8f, r.height), row.text);
                        break;
                    case RowKind.Collapsed:
                        Widgets.DrawHighlightIfMouseover(r);
                        GUI.color = UIUtil.ColMuted;
                        Widgets.Label(new Rect(r.x + 2f * NumW + 8f, r.y, r.width - 2f * NumW - 8f, r.height), row.text);
                        if (Widgets.ButtonInvisible(r)) expand = row.expandKey;
                        break;
                    default:
                        if (row.kindCode == "add") Widgets.DrawBoxSolid(r, AddBg);
                        else if (row.kindCode == "del") Widgets.DrawBoxSolid(r, DelBg);
                        GUI.color = UIUtil.ColMuted;
                        Text.Anchor = TextAnchor.MiddleRight;
                        if (row.oldStr != null) Widgets.Label(new Rect(r.x, r.y, NumW - 4f, r.height), row.oldStr);
                        if (row.newStr != null) Widgets.Label(new Rect(r.x + NumW, r.y, NumW - 4f, r.height), row.newStr);
                        Text.Anchor = TextAnchor.MiddleLeft;
                        GUI.color = row.kindCode == "add" ? UIUtil.ColDiffAdd : row.kindCode == "del" ? UIUtil.ColDiffDel : UIUtil.ColResult;
                        Widgets.Label(new Rect(r.x + 2f * NumW, r.y, r.width - 2f * NumW, r.height), row.text);
                        break;
                }
                if (row.tip != null && Mouse.IsOver(r)) TooltipHandler.TipRegion(r, row.tip);
            }
            GUI.color = Color.white;
            Text.Font = oldFont;
            Text.WordWrap = oldWrap;
            Text.Anchor = oldAnchor;
            Widgets.EndScrollView();
            if (expand != null) expanded.Add(expand);
        }
    }
}
