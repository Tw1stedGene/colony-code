using System.Collections.Generic;
using System.Text;
using ColonyCode.Agents;
using UnityEngine;
using Verse;

namespace ColonyCode.UI
{
    /// <summary>
    /// One task card in full (opened by clicking a card on the task board): subject, source / status /
    /// result tags, who worked on it (Jump, Agent card), the goal a subagent worked for (click opens
    /// it), timestamps, the description, Claude's final answer (light markdown) and the files the
    /// card's turn changed, each with its diff (line numbers, red/green), plus "Open review".
    ///
    /// Every string is built when the task, the board or an agent changes, and every height when the
    /// width changes; a frame only draws.
    /// </summary>
    public class Window_TaskDetail : Window
    {
        private const float FileListWidth = 250f;
        private const float FileRowH = 22f;
        private const float SectionGap = 8f;

        private string sessionId;
        private string taskId;
        private TaskInfo task;
        private TaskInfo parentTask;
        private AgentInfo agent;

        // Rebuilt when the board or an agent changed (or the window shows another card).
        private static int boardVersion;
        private int seenBoardVersion = -1;
        private int seenDetailVersion = -1;
        private TaskInfo seenTask;

        private string subject, statusLine, who, parentLine, timeLine;
        private string sourceTag, resultTag;
        private float sourceTagW, resultTagW;
        private Color sourceTagColor, resultTagColor;
        private string description;   // safe, or null
        private string resultBody;    // rich text, or null
        private string resultNote;    // muted placeholder when there is no result
        private string changesHeader, omittedLine;
        private readonly List<string> fileLabels = new List<string>();
        private readonly List<string> fileCounts = new List<string>();
        private readonly List<string> fileTips = new List<string>();
        private readonly List<string> fileFlags = new List<string>();
        private int selectedFile;
        private readonly List<DiffFile> selectedDiff = new List<DiffFile>(1);
        private readonly DiffRowsView diffView = new DiffRowsView(false);

        // Heights for the cached strings at the width they were measured for.
        private float measuredWidth = -1f;
        private float subjectH, descH, resultH, textPaneH;
        private Vector2 textScroll, fileScroll;

        static Window_TaskDetail()
        {
            AgentRegistry.TasksChanged += () => boardVersion++;
            AgentRegistry.AgentChanged += _ => boardVersion++;
        }

        public Window_TaskDetail(string sessionId, string taskId)
        {
            this.sessionId = sessionId;
            this.taskId = taskId;
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            draggable = true;
            resizeable = true;
            preventCameraMotion = false;
            onlyOneOfTypeAllowed = true;
            forcePause = false;
        }

        // Window does not clamp to the screen: keep the close X reachable at 1024 wide / large UI scale.
        public override Vector2 InitialSize => new Vector2(Mathf.Min(920f, Verse.UI.screenWidth - 20f), Mathf.Min(720f, Verse.UI.screenHeight - 40f));

        /// <summary>Shows another card in this window.</summary>
        public void Show(string session, string id)
        {
            if (session == sessionId && id == taskId) return;
            sessionId = session;
            taskId = id;
            seenBoardVersion = -1;
            textScroll = Vector2.zero;
            fileScroll = Vector2.zero;
            selectedFile = 0;
        }

        // ------------------------------------------------------------------ caches

        private void Refresh()
        {
            if (seenBoardVersion == boardVersion && ReferenceEquals(seenTask, task) && (task == null || seenDetailVersion == task.detailVersion)) return;
            if (seenBoardVersion != boardVersion || task == null || task.id != taskId)
            {
                task = FindTask(sessionId, taskId);
                agent = null;
            }
            bool otherTask = !ReferenceEquals(seenTask, task);
            seenBoardVersion = boardVersion;
            seenTask = task;
            seenDetailVersion = task?.detailVersion ?? -1;
            measuredWidth = -1f;
            if (task == null) return;
            if (otherTask) selectedFile = 0;

            TaskBoardView.EnsureTags();
            subject = UIUtil.SessionPrefix(task.sessionId) + UIUtil.Safe(task.subject);
            TaskBoardView.SourceTag(task, out sourceTag, out sourceTagW, out sourceTagColor);
            if (!TaskBoardView.ResultTag(task, out resultTag, out resultTagW, out resultTagColor)) resultTag = null;
            statusLine = ("ColonyCode_TaskStatus_" + (task.status ?? "todo")).Translate();

            who = null;
            if (TaskBoardView.WhoFor(task, out string w, out AgentInfo a))
            {
                who = UIUtil.Safe(w);
                agent = a;
            }

            parentTask = string.IsNullOrEmpty(task.parent) ? null : FindTask(task.sessionId, task.parent);
            parentLine = string.IsNullOrEmpty(task.parent) ? null
                : "ColonyCode_TaskForGoal".Translate(UIUtil.Safe(UIUtil.OneLine(task.parentSubject ?? task.parent, 90))).ToString();

            timeLine = TimeLine(task);
            description = string.IsNullOrEmpty(task.description) || task.description == task.subject ? null : UIUtil.Safe(task.description);

            resultBody = string.IsNullOrEmpty(task.resultText) ? null : LightMarkdown.Block(task.resultText);
            resultNote = resultBody != null ? null : NoResultNote(task);

            fileLabels.Clear();
            fileCounts.Clear();
            fileTips.Clear();
            fileFlags.Clear();
            int add = 0, del = 0;
            foreach (var c in task.changes)
            {
                add += c.additions;
                del += c.deletions;
                fileLabels.Add(UIUtil.Safe(ShortPath(c.path)));
                fileCounts.Add("+" + c.additions + " -" + c.deletions);
                var tip = new StringBuilder(UIUtil.Safe(c.path));
                string flags = "";
                if (c.via.Count > 0)
                {
                    flags = "ColonyCode_TaskDetailVia".Translate();
                    var names = new List<string>(c.via.Count);
                    foreach (var v in c.via) names.Add(SubagentName(task.sessionId, v));
                    tip.Append("\n").Append("ColonyCode_TaskDetailViaTip".Translate(UIUtil.Safe(string.Join(", ", names.ToArray()))));
                }
                if (c.truncated)
                {
                    flags = flags.Length > 0 ? flags + " · " + "ColonyCode_TaskDetailCut".Translate() : "ColonyCode_TaskDetailCut".Translate().ToString();
                    tip.Append("\n").Append("ColonyCode_TaskDetailCutTip".Translate());
                }
                fileTips.Add(tip.ToString());
                fileFlags.Add(flags);
            }
            int total = task.changes.Count + task.changesOmitted;
            changesHeader = "ColonyCode_TaskDetailChanges".Translate(total, add, del);
            omittedLine = task.changesOmitted > 0 ? "ColonyCode_TaskDetailMoreFiles".Translate(task.changesOmitted).ToString() : null;
            if (selectedFile >= task.changes.Count) selectedFile = 0;
        }

        private static TaskInfo FindTask(string session, string id)
        {
            var reg = UIUtil.Registry;
            if (reg == null || string.IsNullOrEmpty(id)) return null;
            TaskInfo any = null;
            foreach (var t in reg.Tasks)
            {
                if (t == null || t.id != id) continue;
                if (t.sessionId == session) return t;
                if (any == null) any = t;
            }
            return any;
        }

        private static string SubagentName(string session, string cardId)
        {
            var card = FindTask(session, cardId);
            if (card != null) return UIUtil.OneLine(card.subject, 60);
            return cardId;
        }

        private static string TimeLine(TaskInfo t)
        {
            var parts = new List<string>(4);
            if (t.createdAt > 0) parts.Add("ColonyCode_TaskDetailCreated".Translate(UIUtil.FormatTime(t.createdAt)));
            if (t.startedAt > 0) parts.Add("ColonyCode_TaskDetailStarted".Translate(UIUtil.FormatTime(t.startedAt)));
            if (t.status == "done" && t.doneAt > 0)
            {
                parts.Add("ColonyCode_TaskDetailFinished".Translate(UIUtil.FormatTime(t.doneAt)));
                long from = t.startedAt > 0 ? t.startedAt : t.createdAt;
                if (from > 0 && t.doneAt >= from) parts.Add("ColonyCode_TaskDetailTook".Translate(UIUtil.FormatDuration(t.doneAt - from)));
            }
            return parts.Count == 0 ? null : string.Join("  ·  ", parts.ToArray());
        }

        private static string NoResultNote(TaskInfo t)
        {
            if (t.source == "claude" && t.status == "done") return "ColonyCode_TaskDetailClaudeTask".Translate();
            if (t.status == "doing") return "ColonyCode_TaskDetailWorking".Translate();
            if (t.status != "done") return "ColonyCode_TaskDetailNotStarted".Translate();
            if (t.result == "failed") return "ColonyCode_TaskDetailNoResultFailed".Translate();
            if (t.result == "interrupted") return "ColonyCode_TaskDetailNoResultInterrupted".Translate();
            return "ColonyCode_TaskDetailNoResult".Translate();
        }

        private static string ShortPath(string path)
        {
            if (path == null) return "?";
            if (path.Length <= 36) return path;
            int slash = path.LastIndexOf('/');
            string file = slash >= 0 ? path.Substring(slash + 1) : path;
            return "…/" + file;
        }

        /// <summary>Heights of the wrapped texts at this width; measured again only when the width changes.</summary>
        private void Measure(float width, float textWidth)
        {
            if (width == measuredWidth) return;
            measuredWidth = width;
            var oldFont = Text.Font;
            var oldWrap = Text.WordWrap;
            Text.WordWrap = true;
            Text.Font = GameFont.Medium;
            subjectH = Mathf.Min(96f, Mathf.Max(32f, UIUtil.TextHeight(subject, width)));
            Text.Font = GameFont.Small;
            descH = description == null ? 0f : UIUtil.TextHeight(description, textWidth);
            resultH = UIUtil.TextHeight(resultBody ?? resultNote ?? "", textWidth);
            textPaneH = (description == null ? 0f : 24f + descH + SectionGap) + 24f + resultH + 4f;
            Text.Font = oldFont;
            Text.WordWrap = oldWrap;
        }

        // ------------------------------------------------------------------ drawing

        public override void DoWindowContents(Rect inRect)
        {
            Refresh();
            if (task == null)
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(inRect, "ColonyCode_TaskDetailGone".Translate());
                GUI.color = Color.white;
                return;
            }
            float textWidth = inRect.width - 16f - 8f;
            Measure(inRect.width - 30f, textWidth);
            float y = inRect.y;

            // Tags: source, status, result.
            Text.Font = GameFont.Tiny;
            float x = inRect.x;
            TaskBoardView.DrawTag(new Rect(x, y + 1f, sourceTagW, 16f), sourceTag, sourceTagColor);
            x += sourceTagW + 8f;
            var oldWrap = Text.WordWrap;
            Text.WordWrap = false;
            GUI.color = task.status == "done" ? UIUtil.ColGood : task.status == "doing" ? new Color(0.45f, 0.72f, 1f) : UIUtil.ColMuted;
            Widgets.Label(new Rect(x, y, 160f, 18f), statusLine);
            GUI.color = Color.white;
            x += Text.CalcSize(statusLine).x + 8f;
            if (resultTag != null) TaskBoardView.DrawTag(new Rect(x, y + 1f, resultTagW, 16f), resultTag, resultTagColor);
            Text.WordWrap = oldWrap;
            y += 20f;

            // Subject, wrapped.
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, y, inRect.width - 30f, subjectH), subject);
            Text.Font = GameFont.Small;
            y += subjectH + 2f;

            // Who worked on it: state dot, name, Jump, Agent card.
            if (who != null)
            {
                var line = new Rect(inRect.x, y, inRect.width, 26f);
                UIUtil.DrawDot(new Rect(line.x, line.y + 8f, 10f, 10f), agent != null ? UIUtil.StateColor(agent.state) : UIUtil.ColMuted);
                Text.WordWrap = false;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(line.x + 16f, line.y, line.width - 16f - 240f, line.height), who);
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = oldWrap;
                float bx = line.xMax;
                if (agent != null)
                {
                    bx -= 120f;
                    if (Widgets.ButtonText(new Rect(bx, line.y, 120f, 26f), "ColonyCode_TaskDetailAgentCard".Translate()))
                        UIEntry.OpenAgentCard(agent.id);
                    if (UIUtil.CanJump(agent))
                    {
                        bx -= 98f;
                        if (Widgets.ButtonText(new Rect(bx, line.y, 90f, 26f), "ColonyCode_Jump".Translate())) UIUtil.JumpTo(agent);
                    }
                }
                y += 30f;
            }

            // The goal a subagent worked for: click opens it here.
            if (parentLine != null)
            {
                var pr = new Rect(inRect.x, y, inRect.width, 22f);
                Text.WordWrap = false;
                bool canOpen = parentTask != null;
                GUI.color = canOpen && Mouse.IsOver(pr) ? new Color(1f, 0.9f, 0.55f) : UIUtil.ColMuted;
                Widgets.Label(pr, parentLine);
                GUI.color = Color.white;
                Text.WordWrap = oldWrap;
                if (canOpen)
                {
                    Widgets.DrawHighlightIfMouseover(pr);
                    TooltipHandler.TipRegion(pr, "ColonyCode_TaskDetailParentTip".Translate());
                    if (Widgets.ButtonInvisible(pr)) Show(parentTask.sessionId, parentTask.id);
                }
                y += 22f;
            }

            if (timeLine != null)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 18f), timeLine);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                y += 20f;
            }
            y += 4f;

            // Body: description + result on top, changes below; buttons at the bottom.
            float buttonsY = inRect.yMax - 32f;
            float bodyH = buttonsY - 6f - y;
            bool hasChanges = task.changes.Count > 0 || task.changesOmitted > 0;
            float textH = hasChanges ? Mathf.Max(80f, Mathf.Min(textPaneH + 8f, bodyH * 0.45f)) : bodyH;
            DrawTextPane(new Rect(inRect.x, y, inRect.width, textH));
            y += textH + SectionGap;
            DrawChanges(new Rect(inRect.x, y, inRect.width, buttonsY - 6f - y), hasChanges);

            if (!string.IsNullOrEmpty(task.sessionId) && Widgets.ButtonText(new Rect(inRect.x, buttonsY, 150f, 32f), "ColonyCode_TaskDetailOpenReview".Translate()))
                UIEntry.OpenReview(task.sessionId);
            if (Widgets.ButtonText(new Rect(inRect.xMax - 120f, buttonsY, 120f, 32f), "CloseButton".Translate()))
                Close();
        }

        private void DrawTextPane(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.06f, 0.06f, 0.07f, 0.9f));
            var outRect = rect.ContractedBy(4f);
            var view = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(textPaneH, 1f));
            Widgets.BeginScrollView(outRect, ref textScroll, view);
            var oldWrap = Text.WordWrap;
            Text.WordWrap = true;
            float y = 0f;
            float w = measuredWidth > 0f ? Mathf.Min(view.width, outRect.width - 16f) : view.width;
            if (description != null)
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(new Rect(0f, y, w, 22f), "ColonyCode_TaskDetailTask".Translate());
                y += 24f;
                GUI.color = new Color(0.82f, 0.82f, 0.82f);
                Widgets.Label(new Rect(0f, y, w, descH), description);
                y += descH + SectionGap;
            }
            GUI.color = UIUtil.ColMuted;
            Widgets.Label(new Rect(0f, y, w, 22f), "ColonyCode_TaskDetailResult".Translate());
            y += 24f;
            GUI.color = resultBody != null ? UIUtil.ColText : UIUtil.ColMuted;
            Widgets.Label(new Rect(0f, y, w, resultH), resultBody ?? resultNote);
            GUI.color = Color.white;
            Text.WordWrap = oldWrap;
            Widgets.EndScrollView();
        }

        private void DrawChanges(Rect rect, bool hasChanges)
        {
            if (rect.height < 40f) return;
            GUI.color = UIUtil.ColMuted;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 22f), hasChanges ? changesHeader
                : (task.status == "done" ? "ColonyCode_TaskDetailNoChanges" : "ColonyCode_TaskDetailNoChangesYet").Translate().ToString());
            GUI.color = Color.white;
            if (!hasChanges) return;
            float top = rect.y + 24f;
            var listRect = new Rect(rect.x, top, Mathf.Min(FileListWidth, rect.width * 0.4f), rect.yMax - top);
            var diffRect = new Rect(listRect.xMax + 6f, top, rect.width - listRect.width - 6f, rect.yMax - top);
            DrawFileList(listRect);

            Widgets.DrawBoxSolid(diffRect, new Color(0.06f, 0.06f, 0.07f, 0.9f));
            var inner = diffRect.ContractedBy(4f);
            if (selectedFile < 0 || selectedFile >= task.changes.Count)
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(inner, "ColonyCode_TaskDetailPickFile".Translate());
                GUI.color = Color.white;
                return;
            }
            var change = task.changes[selectedFile];
            if (change.diff == null)
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(inner, "ColonyCode_TaskDetailNoDiff".Translate());
                GUI.color = Color.white;
                return;
            }
            if (selectedDiff.Count != 1 || !ReferenceEquals(selectedDiff[0], change.Parsed))
            {
                selectedDiff.Clear();
                selectedDiff.Add(change.Parsed);
            }
            diffView.SetFiles(change, 0, selectedDiff);
            diffView.Draw(inner);
        }

        private void DrawFileList(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
            var outRect = rect.ContractedBy(3f);
            int n = fileLabels.Count;
            float extra = omittedLine != null ? 22f : 0f;
            var view = new Rect(0f, 0f, outRect.width - 16f, n * FileRowH + extra);
            Widgets.BeginScrollView(outRect, ref fileScroll, view);
            var oldWrap = Text.WordWrap;
            var oldAnchor = Text.Anchor;
            Text.WordWrap = false;
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleLeft;
            for (int i = 0; i < n; i++)
            {
                var r = new Rect(0f, i * FileRowH, view.width, FileRowH);
                if (i == selectedFile) Widgets.DrawHighlightSelected(r);
                else Widgets.DrawHighlightIfMouseover(r);
                float countW = 64f;
                string flags = fileFlags[i];
                float flagW = flags.Length > 0 ? Mathf.Min(70f, r.width * 0.3f) : 0f;
                GUI.color = UIUtil.ColDiffFile;
                Widgets.Label(new Rect(r.x + 4f, r.y, r.width - countW - flagW - 8f, r.height), fileLabels[i]);
                if (flagW > 0f)
                {
                    GUI.color = UIUtil.ColMuted;
                    Widgets.Label(new Rect(r.xMax - countW - flagW, r.y, flagW, r.height), flags);
                }
                GUI.color = UIUtil.ColResult;
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(r.xMax - countW, r.y, countW - 4f, r.height), fileCounts[i]);
                Text.Anchor = TextAnchor.MiddleLeft;
                GUI.color = Color.white;
                if (Mouse.IsOver(r)) TooltipHandler.TipRegion(r, fileTips[i]);
                if (Widgets.ButtonInvisible(r)) selectedFile = i;
            }
            if (omittedLine != null)
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(new Rect(4f, n * FileRowH, view.width - 8f, 22f), omittedLine);
                GUI.color = Color.white;
            }
            Text.Anchor = oldAnchor;
            Text.Font = GameFont.Small;
            Text.WordWrap = oldWrap;
            Widgets.EndScrollView();
        }
    }
}
