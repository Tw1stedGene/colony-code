using System.Collections.Generic;
using ColonyCode.Agents;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ColonyCode.World
{
    /// <summary>
    /// Permanent lord of the remote workers on one map. It never ends and never makes its pawns leave:
    /// downed residents stay members (rescue is the player's choice). Only
    /// <see cref="ResidentUtility.StartLeaving"/> moves a resident to an exit lord.
    /// </summary>
    public class LordJob_AgentResident : LordJob
    {
        public LordJob_AgentResident() { }

        public override StateGraph CreateGraph()
        {
            var graph = new StateGraph();
            graph.AddToil(new LordToil_AgentResident());
            return graph;
        }

        public override bool ShouldExistWithoutPawns => true;

        public override bool AddFleeToil => false;

        public override bool CanOpenAnyDoor(Pawn p) => true;

        public override bool ShouldRemovePawn(Pawn p, PawnLostCondition reason)
        {
            // Keep downed residents (and anything we do not recognise as "really gone").
            switch (reason)
            {
                case PawnLostCondition.Incapped:
                case PawnLostCondition.Undefined:
                case PawnLostCondition.InMentalState:
                    return false;
                default:
                    return true;
            }
        }

        public override string GetReport(Pawn pawn) => "ColonyCodeW_LordReport".Translate();
    }

    /// <summary>Sets each resident's duty from its agent's state (see <see cref="ResidentAI.RefreshDuty"/>).</summary>
    public class LordToil_AgentResident : LordToil
    {
        public const int RefreshIntervalTicks = 30;
        private int counter;

        public override bool AllowSatisfyLongNeeds => false;

        public override void UpdateAllDuties()
        {
            var pawns = lord.ownedPawns;
            for (int i = 0; i < pawns.Count; i++) ResidentAI.RefreshDuty(pawns[i], false);
        }

        public override void LordToilTick()
        {
            if (++counter < RefreshIntervalTicks) return;
            counter = 0;
            var pawns = lord.ownedPawns;
            for (int i = 0; i < pawns.Count; i++) ResidentAI.RefreshDuty(pawns[i], true);
        }
    }

    /// <summary>Duty selection for residents.</summary>
    public static class ResidentAI
    {
        public const float IdleWanderRadius = 5f;

        public static bool IsWaiting(AgentInfo a)
        {
            return a != null && (a.state == AgentState.WaitingUser || a.station == AgentStation.User);
        }

        public static Building_DecisionTerminal ClosestTerminal(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned) return null;
            var def = ColonyCodeDefOf.ColonyCode_DecisionTerminal;
            if (def == null || pawn.Map.listerThings.ThingsOfDef(def).Count == 0) return null;
            return GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ThingRequest.ForDef(def),
                PathEndMode.InteractionCell, TraverseParms.For(pawn, Danger.Some), 9999f,
                t => t is Building_DecisionTerminal && t.Spawned && !t.IsBurning()) as Building_DecisionTerminal;
        }

        /// <summary>The desk a resident may code at right now, or null.</summary>
        public static Building_AgentDesk UsableDesk(Pawn pawn)
        {
            var rec = AgentRegistry.Instance?.ResidentFor(pawn);
            var desk = rec?.desk;
            if (desk == null || !desk.Spawned || desk.Map != pawn.Map || !desk.PowerOn || desk.IsBurning()) return null;
            return desk;
        }

        /// <summary>
        /// Picks the duty for a resident: code at its desk while the agent is active, wait at the nearest
        /// decision terminal while the agent waits on the player, otherwise idle near the desk. When the
        /// duty changes and <paramref name="interruptIdle"/> is set, an idle job is ended so the pawn
        /// reacts at once (coding/waiting jobs end by themselves when the agent state changes).
        /// </summary>
        public static void RefreshDuty(Pawn pawn, bool interruptIdle)
        {
            if (pawn == null || pawn.mindState == null || !pawn.Spawned) return;
            var reg = AgentRegistry.Instance;
            var rec = reg?.ResidentFor(pawn);
            if (rec == null || rec.leaving) return;
            var agent = reg.GetForPawn(pawn);

            DutyDef def;
            LocalTargetInfo focus;
            var desk = rec.desk != null && rec.desk.Spawned && rec.desk.Map == pawn.Map ? rec.desk : null;
            Building_DecisionTerminal terminal;
            if (IsWaiting(agent) && (terminal = ClosestTerminal(pawn)) != null)
            {
                def = ColonyCodeDefOf.ColonyCode_AwaitAtTerminal;
                focus = terminal;
            }
            else if (agent != null && AgentRegistry.IsActive(agent.state) && UsableDesk(pawn) != null)
            {
                def = ColonyCodeDefOf.ColonyCode_CodeAtDesk;
                focus = desk;
            }
            else
            {
                def = ColonyCodeDefOf.ColonyCode_IdleNearDesk;
                focus = desk != null ? (LocalTargetInfo)desk.InteractionCell : LocalTargetInfo.Invalid;
            }
            if (def == null) return;

            var cur = pawn.mindState.duty;
            if (cur != null && cur.def == def)
            {
                // Same duty. Idle without a desk keeps its old spot instead of following the pawn.
                if (!focus.IsValid || cur.focus == focus) return;
            }
            if (!focus.IsValid) focus = pawn.Position;
            var duty = new PawnDuty(def, focus);
            if (def == ColonyCodeDefOf.ColonyCode_IdleNearDesk) duty.wanderRadius = IdleWanderRadius;
            pawn.mindState.duty = duty;

            if (interruptIdle) InterruptIdleJob(pawn);
        }

        /// <summary>Ends a wander/wait job so the pawn re-thinks with its new duty.</summary>
        public static void InterruptIdleJob(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.jobs == null) return;
            var def = pawn.CurJobDef;
            if (def == JobDefOf.Wait_Wander || def == JobDefOf.GotoWander || def == JobDefOf.Wait ||
                def == JobDefOf.Wait_MaintainPosture)
            {
                pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, true);
            }
        }
    }

    /// <summary>Duty node: code at the resident's desk while its agent is busy.</summary>
    /// <summary>
    /// Idle wandering around the duty spot (the desk's seat) that stays in that spot's room, so residents
    /// of an office do not drift out through the door. Outdoors, it behaves like the vanilla giver.
    /// </summary>
    public class JobGiver_WanderInDeskRoom : JobGiver_WanderNearDutyLocation
    {
        public JobGiver_WanderInDeskRoom()
        {
            wanderDestValidator = (pawn, cell, root) =>
            {
                var map = pawn.Map;
                if (map == null) return true;
                var home = root.GetRoom(map);
                if (home == null || home.PsychologicallyOutdoors) return true;
                return cell.GetRoom(map) == home;
            };
        }
    }

    public class JobGiver_CodeAtDesk : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            var a = AgentPawnUtility.AgentFor(pawn);
            if (a == null || !AgentRegistry.IsActive(a.state) || ResidentAI.IsWaiting(a)) return null;
            var desk = ResidentAI.UsableDesk(pawn);
            if (desk == null) return null;
            if (!pawn.CanReserveAndReach(desk, PathEndMode.InteractionCell, Danger.Some)) return null;
            return JobMaker.MakeJob(ColonyCodeDefOf.ColonyCode_Code, desk);
        }
    }

    /// <summary>Duty node: stand at the nearest decision terminal while the agent waits on the player.</summary>
    public class JobGiver_AwaitAtTerminal : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!ResidentAI.IsWaiting(AgentPawnUtility.AgentFor(pawn))) return null;
            var terminal = ResidentAI.ClosestTerminal(pawn);
            if (terminal == null) return null;
            return JobMaker.MakeJob(ColonyCodeDefOf.ColonyCode_AwaitDecision, terminal);
        }
    }

    /// <summary>The "remote link" hediff; its label shows the agent the resident embodies.</summary>
    public class Hediff_RemoteLink : HediffWithComps
    {
        public override string LabelInBrackets
        {
            get
            {
                var a = AgentPawnUtility.AgentFor(pawn);
                if (a == null) return "ColonyCodeW_LinkNoAgent".Translate();
                return a.displayName + ", " + AgentStateUtility.Label(a.state);
            }
        }

        public override string TipStringExtra
        {
            get
            {
                var a = AgentPawnUtility.AgentFor(pawn);
                string baseTip = base.TipStringExtra;
                if (a == null) return baseTip;
                string line = "ColonyCodeW_InspectAgent".Translate(a.displayName, a.role ?? "?", AgentStateUtility.Label(a.state));
                if (!string.IsNullOrEmpty(a.activity)) line += ": " + a.activity;
                return string.IsNullOrEmpty(baseTip) ? line : baseTip + "\n" + line;
            }
        }
    }
}
