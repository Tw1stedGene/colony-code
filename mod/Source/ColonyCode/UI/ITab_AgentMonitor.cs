using System.Collections.Generic;
using ColonyCode.Agents;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyCode.UI
{
    /// <summary>
    /// "Monitor" tab on the agent desk. Shows the live log of the agent at this desk: the agent whose
    /// pawn is assigned to it (CompAssignableToPawn), or whose pawn's current job targets it. The
    /// player can override the choice with the agent picker; the override is kept per desk.
    /// </summary>
    public class ITab_AgentMonitor : ITab
    {
        private static readonly Vector2 WinSize = new Vector2(620f, 480f);

        // Manual choice per desk (thingIDNumber -> agent id). Session-only UI state.
        private static readonly Dictionary<int, string> manualChoice = new Dictionary<int, string>();

        private readonly LogView view = new LogView();

        public ITab_AgentMonitor()
        {
            size = WinSize;
            labelKey = "ColonyCode_TabMonitor";
        }

        protected override void FillTab()
        {
            var rect = new Rect(0f, 0f, size.x, size.y).ContractedBy(10f);
            var thing = SelThing;
            var agent = FindAgent(thing);

            var headerRect = new Rect(rect.x, rect.y, rect.width, 30f);
            var nameRect = new Rect(headerRect.x + 18f, headerRect.y, headerRect.width - 18f - 230f, 30f);
            Text.Font = GameFont.Medium;
            if (agent != null)
            {
                UIUtil.DrawDot(new Rect(headerRect.x, headerRect.y + 9f, 12f, 12f), UIUtil.StateColor(agent.state));
                Widgets.Label(nameRect, UIUtil.Safe(UIUtil.AgentName(agent)));
            }
            else
            {
                Widgets.Label(nameRect, "ColonyCode_MonitorNoAgent".Translate());
            }
            Text.Font = GameFont.Small;

            float bx = headerRect.xMax;
            bx -= 110f;
            if (Widgets.ButtonText(new Rect(bx, headerRect.y + 2f, 110f, 26f), "ColonyCode_PickAgent".Translate()))
            {
                int id = thing != null ? thing.thingIDNumber : -1;
                AgentPicker.Open(aid => { if (id >= 0) manualChoice[id] = aid; });
            }
            if (agent != null)
            {
                bx -= 114f;
                if (Widgets.ButtonText(new Rect(bx, headerRect.y + 2f, 110f, 26f), "ColonyCode_ShowDiff".Translate()))
                    UIEntry.OpenDiff(agent.id);
            }

            float y = headerRect.yMax + 2f;
            if (agent != null)
            {
                var stateRect = new Rect(rect.x, y, rect.width, 22f);
                GUI.color = UIUtil.ColMuted;
                string line = UIUtil.RoleLabel(agent.role) + " - " + UIUtil.StateLabel(agent.state);
                if (!string.IsNullOrEmpty(agent.activity)) line += ": " + UIUtil.OneLine(agent.activity, 80);
                Widgets.Label(stateRect, line);
                GUI.color = Color.white;
                y = stateRect.yMax + 4f;
            }

            var logRect = new Rect(rect.x, y, rect.width, rect.yMax - y);
            if (agent == null)
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(logRect, (UIUtil.Registry == null || UIUtil.Registry.Agents == null || UIUtil.Registry.Agents.Count == 0)
                    ? "ColonyCode_NoAgents".Translate()
                    : "ColonyCode_MonitorPickHint".Translate());
                GUI.color = Color.white;
                return;
            }
            view.Draw(logRect, agent);
        }

        /// <summary>The agent shown for this desk, or null.</summary>
        public static AgentInfo FindAgent(Thing desk)
        {
            var reg = UIUtil.Registry;
            if (reg?.Agents == null || desk == null) return null;

            string chosen;
            if (manualChoice.TryGetValue(desk.thingIDNumber, out chosen))
            {
                var a = reg.Get(chosen);
                if (a != null) return a;
                manualChoice.Remove(desk.thingIDNumber);
            }

            // 1) The desk's own agent (Building_AgentDesk.Agent), read loosely so the UI does not
            //    depend on the building class.
            var owner = DeskAgent(desk);
            if (owner != null) return owner;

            // 2) A linked pawn assigned to this desk.
            var assign = desk.TryGetComp<CompAssignableToPawn>();
            if (assign != null)
            {
                foreach (var a in reg.Agents)
                {
                    if (a?.pawn != null && assign.AssignedPawnsForReading.Contains(a.pawn)) return a;
                }
            }
            // 3) A linked pawn currently working at this desk.
            foreach (var a in reg.Agents)
            {
                var job = a?.pawn?.CurJob;
                if (job != null && (job.targetA.Thing == desk || job.targetB.Thing == desk)) return a;
            }
            // 4) Only one agent in the colony: show it.
            return reg.Agents.Count == 1 ? reg.Agents[0] : null;
        }

        private static readonly Dictionary<System.Type, System.Reflection.PropertyInfo> agentProps = new Dictionary<System.Type, System.Reflection.PropertyInfo>();

        private static AgentInfo DeskAgent(Thing desk)
        {
            var t = desk.GetType();
            System.Reflection.PropertyInfo prop;
            if (!agentProps.TryGetValue(t, out prop))
            {
                prop = t.GetProperty("Agent", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (prop != null && prop.PropertyType != typeof(AgentInfo)) prop = null;
                agentProps[t] = prop;
            }
            if (prop == null) return null;
            try { return prop.GetValue(desk, null) as AgentInfo; }
            catch { return null; }
        }
    }
}
