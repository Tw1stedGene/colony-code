using System.Collections.Generic;
using ColonyCode.Agents;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyCode.UI
{
    /// <summary>
    /// The kanban drawn by the task board tab and window: three equal columns (To do / In progress /
    /// Done) of cards from <see cref="AgentRegistry.Tasks"/>. Each instance keeps its own scroll
    /// positions; each column scrolls on its own.
    /// </summary>
    public class TaskBoardView
    {
        public const int DoneCap = 50;
        private const float ColumnGap = 8f;
        private const float HeaderHeight = 30f;
        private const float CardGap = 6f;
        private const float CardPad = 6f;
        private const float AccentWidth = 3f;
        private const float MetaLineHeight = 18f;
        private const float ScrollbarWidth = 16f;
        private const float TagLineHeight = 18f;
        private const float TagPadX = 4f;
        /// <summary>Subagent cards linked to a goal sit this much to the right.</summary>
        private const float SubIndent = 12f;

        private static readonly Color ColTodo = new Color(0.62f, 0.62f, 0.62f);
        private static readonly Color ColDoing = new Color(0.45f, 0.72f, 1f);
        private static readonly Color ColDone = new Color(0.4f, 0.85f, 0.4f);
        private static readonly Color CardBg = new Color(0.17f, 0.18f, 0.19f, 0.95f);
        private static readonly Color CardBorder = new Color(0.32f, 0.33f, 0.35f);
        private static readonly Color TagGoal = new Color(0.45f, 0.72f, 1f);
        private static readonly Color TagAgent = new Color(0.78f, 0.6f, 1f);
        private static readonly Color TagGame = new Color(0.55f, 0.85f, 0.45f);
        private static readonly Color TagClaude = new Color(0.7f, 0.7f, 0.7f);
        private static readonly Color TagWaiting = new Color(0.95f, 0.75f, 0.35f);
        private static readonly Color TagInterrupted = new Color(1f, 0.72f, 0.25f);

        // Translated tag labels and their widths (Tiny font), built once per active language.
        private static LoadedLanguage tagsFor;
        private static string tagGoal, tagAgent, tagGame, tagClaude, tagWaiting, tagFailed, tagInterrupted;
        private static float wGoal, wAgent, wGame, wClaude, wWaiting, wFailed, wInterrupted;

        private readonly Vector2[] scroll = new Vector2[3];
        private readonly List<TaskInfo> todo = new List<TaskInfo>();
        private readonly List<TaskInfo> doing = new List<TaskInfo>();
        private readonly List<TaskInfo> done = new List<TaskInfo>();

        public void Draw(Rect rect)
        {
            var reg = UIUtil.Registry;
            if (reg == null)
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(rect, "ColonyCode_NoRegistry".Translate());
                GUI.color = Color.white;
                return;
            }

            EnsureTags();
            todo.Clear();
            doing.Clear();
            done.Clear();
            var tasks = reg.Tasks;
            for (int i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                if (t == null) continue;
                if (t.status == "done") done.Add(t);
                else if (t.status == "doing") doing.Add(t);
                else todo.Add(t);
            }
            // Newest first: the order in which they became done.
            done.Sort((a, b) => b.doneSeq.CompareTo(a.doneSeq));

            float colW = Mathf.Floor((rect.width - 2f * ColumnGap) / 3f);
            DrawColumn(new Rect(rect.x, rect.y, colW, rect.height), 0, "ColonyCode_TaskColTodo".Translate(todo.Count), ColTodo, todo, 0);
            DrawColumn(new Rect(rect.x + colW + ColumnGap, rect.y, colW, rect.height), 1, "ColonyCode_TaskColDoing".Translate(doing.Count), ColDoing, doing, 0);
            int hidden = Mathf.Max(0, done.Count - DoneCap);
            DrawColumn(new Rect(rect.x + 2f * (colW + ColumnGap), rect.y, colW, rect.height), 2, "ColonyCode_TaskColDone".Translate(done.Count), ColDone, done, hidden);

            if (todo.Count + doing.Count + done.Count == 0)
            {
                var msg = new Rect(rect.x + 20f, rect.y + HeaderHeight + 20f, rect.width - 40f, 60f);
                GUI.color = UIUtil.ColMuted;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(msg, "ColonyCode_TaskBoardEmpty".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }
        }

        private void DrawColumn(Rect r, int index, string header, Color accent, List<TaskInfo> items, int hidden)
        {
            Widgets.DrawMenuSection(r);
            var headerRect = new Rect(r.x + 8f, r.y + 4f, r.width - 16f, HeaderHeight - 6f);
            Text.Font = GameFont.Small;
            if (index == 0)
            {
                // "+ Add task": asks Claude Code to work on one (task.create); the plugin adds its card (game-<id>) with its next snapshot.
                float bw = Mathf.Min(110f, headerRect.width * 0.45f);
                var addRect = new Rect(headerRect.xMax - bw, headerRect.y, bw, headerRect.height);
                if (Widgets.ButtonText(addRect, "ColonyCode_TaskAdd".Translate())) OpenAddTask();
                TooltipHandler.TipRegion(addRect, "ColonyCode_TaskAddTip".Translate());
                headerRect.width -= bw + 4f;
            }
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = accent;
            Widgets.Label(headerRect, header);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Widgets.DrawBoxSolid(new Rect(r.x + 4f, r.y + HeaderHeight - 2f, r.width - 8f, 2f), accent);

            var outRect = new Rect(r.x + 4f, r.y + HeaderHeight + 4f, r.width - 8f, r.height - HeaderHeight - 8f);
            int shown = Mathf.Min(items.Count, items.Count - hidden);
            float cardW = outRect.width - ScrollbarWidth;
            float total = 0f;
            var heights = new float[shown];
            for (int i = 0; i < shown; i++)
            {
                heights[i] = CardHeight(items[i], cardW - Indent(items[i]));
                total += heights[i] + CardGap;
            }
            if (hidden > 0) total += 24f;

            var viewRect = new Rect(0f, 0f, cardW, Mathf.Max(total, 1f));
            Widgets.BeginScrollView(outRect, ref scroll[index], viewRect);
            float y = 0f;
            for (int i = 0; i < shown; i++)
            {
                float indent = Indent(items[i]);
                var card = new Rect(indent, y, cardW - indent, heights[i]);
                // Only cards in view are drawn.
                if (card.yMax >= scroll[index].y && card.y <= scroll[index].y + outRect.height) DrawCard(card, items[i]);
                y += heights[i] + CardGap;
            }
            if (hidden > 0)
            {
                GUI.color = UIUtil.ColMuted;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(0f, y, cardW, 22f), "ColonyCode_TaskMore".Translate(hidden));
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
            }
            Widgets.EndScrollView();
        }

        /// <summary>Opens the "add task" text box; sends task.create (with a session picker when several sessions are known).</summary>
        public static void OpenAddTask()
        {
            bool pick = SessionPicker.Worthwhile();
            Find.WindowStack.Add(new Dialog_TextInput(
                "ColonyCode_TaskAddTitle".Translate(),
                "ColonyCode_TaskAddPrompt".Translate(),
                "ColonyCode_Send".Translate(), pick, pick ? Window_Console.PickedSession : null,
                (text, session) =>
                {
                    var reg = UIUtil.Registry;
                    if (reg != null && reg.CreateTask(text, session))
                    {
                        string key = reg.AnySessionConnected() ? "ColonyCode_TaskSent" : "ColonyCode_TaskQueuedNoSession";
                        UIUtil.Toast((session != null ? "[" + UIUtil.SessionLabel(session) + "] " : "") + key.Translate(UIUtil.OneLine(text, 80)));
                    }
                }));
        }

        // ------------------------------------------------------------------ cards

        /// <summary>The subject as shown: "[session] " prefix when several sessions are known.</summary>
        private static string SubjectText(TaskInfo t) => UIUtil.SessionPrefix(t.sessionId) + UIUtil.Safe(t.subject);

        private static float InnerWidth(float cardW) => cardW - 2f * CardPad - AccentWidth;

        /// <summary>Subagent cards with a goal are indented under it.</summary>
        private static float Indent(TaskInfo t) => t.source == "agent" && !string.IsNullOrEmpty(t.parent) ? SubIndent : 0f;

        private static void EnsureTags()
        {
            if (tagsFor == LanguageDatabase.activeLanguage && tagGoal != null) return;
            tagsFor = LanguageDatabase.activeLanguage;
            tagGoal = "ColonyCode_TaskSrcGoal".Translate();
            tagAgent = "ColonyCode_TaskSrcAgent".Translate();
            tagGame = "ColonyCode_TaskSrcGame".Translate();
            tagClaude = "ColonyCode_TaskSrcClaude".Translate();
            tagWaiting = "ColonyCode_TaskSrcWaiting".Translate();
            tagFailed = "ColonyCode_TaskResultFailed".Translate();
            tagInterrupted = "ColonyCode_TaskResultInterrupted".Translate();
            var old = Text.Font;
            Text.Font = GameFont.Tiny;
            wGoal = Text.CalcSize(tagGoal).x + 2f * TagPadX;
            wAgent = Text.CalcSize(tagAgent).x + 2f * TagPadX;
            wGame = Text.CalcSize(tagGame).x + 2f * TagPadX;
            wClaude = Text.CalcSize(tagClaude).x + 2f * TagPadX;
            wWaiting = Text.CalcSize(tagWaiting).x + 2f * TagPadX;
            wFailed = Text.CalcSize(tagFailed).x + 2f * TagPadX;
            wInterrupted = Text.CalcSize(tagInterrupted).x + 2f * TagPadX;
            Text.Font = old;
        }

        private static void SourceTag(TaskInfo t, out string label, out float width, out Color color)
        {
            if (t.pending) { label = tagWaiting; width = wWaiting; color = TagWaiting; return; }
            switch (t.source)
            {
                case "goal": label = tagGoal; width = wGoal; color = TagGoal; return;
                case "agent": label = tagAgent; width = wAgent; color = TagAgent; return;
                case "game": label = tagGame; width = wGame; color = TagGame; return;
                default: label = tagClaude; width = wClaude; color = TagClaude; return;
            }
        }

        /// <summary>"↳ for: <goal>" on a subagent card, cached until the goal's subject changes; null when none.</summary>
        private static string ParentLine(TaskInfo t)
        {
            if (string.IsNullOrEmpty(t.parent)) return null;
            string goal = t.parentSubject ?? t.parent;
            if (t.uiParentLine == null || !ReferenceEquals(t.uiParentLineFor, goal))
            {
                t.uiParentLineFor = goal;
                t.uiParentLine = "ColonyCode_TaskForGoal".Translate(UIUtil.Safe(UIUtil.OneLine(goal, 70)));
            }
            return t.uiParentLine;
        }

        /// <summary>A small tinted box with the label: the card's source or its result.</summary>
        private static void DrawTag(Rect r, string label, Color color)
        {
            Widgets.DrawBoxSolid(r, new Color(color.r, color.g, color.b, 0.18f));
            GUI.color = color;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(r, label);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }

        private static float CardHeight(TaskInfo t, float cardW)
        {
            float w = InnerWidth(cardW);
            Text.Font = GameFont.Small;
            float h = CardPad + TagLineHeight + Text.CalcHeight(SubjectText(t), w);
            if (ParentLine(t) != null) h += MetaLineHeight;
            if (WhoFor(t, out _, out _)) h += MetaLineHeight;
            if (ShowsActiveForm(t))
            {
                Text.Font = GameFont.Tiny;
                h += Text.CalcHeight(ActiveFormText(t), w);
            }
            if (t.IsBlocked) h += MetaLineHeight;
            Text.Font = GameFont.Small;
            return h + CardPad;
        }

        private static bool ShowsActiveForm(TaskInfo t) => t.status == "doing" && !string.IsNullOrEmpty(t.activeForm);

        private static string ActiveFormText(TaskInfo t) => "<i>" + UIUtil.Safe(t.activeForm) + "</i>";

        private static void DrawCard(Rect r, TaskInfo t)
        {
            Widgets.DrawBoxSolidWithOutline(r, CardBg, CardBorder);
            Color accent = t.status == "done" ? ColDone : t.status == "doing" ? ColDoing : ColTodo;
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, AccentWidth, r.height), accent);
            Widgets.DrawHighlightIfMouseover(r);

            float x = r.x + AccentWidth + CardPad;
            float w = InnerWidth(r.width);
            float y = r.y + CardPad;

            // Source tag (Goal / Subagent / From game / Claude task); a failed or interrupted result on the right.
            Text.Font = GameFont.Tiny;
            SourceTag(t, out string tag, out float tagW, out Color tagColor);
            DrawTag(new Rect(x, y + 1f, Mathf.Min(tagW, w), TagLineHeight - 3f), tag, tagColor);
            if (t.result != null)
            {
                bool failed = t.result == "failed";
                float rw = failed ? wFailed : wInterrupted;
                DrawTag(new Rect(x + w - rw, y + 1f, rw, TagLineHeight - 3f), failed ? tagFailed : tagInterrupted, failed ? UIUtil.ColBad : TagInterrupted);
            }
            y += TagLineHeight;

            // Subject, wrapped.
            Text.Font = GameFont.Small;
            string subject = SubjectText(t);
            float sh = Text.CalcHeight(subject, w);
            if (t.status == "done") GUI.color = new Color(0.8f, 0.8f, 0.8f);
            Widgets.Label(new Rect(x, y, w, sh), subject);
            GUI.color = Color.white;
            y += sh;

            // "↳ for: <goal>" on a subagent card.
            string parentLine = ParentLine(t);
            if (parentLine != null)
            {
                Text.Font = GameFont.Tiny;
                var oldWrap = Text.WordWrap;
                Text.WordWrap = false;
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(new Rect(x, y, w, MetaLineHeight), parentLine);
                GUI.color = Color.white;
                Text.WordWrap = oldWrap;
                Text.Font = GameFont.Small;
                y += MetaLineHeight;
            }

            // Owner / agent with its state dot; click jumps to the resident.
            AgentInfo agent;
            string who;
            if (WhoFor(t, out who, out agent))
            {
                var line = new Rect(x, y, w, MetaLineHeight);
                UIUtil.DrawDot(new Rect(line.x, line.y + 4f, 10f, 10f), agent != null ? UIUtil.StateColor(agent.state) : UIUtil.ColMuted);
                Text.Font = GameFont.Tiny;
                var oldWrap = Text.WordWrap;
                Text.WordWrap = false;
                Text.Anchor = TextAnchor.MiddleLeft;
                var nameRect = new Rect(line.x + 14f, line.y, Mathf.Min(w - 14f, Text.CalcSize(who).x + 4f), MetaLineHeight);
                bool canJump = UIUtil.CanJump(agent);
                if (canJump && Mouse.IsOver(nameRect)) GUI.color = new Color(1f, 0.9f, 0.55f);
                else GUI.color = new Color(0.82f, 0.82f, 0.82f);
                Widgets.Label(nameRect, who);
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = oldWrap;
                if (canJump)
                {
                    TooltipHandler.TipRegion(nameRect, "ColonyCode_TaskJumpTip".Translate(agent.pawn.LabelShort));
                    if (Widgets.ButtonInvisible(nameRect)) UIUtil.JumpTo(agent);
                }
                else if (agent != null)
                {
                    TooltipHandler.TipRegion(nameRect, "ColonyCode_OpenCardTip".Translate());
                    if (Widgets.ButtonInvisible(nameRect)) UIEntry.OpenAgentCard(agent.id);
                }
                y += MetaLineHeight;
            }

            // What it is doing right now, in grey italics.
            if (ShowsActiveForm(t))
            {
                Text.Font = GameFont.Tiny;
                string af = ActiveFormText(t);
                float ah = Text.CalcHeight(af, w);
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(new Rect(x, y, w, ah), af);
                GUI.color = Color.white;
                y += ah;
            }

            if (t.IsBlocked)
            {
                Text.Font = GameFont.Tiny;
                var oldWrap = Text.WordWrap;
                Text.WordWrap = false;
                GUI.color = UIUtil.ColBad;
                Widgets.Label(new Rect(x, y, w, MetaLineHeight), "ColonyCode_TaskBlockedBy".Translate(BlockerList(t)));
                GUI.color = Color.white;
                Text.WordWrap = oldWrap;
            }
            Text.Font = GameFont.Small;

            TooltipHandler.TipRegion(r, Tooltip(t));
        }

        /// <summary>The name shown on a card: the owner, else the agent that last moved it.</summary>
        private static bool WhoFor(TaskInfo t, out string who, out AgentInfo agent)
        {
            var reg = UIUtil.Registry;
            agent = null;
            who = null;
            if (!string.IsNullOrEmpty(t.owner))
            {
                var owner = reg?.Get(t.owner) ?? reg?.Get(UIUtil.ResolveAgentId(t.owner));
                agent = owner;
                who = owner != null ? UIUtil.AgentName(owner) : t.owner;
            }
            else if (!string.IsNullOrEmpty(t.agentId))
            {
                agent = reg?.Get(t.agentId);
                who = agent != null ? UIUtil.AgentName(agent) : t.agentId;
            }
            if (string.IsNullOrEmpty(who)) return false;
            who = UIUtil.OneLine(who, 60);
            return true;
        }

        private static string BlockerList(TaskInfo t)
        {
            var parts = new List<string>(t.blockedBy.Count);
            foreach (var b in t.blockedBy) parts.Add("#" + b);
            return UIUtil.Safe(string.Join(", ", parts));
        }

        private static string Tooltip(TaskInfo t)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(UIUtil.Safe(t.subject));
            sb.Append("  (#").Append(UIUtil.Safe(t.id)).Append(')');
            SourceTag(t, out string tag, out _, out _);
            sb.Append('\n').Append(tag);
            if (t.result == "failed") sb.Append(" · ").Append(tagFailed);
            else if (t.result == "interrupted") sb.Append(" · ").Append(tagInterrupted);
            string parentLine = ParentLine(t);
            if (parentLine != null) sb.Append('\n').Append(parentLine);
            if (!string.IsNullOrEmpty(t.description)) sb.Append("\n\n").Append(UIUtil.Safe(t.description));
            if (t.IsBlocked) sb.Append("\n\n").Append("ColonyCode_TaskBlockedBy".Translate(BlockerList(t)));
            return sb.ToString();
        }
    }

    /// <summary>"Tasks" tab on the task board building.</summary>
    public class ITab_TaskBoard : ITab
    {
        private readonly TaskBoardView view = new TaskBoardView();

        public ITab_TaskBoard()
        {
            size = new Vector2(860f, 500f);
            labelKey = "ColonyCode_TabTaskBoard";
        }

        protected override void FillTab()
        {
            var rect = new Rect(0f, 0f, size.x, size.y).ContractedBy(10f);
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y, rect.width - 180f, 32f), "ColonyCode_TaskBoardTitle".Translate());
            Text.Font = GameFont.Small;
            if (Widgets.ButtonText(new Rect(rect.xMax - 170f, rect.y + 2f, 170f, 28f), "ColonyCode_TaskOpenWindow".Translate()))
                UIEntry.OpenTaskBoard();
            view.Draw(new Rect(rect.x, rect.y + 38f, rect.width, rect.height - 38f));
        }
    }

    /// <summary>Free-floating, resizable task board window.</summary>
    public class Window_TaskBoard : Window
    {
        private readonly TaskBoardView view = new TaskBoardView();

        public Window_TaskBoard()
        {
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            draggable = true;
            resizeable = true;
            preventCameraMotion = false;
            onlyOneOfTypeAllowed = true;
        }

        public override Vector2 InitialSize => new Vector2(1000f, 620f);

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 40f, 34f), "ColonyCode_TaskBoardTitle".Translate());
            Text.Font = GameFont.Small;
            view.Draw(new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f));
        }
    }
}
