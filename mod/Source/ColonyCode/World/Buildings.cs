using System.Collections.Generic;
using System.Text;
using ColonyCode.Agents;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyCode.World
{
    /// <summary>
    /// Agent workstation. Each powered desk brings one remote-worker resident to the colony; the
    /// resident ↔ desk assignment lives in the AgentRegistry. The Monitor inspector tab
    /// (ColonyCode.UI.ITab_AgentMonitor) reads <see cref="Agent"/>.
    /// </summary>
    public class Building_AgentDesk : Building
    {
        public const float FocusMinTemp = 16f;
        public const float FocusMaxTemp = 26f;
        public const float FocusMinCleanliness = -1f;
        public const float FocusMinImpressiveness = 30f;

        private CompPowerTrader powerComp;

        /// <summary>Agent embodied by this desk's resident right now, or null.</summary>
        public AgentInfo Agent => AgentRegistry.Instance?.AgentForDesk(this);

        /// <summary>Resident assigned to this desk, or null.</summary>
        public Pawn Resident => AgentRegistry.Instance?.ResidentForDesk(this)?.pawn;

        public bool PowerOn
        {
            get
            {
                var p = powerComp ?? GetComp<CompPowerTrader>();
                return p == null || p.PowerOn;
            }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            powerComp = GetComp<CompPowerTrader>();
        }

        /// <summary>Realtime part (drawerType MapMeshAndRealTime): the monitor glow while powered.</summary>
        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            base.DrawAt(drawLoc, flip);
            if (Spawned && PowerOn) ColonyEffects.DrawMonitorGlow(this, drawLoc);
        }

        public override string GetInspectString()
        {
            var sb = new StringBuilder(base.GetInspectString());
            var resident = Resident;
            var a = Agent;
            if (sb.Length > 0) sb.AppendLine();
            if (resident == null) sb.Append("ColonyCodeW_DeskFree".Translate());
            else if (a == null) sb.Append("ColonyCodeW_DeskResidentNoAgent".Translate(resident.LabelShort));
            else
            {
                sb.Append("ColonyCodeW_DeskOwner".Translate(resident.LabelShort, AgentStateUtility.Label(a.state)));
                if (!string.IsNullOrEmpty(a.activity)) sb.Append(": ").Append(a.activity);
            }
            string focus = FocusLine();
            if (focus != null) sb.AppendLine().Append(focus);
            return sb.ToString().TrimEndNewlines();
        }

        /// <summary>
        /// Cosmetic "Focus: good/ok/poor" from the room at the chair: clean, 16-26 C and somewhat
        /// impressive each count one point (3 good, 2 ok, else poor). No gameplay effect.
        /// </summary>
        public string FocusLine()
        {
            if (!Spawned) return null;
            var room = RegionAndRoomQuery.RoomAt(InteractionCell, Map);
            if (room == null || room.PsychologicallyOutdoors)
                return "ColonyCodeW_FocusWithReasons".Translate("ColonyCodeW_FocusPoor".Translate(), "ColonyCodeW_FocusOutdoors".Translate());
            var reasons = new List<string>(3);
            int points = 0;
            if (room.GetStat(RoomStatDefOf.Cleanliness) >= FocusMinCleanliness) points++;
            else reasons.Add("ColonyCodeW_FocusDirty".Translate());
            float t = room.Temperature;
            if (t < FocusMinTemp) reasons.Add("ColonyCodeW_FocusCold".Translate());
            else if (t > FocusMaxTemp) reasons.Add("ColonyCodeW_FocusHot".Translate());
            else points++;
            if (room.GetStat(RoomStatDefOf.Impressiveness) >= FocusMinImpressiveness) points++;
            else reasons.Add("ColonyCodeW_FocusPlain".Translate());
            string level = points >= 3 ? "ColonyCodeW_FocusGood".Translate() : points == 2 ? "ColonyCodeW_FocusOk".Translate() : "ColonyCodeW_FocusPoor".Translate();
            if (reasons.Count == 0) return "ColonyCodeW_Focus".Translate(level);
            return "ColonyCodeW_FocusWithReasons".Translate(level, string.Join(", ", reasons));
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;
            var a = Agent;
            string agentId = a?.id;
            string sessionId = a?.sessionId;

            var card = new Command_Action
            {
                defaultLabel = "ColonyCodeW_GizmoAgentCard".Translate(),
                defaultDesc = "ColonyCodeW_GizmoAgentCardDesc".Translate(),
                icon = ColonyEffects.CardIcon,
                action = () => UIBridge.OpenAgentCard(agentId),
            };
            if (agentId == null) card.Disable("ColonyCodeW_GizmoNoAgent".Translate());
            yield return card;

            var review = new Command_Action
            {
                defaultLabel = "ColonyCodeW_GizmoReview".Translate(),
                defaultDesc = "ColonyCodeW_GizmoReviewDesc".Translate(),
                icon = ColonyEffects.ReviewIcon,
                action = () => UIBridge.OpenReview(sessionId),
            };
            if (agentId == null) review.Disable("ColonyCodeW_GizmoNoAgent".Translate());
            else if (string.IsNullOrEmpty(sessionId)) review.Disable("ColonyCodeW_GizmoNoSession".Translate());
            yield return review;
        }
    }

    /// <summary>Where agents walk when they wait on the player. Its gizmo opens the open decisions.</summary>
    public class Building_DecisionTerminal : Building
    {
        public override string GetInspectString()
        {
            var sb = new StringBuilder(base.GetInspectString());
            var reg = AgentRegistry.Instance;
            if (reg != null)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("ColonyCodeW_OpenDecisionsCount".Translate(reg.OpenDecisions.Count));
            }
            return sb.ToString().TrimEndNewlines();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;
            var reg = AgentRegistry.Instance;
            int n = reg?.OpenDecisions.Count ?? 0;
            var cmd = new Command_Action
            {
                defaultLabel = "ColonyCodeW_OpenDecisions".Translate(n),
                defaultDesc = "ColonyCodeW_OpenDecisionsDesc".Translate(),
                icon = AgentStateUtility.CommandIcon(def),
                action = () => UIBridge.OpenDecisions(),
            };
            yield return cmd;
        }
    }

    /// <summary>
    /// Task board: goal and task counts in the inspect string; the "Tasks" tab
    /// (ColonyCode.UI.ITab_TaskBoard) and the "Open task board" gizmo show the kanban.
    /// </summary>
    public class Building_TaskBoard : Building
    {
        public override string GetInspectString()
        {
            var sb = new StringBuilder(base.GetInspectString());
            var reg = AgentRegistry.Instance;
            if (reg != null)
            {
                int active = 0, done = 0, failed = 0;
                GoalInfo current = null;
                foreach (var g in reg.Goals)
                {
                    if (g.status == "done") done++;
                    else if (g.status == "failed") failed++;
                    else { active++; current = g; }
                }
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("ColonyCodeW_TaskBoardCounts".Translate(active, done, failed));
                if (current != null && !string.IsNullOrEmpty(current.text))
                {
                    string t = current.text.Length > 60 ? current.text.Substring(0, 57) + "..." : current.text;
                    sb.AppendLine().Append("ColonyCodeW_TaskBoardCurrent".Translate(t,
                        current.progress >= 0f ? current.progress.ToStringPercent() : "?"));
                }
                int todo, doing, finished;
                reg.CountTasks(out todo, out doing, out finished);
                if (todo + doing + finished > 0)
                    sb.AppendLine().Append("ColonyCode_TaskBoardInspect".Translate(todo, doing, finished));
            }
            return sb.ToString().TrimEndNewlines();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;
            yield return new Command_Action
            {
                defaultLabel = "ColonyCode_OpenTaskBoardGizmo".Translate(),
                defaultDesc = "ColonyCode_OpenTaskBoardDesc".Translate(),
                icon = AgentStateUtility.CommandIcon(def),
                action = () => UIBridge.OpenTaskBoard(),
            };
        }
    }
}
