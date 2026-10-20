using System.Collections.Generic;
using ColonyCode.Agents;
using ColonyCode.Core;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ColonyCode.World
{
    /// <summary>
    /// A resident sits at its agent desk while the agent is busy (duty ColonyCode_CodeAtDesk). Motes
    /// follow the agent state. Ends when the agent goes idle/done/waiting, the resident loses the
    /// agent, or after <see cref="MaxTicks"/> (the duty then gives a fresh job).
    /// </summary>
    public class JobDriver_Code : JobDriver
    {
        public const int MaxTicks = 6000;
        public const int EffectInterval = 240;
        public const int KeyIntervalMin = 25;
        public const int KeyIntervalMax = 70;

        private int workedTicks;
        private int nextEffect;
        private int nextKey;

        private Building_AgentDesk Desk => job.GetTarget(TargetIndex.A).Thing as Building_AgentDesk;

        private AgentInfo Agent => AgentPawnUtility.AgentFor(pawn);

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref workedTicks, "workedTicks");
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, -1, null, errorOnFailed);
        }

        public override string GetReport()
        {
            var a = Agent;
            if (a != null && !string.IsNullOrEmpty(a.activity))
                return "ColonyCodeW_ReportCoding".Translate(a.activity);
            return base.GetReport();
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOnBurningImmobile(TargetIndex.A);
            this.FailOn(() => Desk == null || !Desk.PowerOn);
            this.FailOn(() => Agent == null);

            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);

            var work = ToilMaker.MakeToil("ColonyCode_Code");
            work.initAction = () =>
            {
                nextEffect = 30;
                nextKey = Rand.Range(KeyIntervalMin, KeyIntervalMax);
            };
            work.tickIntervalAction = delta =>
            {
                pawn.rotationTracker.FaceTarget(job.GetTarget(TargetIndex.A));
                workedTicks += delta;
                var settings = ColonyCodeMod.Settings;
                if (settings == null || settings.flavorSkillXp)
                    pawn.skills?.Learn(SkillDefOf.Intellectual, 0.04f * delta);
                nextEffect -= delta;
                nextKey -= delta;
                if (nextEffect <= 0 || nextKey <= 0)
                {
                    var a = Agent;
                    if (nextEffect <= 0)
                    {
                        nextEffect = a != null ? ColonyEffects.ActivityInterval(a.state) : EffectInterval;
                        ColonyEffects.ThrowActivity(Desk, a);
                    }
                    if (nextKey <= 0)
                    {
                        nextKey = Rand.Range(KeyIntervalMin, KeyIntervalMax);
                        ColonyEffects.KeyboardTick(Desk, a);
                    }
                }
            };
            work.AddEndCondition(() =>
            {
                var a = Agent;
                if (a == null || !AgentRegistry.IsActive(a.state) || a.station == AgentStation.User) return JobCondition.Succeeded;
                if (workedTicks >= MaxTicks) return JobCondition.Succeeded;
                return JobCondition.Ongoing;
            });
            work.handlingFacing = true;
            work.defaultCompleteMode = ToilCompleteMode.Never;
            work.activeSkill = () => SkillDefOf.Intellectual;
            yield return work;
        }
    }

    /// <summary>
    /// A short celebration after a shipped goal: the resident spins on the spot, throwing sparkles and
    /// music notes, then goes back to its duty.
    /// </summary>
    public class JobDriver_Celebrate : JobDriver
    {
        public const int DurationTicks = 300;
        public const int StepTicks = 40;

        private int stepCounter;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            var dance = ToilMaker.MakeToil("ColonyCode_Celebrate");
            dance.initAction = () =>
            {
                stepCounter = 0;
                pawn.pather?.StopDead();
                if (pawn.Spawned) ColonyEffects.ThrowSparkles(pawn.DrawPos + new Vector3(0f, 0f, 0.4f), pawn.Map, 5);
            };
            dance.tickIntervalAction = delta =>
            {
                stepCounter += delta;
                if (stepCounter < StepTicks) return;
                stepCounter = 0;
                pawn.Rotation = pawn.Rotation.Rotated(RotationDirection.Clockwise);
                if (!pawn.Spawned) return;
                Vector3 loc = pawn.DrawPos + new Vector3(0f, 0f, 0.5f);
                if (Rand.Bool) ColonyEffects.ThrowNote(loc, pawn.Map);
                else ColonyEffects.ThrowSparkles(loc, pawn.Map, 2);
            };
            dance.handlingFacing = true;
            dance.defaultCompleteMode = ToilCompleteMode.Delay;
            dance.defaultDuration = DurationTicks;
            yield return dance;
        }
    }

    /// <summary>Stand at the decision terminal while the agent waits on the player.</summary>
    public class JobDriver_AwaitDecision : JobDriver
    {
        public const int MaxTicks = 5000;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);

            int waited = 0;
            var wait = ToilMaker.MakeToil("ColonyCode_AwaitDecision");
            wait.initAction = () => waited = 0;
            wait.tickIntervalAction = delta =>
            {
                pawn.rotationTracker.FaceTarget(job.GetTarget(TargetIndex.A));
                int before = waited;
                waited += delta;
                if (before / 300 != waited / 300 && pawn.Spawned)
                    MoteMaker.ThrowText(pawn.DrawPos + new Vector3(0f, 0f, 0.5f), pawn.Map, "!",
                        AgentStateUtility.StateColor(AgentState.WaitingUser), 1.5f);
            };
            wait.AddEndCondition(() =>
            {
                if (!ResidentAI.IsWaiting(AgentPawnUtility.AgentFor(pawn))) return JobCondition.Succeeded;
                return waited >= MaxTicks ? JobCondition.Succeeded : JobCondition.Ongoing;
            });
            wait.handlingFacing = true;
            wait.defaultCompleteMode = ToilCompleteMode.Never;
            yield return wait;
        }
    }
}
