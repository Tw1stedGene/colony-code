using System;
using System.Collections.Generic;
using ColonyCode.Agents;
using ColonyCode.Core;
using ColonyCode.Http;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ColonyCode.World
{
    /// <summary>
    /// Makes the colony feel alive: watches agent state changes (error puffs, done sparkles, lead to
    /// worker hand-offs as speech), goal results (speech, chime, celebration), decisions (bell),
    /// agent.say (speech bubble) and dyes residents' clothes in their name colour. Cosmetic only.
    /// RimWorld adds this component to every game automatically.
    /// </summary>
    public class GameComponent_ColonyLife : GameComponent
    {
        public const float PollIntervalSeconds = 0.25f;
        public const int LookIntervalTicks = 250;
        /// <summary>A resident that coded for a session within this many real seconds celebrates its goals.</summary>
        public const float CelebrateWorkedWithinSeconds = 900f;

        private class WorkRecord
        {
            public string sessionId;
            public float lastRealtime;
        }

        public static GameComponent_ColonyLife Instance { get; private set; }

        private float nextPoll;

        // Agents first seen in a quiet window are seeded silently: right after load (first poll) and
        // for a few seconds after any session change, when the plugin re-sends a snapshot of every
        // agent. Otherwise each busy worker would announce a delegation at once on load/reconnect.
        private const float QuietSeconds = 3f;
        private float quietUntil = -1f;
        private bool polledOnce;
        private static volatile bool sessionsChanged;
        static GameComponent_ColonyLife() { AgentRegistry.SessionsChanged += () => sessionsChanged = true; }
        private readonly Dictionary<string, AgentState> lastState = new Dictionary<string, AgentState>();
        private readonly Dictionary<string, string> goalStatus = new Dictionary<string, string>();
        private readonly Dictionary<Pawn, WorkRecord> worked = new Dictionary<Pawn, WorkRecord>();
        private readonly Dictionary<Pawn, int> dyedApparelCount = new Dictionary<Pawn, int>();
        private static readonly List<Pawn> tmpPawns = new List<Pawn>();

        public GameComponent_ColonyLife() { Instance = this; }

        public GameComponent_ColonyLife(Game game) { Instance = this; }

        public override void FinalizeInit()
        {
            Instance = this;
        }

        public override void GameComponentUpdate()
        {
            Instance = this;
            float now = Time.realtimeSinceStartup;
            if (now < nextPoll) return;
            nextPoll = now + PollIntervalSeconds;
            if (Current.ProgramState != ProgramState.Playing) return;
            try { Poll(now); }
            catch (Exception e) { Log.ErrorOnce("[ColonyCode] Colony effects failed: " + e, 0x6c0de10); }
        }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % LookIntervalTicks != 0) return;
            try { ApplyLooks(); }
            catch (Exception e) { Log.ErrorOnce("[ColonyCode] Resident looks failed: " + e, 0x6c0de11); }
        }

        // ------------------------------------------------------------------ agent state changes

        private void Poll(float now)
        {
            var reg = AgentRegistry.Instance;
            if (reg == null) return;
            if (!polledOnce || sessionsChanged)
            {
                polledOnce = true;
                sessionsChanged = false;
                quietUntil = now + QuietSeconds;
            }
            bool quiet = now < quietUntil;
            var agents = reg.Agents;
            for (int i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                if (a == null || a.id == null) continue;
                if (a.pawn != null && AgentRegistry.IsActive(a.state)) NoteWorked(a.pawn, a.sessionId, now);

                AgentState prev;
                if (!lastState.TryGetValue(a.id, out prev))
                {
                    lastState[a.id] = a.state;
                    if (!quiet && a.role == "worker" && AgentRegistry.IsActive(a.state)) OnWorkerJoined(reg, a);
                    continue;
                }
                if (prev == a.state) continue;
                lastState[a.id] = a.state;
                if (a.state == AgentState.Error) ColonyEffects.ErrorBurst(a);
                else if (a.state == AgentState.Done)
                {
                    ColonyEffects.DoneSparkle(a);
                    if (a.role == "worker" && AgentRegistry.IsActive(prev)) OnWorkerDone(reg, a);
                }
            }
        }

        private void NoteWorked(Pawn p, string sessionId, float now)
        {
            WorkRecord w;
            if (!worked.TryGetValue(p, out w))
            {
                w = new WorkRecord();
                worked[p] = w;
            }
            w.sessionId = sessionId;
            w.lastRealtime = now;
        }

        private static AgentInfo LeadOf(AgentRegistry reg, string sessionId)
        {
            var agents = reg.Agents;
            for (int i = 0; i < agents.Count; i++)
                if (agents[i].role == "lead" && agents[i].sessionId == sessionId) return agents[i];
            return null;
        }

        /// <summary>Spoken name: the resident's name, else the agent's display name.</summary>
        private static string SpeakerName(AgentInfo a)
        {
            if (a == null) return "ColonyCodeW_Teammate".Translate();
            if (a.pawn != null) return a.pawn.LabelShort;
            return a.displayName ?? a.id;
        }

        /// <summary>"general-purpose: Fix the tests" → "Fix the tests".</summary>
        private static string TaskOf(AgentInfo a)
        {
            string label = a.label;
            if (!string.IsNullOrEmpty(label))
            {
                int c = label.IndexOf(": ", StringComparison.Ordinal);
                return c >= 0 && c + 2 < label.Length ? label.Substring(c + 2) : label;
            }
            return a.activity ?? "";
        }

        /// <summary>A lead spawned a worker: "Juniper → Kit: fix the tests" over the lead (or the worker).</summary>
        private static void OnWorkerJoined(AgentRegistry reg, AgentInfo worker)
        {
            var lead = LeadOf(reg, worker.sessionId);
            Pawn speaker = lead?.pawn ?? worker.pawn;
            if (speaker == null) return;
            string task = TaskOf(worker);
            string line = string.IsNullOrEmpty(task)
                ? (string)"ColonyCodeW_SpeechDelegateShort".Translate(SpeakerName(lead), SpeakerName(worker))
                : (string)"ColonyCodeW_SpeechDelegate".Translate(SpeakerName(lead), SpeakerName(worker), task);
            ColonyEffects.Say(speaker, line, AgentLook.NameColor(speaker));
        }

        /// <summary>A worker finished: "Kit → Juniper: done!" over the worker (or the lead).</summary>
        private static void OnWorkerDone(AgentRegistry reg, AgentInfo worker)
        {
            var lead = LeadOf(reg, worker.sessionId);
            Pawn speaker = worker.pawn ?? lead?.pawn;
            if (speaker == null) return;
            ColonyEffects.Say(speaker, "ColonyCodeW_SpeechWorkerDone".Translate(SpeakerName(worker), SpeakerName(lead)),
                AgentLook.NameColor(speaker));
        }

        // ------------------------------------------------------------------ goals

        public void OnGoalChanged(GoalInfo g)
        {
            if (g == null || g.id == null) return;
            string prev;
            goalStatus.TryGetValue(g.id, out prev);
            goalStatus[g.id] = g.status;
            // Only real transitions: a goal first seen already done/failed (e.g. resent after a
            // reconnect) is not celebrated again.
            if (prev != "active" || g.status == prev) return;
            var reg = AgentRegistry.Instance;
            if (reg == null) return;
            var lead = string.IsNullOrEmpty(g.sessionId) ? null : LeadOf(reg, g.sessionId);
            string text = Short(g.text, 50);
            if (g.status == "done")
            {
                ColonyEffects.SuccessChime();
                Celebrate(g.sessionId);
                Pawn speaker = lead?.pawn ?? (tmpPawns.Count > 0 ? tmpPawns[0] : null);
                if (speaker != null)
                    ColonyEffects.Say(speaker, "ColonyCodeW_SpeechGoalDone".Translate(text), ColonyEffects.ColSparkle);
            }
            else if (g.status == "failed")
            {
                if (lead?.pawn != null)
                {
                    ColonyEffects.ErrorBurst(lead);
                    ColonyEffects.Say(lead.pawn, "ColonyCodeW_SpeechGoalFailed".Translate(text), ColonyEffects.ColError);
                }
            }
        }

        private static string Short(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\n', ' ').Trim();
            return s.Length > max ? s.Substring(0, max - 3) + "..." : s;
        }

        /// <summary>Residents that coded for this session recently do a short celebration (tmpPawns keeps them).</summary>
        private void Celebrate(string sessionId)
        {
            tmpPawns.Clear();
            float now = Time.realtimeSinceStartup;
            foreach (var kv in worked)
            {
                var p = kv.Key;
                if (p == null || p.Destroyed || p.Dead) continue;
                if (now - kv.Value.lastRealtime > CelebrateWorkedWithinSeconds) continue;
                if (!string.IsNullOrEmpty(sessionId) && kv.Value.sessionId != sessionId) continue;
                tmpPawns.Add(p);
            }
            bool thoughts = ColonyEffects.ThoughtsOn;
            for (int i = 0; i < tmpPawns.Count; i++)
            {
                var p = tmpPawns[i];
                StartCelebration(p);
                if (thoughts && ColonyCodeDefOf.ColonyCode_ShippedIt != null)
                    p.needs?.mood?.thoughts?.memories?.TryGainMemory(ColonyCodeDefOf.ColonyCode_ShippedIt);
            }
        }

        public static void StartCelebration(Pawn p)
        {
            var def = ColonyCodeDefOf.ColonyCode_Celebrate;
            if (def == null || p == null || !p.Spawned || p.Dead || p.Downed || p.jobs == null || p.InMentalState) return;
            if (p.Drafted || p.CurJobDef == def || p.CurJobDef == ColonyCodeDefOf.ColonyCode_AwaitDecision) return;
            if (ResidentAI.IsWaiting(AgentPawnUtility.AgentFor(p))) return;
            p.jobs.StartJob(JobMaker.MakeJob(def), JobCondition.InterruptForced, null, false, true);
        }

        // ------------------------------------------------------------------ other events

        public void OnDecisionOpened(DecisionInfo d)
        {
            ColonyEffects.DecisionBell();
        }

        public void OnBridgeEvent(Dictionary<string, object> ev)
        {
            if (Json.GetString(ev, "type") != "agent.say") return;
            // The registry already throws the text; add the speech bubble over the speaker.
            var a = AgentRegistry.Instance?.Get(Json.GetString(ev, "agentId"));
            if (a?.pawn != null) ColonyEffects.SpeechBubble(a.pawn);
        }

        // ------------------------------------------------------------------ looks

        /// <summary>Dyes each resident's torso apparel once, and again whenever its worn apparel count changes.</summary>
        private void ApplyLooks()
        {
            var reg = AgentRegistry.Instance;
            if (reg == null) return;
            var residents = reg.Residents;
            for (int i = 0; i < residents.Count; i++)
            {
                var r = residents[i];
                if (r == null || !r.Valid || !r.pawn.Spawned || r.pawn.apparel == null) continue;
                int count = r.pawn.apparel.WornApparelCount;
                int had;
                if (dyedApparelCount.TryGetValue(r.pawn, out had) && had == count) continue;
                AgentLook.ApplyApparelColor(r.pawn);
                dyedApparelCount[r.pawn] = count;
            }
            if (worked.Count > 64) worked.Clear();
        }
    }

    /// <summary>Subscribes the colony-life component to registry and bridge events once at startup.</summary>
    [StaticConstructorOnStartup]
    public static class ColonyLifeBootstrap
    {
        static ColonyLifeBootstrap()
        {
            AgentRegistry.GoalChanged += g => GameComponent_ColonyLife.Instance?.OnGoalChanged(g);
            AgentRegistry.DecisionOpened += d => GameComponent_ColonyLife.Instance?.OnDecisionOpened(d);
            GameComponent_ColonyCode.OnBridgeEvent += ev => GameComponent_ColonyLife.Instance?.OnBridgeEvent(ev);
        }
    }
}
