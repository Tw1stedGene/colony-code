using System.Collections.Generic;
using ColonyCode.Agents;
using UnityEngine;
using Verse;

namespace ColonyCode.UI
{
    /// <summary>
    /// One agent at a glance: name, session, role, state, resident, in-progress tasks, the last 30 log
    /// lines, and Message / Stop / Diff buttons.
    /// </summary>
    public class Window_AgentCard : Window
    {
        public const int LogLines = 30;

        public string agentId;
        private readonly LogView log = new LogView(false, LogLines);
        private readonly List<TaskInfo> tasks = new List<TaskInfo>();
        private Vector2 taskScroll;

        // The task list is rebuilt only when tasks or agents changed (or the card shows another agent),
        // not on every OnGUI event: ResolveAgentId allocates per call.
        private static int tasksVersion;
        private int seenTasksVersion = -1;
        private string seenTasksAgent;
        static Window_AgentCard()
        {
            AgentRegistry.TasksChanged += () => tasksVersion++;
            AgentRegistry.AgentChanged += _ => tasksVersion++;
        }

        public Window_AgentCard(string agentId)
        {
            this.agentId = agentId;
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            draggable = true;
            resizeable = true;
            preventCameraMotion = false;
            onlyOneOfTypeAllowed = true;
            forcePause = false;
        }

        public override Vector2 InitialSize => new Vector2(620f, 600f);

        /// <summary>In-progress tasks owned by (or last moved by) the agent.</summary>
        public static void TasksOf(AgentInfo a, List<TaskInfo> into)
        {
            into.Clear();
            var reg = UIUtil.Registry;
            if (reg == null || a == null) return;
            foreach (var t in reg.Tasks)
            {
                if (t == null || t.status != "doing" || t.sessionId != a.sessionId) continue;
                bool mine;
                if (!string.IsNullOrEmpty(t.owner))
                    mine = t.owner == a.id || UIUtil.ResolveAgentId(t.owner) == a.id;
                else
                    mine = t.agentId == a.id;
                if (mine) into.Add(t);
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            var reg = UIUtil.Registry;
            var a = reg?.Get(agentId);
            float y = inRect.y;
            if (a == null)
            {
                Widgets.Label(inRect, "ColonyCode_AgentGone".Translate());
                return;
            }

            // Name + state dot.
            UIUtil.DrawDot(new Rect(inRect.x, y + 9f, 14f, 14f), UIUtil.StateColor(a.state));
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x + 20f, y, inRect.width - 60f, 34f), UIUtil.Safe(UIUtil.AgentName(a)));
            Text.Font = GameFont.Small;
            y += 36f;

            // Session · role · state.
            GUI.color = UIUtil.ColMuted;
            string session = string.IsNullOrEmpty(a.sessionId) ? "?" : UIUtil.SessionLabel(a.sessionId);
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 22f),
                "ColonyCode_CardMeta".Translate(session, UIUtil.RoleLabel(a.role)));
            GUI.color = Color.white;
            y += 22f;
            GUI.color = UIUtil.StateColor(a.state);
            var oldWrap = Text.WordWrap;
            Text.WordWrap = false;
            string act = string.IsNullOrEmpty(a.activity) ? "" : " - " + UIUtil.OneLine(a.activity, 60);
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 22f), UIUtil.StateLabel(a.state));
            GUI.color = Color.white;
            float stw = Text.CalcSize(UIUtil.StateLabel(a.state)).x;
            Widgets.Label(new Rect(inRect.x + stw, y, inRect.width - stw, 22f), act);
            Text.WordWrap = oldWrap;
            y += 24f;

            // Resident + jump.
            string resident = a.pawn != null ? UIUtil.Safe(a.pawn.LabelShort) : "ColonyCode_NoPawn".Translate().ToString();
            Widgets.Label(new Rect(inRect.x, y + 3f, inRect.width - 100f, 22f), "ColonyCode_CardResident".Translate(resident));
            if (UIUtil.CanJump(a) && Widgets.ButtonText(new Rect(inRect.xMax - 90f, y, 90f, 26f), "ColonyCode_Jump".Translate()))
                UIUtil.JumpTo(a);
            y += 30f;

            // In-progress tasks.
            if (seenTasksVersion != tasksVersion || seenTasksAgent != a.id)
            {
                seenTasksVersion = tasksVersion;
                seenTasksAgent = a.id;
                TasksOf(a, tasks);
            }
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 22f), "ColonyCode_CardTasks".Translate(tasks.Count));
            y += 22f;
            float tasksH = tasks.Count == 0 ? 22f : Mathf.Min(tasks.Count, 4) * 22f;
            var taskOut = new Rect(inRect.x, y, inRect.width, tasksH);
            if (tasks.Count == 0)
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(taskOut, "ColonyCode_CardNoTasks".Translate());
                GUI.color = Color.white;
            }
            else
            {
                var view = new Rect(0f, 0f, taskOut.width - 16f, tasks.Count * 22f);
                Widgets.BeginScrollView(taskOut, ref taskScroll, view);
                Text.WordWrap = false;
                for (int i = 0; i < tasks.Count; i++)
                {
                    var t = tasks[i];
                    var r = new Rect(0f, i * 22f, view.width, 22f);
                    string line = "• " + UIUtil.OneLine(t.subject, 90);
                    if (!string.IsNullOrEmpty(t.activeForm)) line += "  <i>" + UIUtil.OneLine(t.activeForm, 60) + "</i>";
                    Widgets.Label(r, line);
                    TooltipHandler.TipRegion(r, UIUtil.Safe(t.subject + (string.IsNullOrEmpty(t.description) ? "" : "\n\n" + t.description)));
                }
                Text.WordWrap = oldWrap;
                Widgets.EndScrollView();
            }
            y += tasksH + 6f;

            // Log.
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 22f), "ColonyCode_CardLog".Translate(LogLines));
            y += 22f;
            float buttonsY = inRect.yMax - 32f;
            log.Draw(new Rect(inRect.x, y, inRect.width, buttonsY - y - 6f), a);

            // Buttons.
            float bx = inRect.x;
            if (Widgets.ButtonText(new Rect(bx, buttonsY, 130f, 32f), "ColonyCode_CardMessage".Translate()))
                UIEntry.OpenConsole("@" + UIUtil.Handle(a) + " ");
            bx += 138f;
            bool lead = a.role == "lead";
            var stopRect = new Rect(bx, buttonsY, 110f, 32f);
            if (Widgets.ButtonText(stopRect, "ColonyCode_Interrupt".Translate(), true, true, lead ? UIUtil.ColBad : Color.white, lead) && lead)
                reg.Interrupt(a.id);
            TooltipHandler.TipRegion(stopRect, lead ? "ColonyCode_InterruptTip".Translate() : "ColonyCode_StopWorkerTip".Translate());
            bx += 118f;
            if (Widgets.ButtonText(new Rect(bx, buttonsY, 110f, 32f), "ColonyCode_Diff".Translate()))
                UIEntry.OpenDiff(a.id);
            bx += 118f;
            if (!string.IsNullOrEmpty(a.sessionId) && Widgets.ButtonText(new Rect(bx, buttonsY, 110f, 32f), "ColonyCode_Review".Translate()))
                UIEntry.OpenReview(a.sessionId);
        }
    }
}
