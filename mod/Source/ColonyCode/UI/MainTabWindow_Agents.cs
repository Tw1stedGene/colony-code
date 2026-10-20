using System.Collections.Generic;
using ColonyCode.Agents;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyCode.UI
{
    /// <summary>The "Agents" main tab: bridge status, agents, goals, open decisions.</summary>
    public class MainTabWindow_Agents : MainTabWindow
    {
        private const float AgentRowHeight = 30f;
        private const float GoalRowHeight = 44f;

        private const float SessionRowHeight = 28f;

        private Vector2 agentScroll;
        private Vector2 goalScroll;
        private Vector2 sessionScroll;

        public override Vector2 RequestedTabSize => new Vector2(1010f, 640f);

        public override void DoWindowContents(Rect inRect)
        {
            var reg = UIUtil.Registry;
            float y = inRect.y;

            // Title + buttons.
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, y, 300f, 34f), "ColonyCode_AgentsTitle".Translate());
            Text.Font = GameFont.Small;
            float bx = inRect.xMax;
            int open = UIUtil.OpenDecisionCount();
            bx -= 170f;
            if (Widgets.ButtonText(new Rect(bx, y + 2f, 170f, 30f), "ColonyCode_OpenDecisions".Translate(open), true, true,
                    open > 0 ? new Color(1f, 0.55f, 0.55f) : Color.white))
                UIEntry.OpenDecisions();
            bx -= 150f;
            if (Widgets.ButtonText(new Rect(bx, y + 2f, 142f, 30f), "ColonyCode_OpenConsole".Translate()))
                UIEntry.OpenConsole();
            bx -= 150f;
            if (Widgets.ButtonText(new Rect(bx, y + 2f, 142f, 30f), "ColonyCode_OpenTaskBoard".Translate()))
                UIEntry.OpenTaskBoard();
            bx -= 130f;
            if (Widgets.ButtonText(new Rect(bx, y + 2f, 122f, 30f), "ColonyCode_OpenJournal".Translate()))
                UIEntry.OpenJournal();
            y += 38f;

            // Connection status.
            y = DrawStatus(new Rect(inRect.x, y, inRect.width, 48f), reg) + 6f;

            if (reg == null)
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 30f), "ColonyCode_NoRegistry".Translate());
                GUI.color = Color.white;
                return;
            }

            int sessionCount = reg.Sessions.Count;
            if (sessionCount > 0)
            {
                float sh = 6f + 20f + Mathf.Min(sessionCount, 3) * SessionRowHeight + 6f;
                DrawSessions(new Rect(inRect.x, y, inRect.width, sh), reg);
                y += sh + 6f;
            }

            float remaining = inRect.yMax - y;
            float agentsH = Mathf.Floor(remaining * 0.58f);
            DrawAgents(new Rect(inRect.x, y, inRect.width, agentsH), reg);
            y += agentsH + 8f;
            DrawGoals(new Rect(inRect.x, y, inRect.width, inRect.yMax - y), reg);
        }

        private float DrawStatus(Rect r, AgentRegistry reg)
        {
            Widgets.DrawMenuSection(r);
            var inner = r.ContractedBy(6f);
            var server = Core.Bridge.Instance;
            bool running = server != null && server.IsRunning;
            bool client = running && server.HasRecentClient(System.TimeSpan.FromSeconds(40));
            UIUtil.DrawDot(new Rect(inner.x, inner.y + 5f, 12f, 12f), !running ? UIUtil.ColBad : client ? UIUtil.ColGood : new Color(1f, 0.8f, 0.3f));
            Widgets.Label(new Rect(inner.x + 18f, inner.y, inner.width - 18f, 22f), UIUtil.Safe(Core.BridgeHost.StatusText));

            GUI.color = UIUtil.ColMuted;
            Text.Font = GameFont.Tiny;
            string details;
            if (running)
            {
                int connectedSessions = 0;
                if (reg != null) foreach (var s in reg.Sessions) if (s.connected) connectedSessions++;
                details = "ColonyCode_StatusDetails".Translate(server.Port, connectedSessions, reg?.Agents?.Count ?? 0, server.RequestCount);
            }
            else
            {
                details = "ColonyCode_StatusStopped".Translate(UIUtil.Safe(server?.LastError ?? ""));
            }
            Widgets.Label(new Rect(inner.x + 18f, inner.y + 22f, inner.width - 18f, 18f), details);
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            return r.yMax;
        }

        /// <summary>One row per session: label, connection, token usage (in/out, cache), turns, worktrees, Review.</summary>
        private void DrawSessions(Rect r, AgentRegistry reg)
        {
            Widgets.DrawMenuSection(r);
            var inner = r.ContractedBy(6f);
            Text.Font = GameFont.Tiny;
            GUI.color = UIUtil.ColMuted;
            Widgets.Label(new Rect(inner.x + 20f, inner.y, 220f, 18f), "ColonyCode_ColSession".Translate());
            Widgets.Label(new Rect(inner.x + 240f, inner.y, 300f, 18f), "ColonyCode_ColUsage".Translate());
            Widgets.Label(new Rect(inner.x + 560f, inner.y, 160f, 18f), "ColonyCode_ColWorktrees".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            var sessions = new List<SessionInfo>(reg.Sessions);
            sessions.Sort((a, b) => a.connected != b.connected ? (a.connected ? -1 : 1) : a.seq.CompareTo(b.seq));
            var outRect = new Rect(inner.x, inner.y + 20f, inner.width, inner.height - 20f);
            var view = new Rect(0f, 0f, outRect.width - 16f, sessions.Count * SessionRowHeight);
            Widgets.BeginScrollView(outRect, ref sessionScroll, view);
            var oldWrap = Text.WordWrap;
            Text.WordWrap = false;
            Text.Anchor = TextAnchor.MiddleLeft;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                var row = new Rect(0f, i * SessionRowHeight, view.width, SessionRowHeight);
                if (i % 2 == 1) Widgets.DrawLightHighlight(row);
                UIUtil.DrawDot(new Rect(row.x + 4f, row.y + (row.height - 12f) / 2f, 12f, 12f), s.connected ? UIUtil.ColGood : UIUtil.ColMuted);
                if (!s.connected) GUI.color = UIUtil.ColMuted;
                var labelRect = new Rect(row.x + 20f, row.y, 218f, row.height);
                Widgets.Label(labelRect, UIUtil.SessionLabel(s.id) + (s.connected ? "" : " " + "ColonyCode_SessionEndedTag".Translate()));
                TooltipHandler.TipRegion(labelRect, UIUtil.Safe((s.cwd ?? s.id) + (string.IsNullOrEmpty(s.branch) ? "" : "\n" + s.branch) + (string.IsNullOrEmpty(s.model) ? "" : "\n" + s.model)));
                var u = s.usage;
                string usage = u.updatedAt == 0 && u.turns == 0
                    ? "ColonyCode_UsageNone".Translate().ToString()
                    : "ColonyCode_UsageLine".Translate(UIUtil.FormatTokens(u.inputTokens), UIUtil.FormatTokens(u.outputTokens), u.turns).ToString();
                var usageRect = new Rect(row.x + 240f, row.y, 310f, row.height);
                Widgets.Label(usageRect, usage);
                if (u.cacheReadTokens > 0 || u.updatedAt > 0)
                    TooltipHandler.TipRegion(usageRect, "ColonyCode_UsageTip".Translate(u.inputTokens, u.outputTokens, u.cacheReadTokens, u.turns));
                string wts = s.hasWorktreeList
                    ? "ColonyCode_WorktreeCount".Translate(s.worktrees.Count).ToString() + (s.dirty ? " · " + "ColonyCode_ReviewMainDirty".Translate() : "")
                    : "-";
                Widgets.Label(new Rect(row.x + 560f, row.y, 200f, row.height), wts);
                GUI.color = Color.white;
                string sid = s.id;
                if (Widgets.ButtonText(new Rect(row.xMax - 90f, row.y + 2f, 88f, row.height - 4f), "ColonyCode_Review".Translate()))
                    UIEntry.OpenReview(sid);
            }
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = oldWrap;
            Widgets.EndScrollView();
        }

        private void DrawAgents(Rect r, AgentRegistry reg)
        {
            Widgets.DrawMenuSection(r);
            var inner = r.ContractedBy(6f);
            var agents = reg.Agents;
            int count = agents?.Count ?? 0;

            // Header row.
            Text.Font = GameFont.Tiny;
            GUI.color = UIUtil.ColMuted;
            float x0 = inner.x;
            Widgets.Label(new Rect(x0 + 20f, inner.y, 180f, 18f), "ColonyCode_ColName".Translate());
            Widgets.Label(new Rect(x0 + 200f, inner.y, 70f, 18f), "ColonyCode_ColRole".Translate());
            Widgets.Label(new Rect(x0 + 270f, inner.y, 110f, 18f), "ColonyCode_ColState".Translate());
            Widgets.Label(new Rect(x0 + 380f, inner.y, 250f, 18f), "ColonyCode_ColActivity".Translate());
            Widgets.Label(new Rect(x0 + 640f, inner.y, 125f, 18f), "ColonyCode_ColPawn".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            var outRect = new Rect(inner.x, inner.y + 20f, inner.width, inner.height - 20f);
            if (count == 0)
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(outRect, "ColonyCode_NoAgentsLong".Translate());
                GUI.color = Color.white;
                return;
            }
            var viewRect = new Rect(0f, 0f, outRect.width - 16f, count * AgentRowHeight);
            Widgets.BeginScrollView(outRect, ref agentScroll, viewRect);
            AgentInfo interrupt = null;
            for (int i = 0; i < count; i++)
            {
                var a = agents[i];
                if (a == null) continue;
                var row = new Rect(0f, i * AgentRowHeight, viewRect.width, AgentRowHeight);
                if (i % 2 == 1) Widgets.DrawLightHighlight(row);
                Widgets.DrawHighlightIfMouseover(row);
                if (Event.current.type == EventType.MouseDown && Event.current.clickCount == 2 && Mouse.IsOver(new Rect(row.x, row.y, 640f, row.height)))
                {
                    UIEntry.OpenAgentCard(a.id);
                    Event.current.Use();
                }
                if (DrawAgentRow(row, a)) interrupt = a;
            }
            Widgets.EndScrollView();
            if (interrupt != null) reg.Interrupt(interrupt.id);
        }

        /// <returns>true when the interrupt button was clicked.</returns>
        private static bool DrawAgentRow(Rect row, AgentInfo a)
        {
            var oldWrap = Text.WordWrap;
            Text.WordWrap = false;
            Text.Anchor = TextAnchor.MiddleLeft;
            UIUtil.DrawDot(new Rect(row.x + 4f, row.y + (row.height - 12f) / 2f, 12f, 12f), UIUtil.StateColor(a.state));
            var nameRect = new Rect(row.x + 20f, row.y, 178f, row.height);
            if (Mouse.IsOver(nameRect)) GUI.color = new Color(1f, 0.9f, 0.55f);
            Widgets.Label(nameRect, UIUtil.AgentLabel(a));
            GUI.color = Color.white;
            TooltipHandler.TipRegion(nameRect, "ColonyCode_OpenCardTip".Translate());
            Widgets.Label(new Rect(row.x + 200f, row.y, 68f, row.height), UIUtil.RoleLabel(a.role));
            GUI.color = UIUtil.StateColor(a.state);
            Widgets.Label(new Rect(row.x + 270f, row.y, 108f, row.height), UIUtil.StateLabel(a.state));
            GUI.color = Color.white;
            var actRect = new Rect(row.x + 380f, row.y, 250f, row.height);
            Widgets.Label(actRect, UIUtil.OneLine(a.activity, 48));
            if (!string.IsNullOrEmpty(a.activity)) TooltipHandler.TipRegion(actRect, UIUtil.Safe(a.activity));

            var pawnRect = new Rect(row.x + 640f, row.y, 125f, row.height);
            if (a.pawn != null)
            {
                Widgets.Label(pawnRect, UIUtil.Safe(a.pawn.LabelShort));
            }
            else
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(pawnRect, "ColonyCode_NoPawn".Translate());
                GUI.color = Color.white;
            }
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = oldWrap;

            // Name opens the agent card.
            if (Widgets.ButtonInvisible(nameRect)) UIEntry.OpenAgentCard(a.id);

            // Buttons: jump, diff, interrupt.
            float bx = row.xMax;
            bool clickedInterrupt = false;
            bx -= 64f;
            if (a.role == "lead")
            {
                if (Widgets.ButtonText(new Rect(bx, row.y + 3f, 62f, row.height - 6f), "ColonyCode_Interrupt".Translate()))
                    clickedInterrupt = true;
                TooltipHandler.TipRegion(new Rect(bx, row.y, 62f, row.height), "ColonyCode_InterruptTip".Translate());
            }
            bx -= 54f;
            if (Widgets.ButtonText(new Rect(bx, row.y + 3f, 52f, row.height - 6f), "ColonyCode_Diff".Translate()))
                UIEntry.OpenDiff(a.id);
            bx -= 54f;
            if (UIUtil.CanJump(a) && Widgets.ButtonText(new Rect(bx, row.y + 3f, 52f, row.height - 6f), "ColonyCode_Jump".Translate()))
                UIUtil.JumpTo(a);
            return clickedInterrupt;
        }

        private void DrawGoals(Rect r, AgentRegistry reg)
        {
            Widgets.DrawMenuSection(r);
            var inner = r.ContractedBy(6f);
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(inner.x, inner.y, inner.width, 22f), "ColonyCode_Goals".Translate());
            var outRect = new Rect(inner.x, inner.y + 24f, inner.width, inner.height - 24f);
            var goals = reg.Goals;
            if (goals == null || goals.Count == 0)
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(outRect, "ColonyCode_NoGoals".Translate());
                GUI.color = Color.white;
                return;
            }
            var viewRect = new Rect(0f, 0f, outRect.width - 16f, goals.Count * GoalRowHeight);
            Widgets.BeginScrollView(outRect, ref goalScroll, viewRect);
            int row = 0;
            for (int i = goals.Count - 1; i >= 0; i--)
            {
                if (goals[i] == null) continue;
                GoalRow.Draw(new Rect(0f, row * GoalRowHeight, viewRect.width, GoalRowHeight - 4f), goals[i]);
                row++;
            }
            Widgets.EndScrollView();
        }
    }
}
