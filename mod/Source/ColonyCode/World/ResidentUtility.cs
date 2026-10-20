using System.Collections.Generic;
using ColonyCode.Agents;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ColonyCode.World
{
    /// <summary>Faction, spawning, lord and leaving helpers for remote-worker residents.</summary>
    public static class ResidentUtility
    {
        private static Faction cachedFaction;

        /// <summary>The Remote Workers faction of the current game, or null if it does not exist (yet).</summary>
        public static Faction Faction
        {
            get
            {
                var def = ColonyCodeDefOf.ColonyCode_RemoteWorkers;
                if (def == null || Find.FactionManager == null) return null;
                if (cachedFaction != null && cachedFaction.def == def && Find.FactionManager.AllFactionsListForReading.Contains(cachedFaction))
                    return cachedFaction;
                cachedFaction = Find.FactionManager.FirstFactionOfDef(def);
                return cachedFaction;
            }
        }

        /// <summary>
        /// Creates the Remote Workers faction if the game has none (saves from before this version, or a
        /// world generated without it) and keeps it allied to the player. Hidden factions have no
        /// goodwill, so the relation only changes through SetRelationDirect; we reset it whenever it
        /// drifted (e.g. after friendly fire).
        /// </summary>
        public static Faction EnsureFaction()
        {
            var def = ColonyCodeDefOf.ColonyCode_RemoteWorkers;
            if (def == null || Find.FactionManager == null || Faction.OfPlayerSilentFail == null) return null;
            var f = Faction;
            if (f == null)
            {
                FactionGenerator.CreateFactionAndAddToManager(def);
                cachedFaction = null;
                f = Faction;
                if (f == null)
                {
                    Log.ErrorOnce("[ColonyCode] Could not create the Remote Workers faction.", 0x6c0de02);
                    return null;
                }
                Log.Message("[ColonyCode] Added the Remote Workers faction to this game.");
            }
            if (f.RelationKindWith(Faction.OfPlayer) != FactionRelationKind.Ally)
                f.SetRelationDirect(Faction.OfPlayer, FactionRelationKind.Ally, false);
            return f;
        }

        /// <summary>Generates an unarmed adult remote worker named <paramref name="nick"/>.</summary>
        public static Pawn GenerateResident(Map map, string nick)
        {
            var faction = EnsureFaction();
            var kind = ColonyCodeDefOf.ColonyCode_RemoteWorker;
            if (faction == null || kind == null) return null;
            var req = new PawnGenerationRequest(kind, faction, PawnGenerationContext.NonPlayer, tile: map.Tile,
                forceGenerateNewPawn: true, canGeneratePawnRelations: false, mustBeCapableOfViolence: false,
                colonistRelationChanceFactor: 0f, allowFood: false, allowAddictions: false,
                developmentalStages: DevelopmentalStage.Adult, dontGiveWeapon: true);
            Pawn pawn = PawnGenerator.GeneratePawn(req);
            var nt = pawn.Name as NameTriple;
            if (nt != null && !string.IsNullOrEmpty(nick)) pawn.Name = new NameTriple(nt.First, nick, nt.Last);
            EnsureHediff(pawn);
            return pawn;
        }

        public static void EnsureHediff(Pawn pawn)
        {
            var def = ColonyCodeDefOf.ColonyCode_RemoteLink;
            if (pawn?.health?.hediffSet == null || def == null || pawn.Dead) return;
            if (pawn.health.hediffSet.HasHediff(def)) return;
            pawn.health.AddHediff(def);
            pawn.needs?.AddOrRemoveNeedsAsAppropriate();
        }

        /// <summary>Walks the pawn in from the map edge, or drops it by pod near the desk if no edge cell is reachable.</summary>
        public static bool SpawnAtEdge(Pawn pawn, Map map, IntVec3 near)
        {
            IntVec3 cell;
            if (RCellFinder.TryFindRandomPawnEntryCell(out cell, map, CellFinder.EdgeRoadChance_Friendly))
            {
                GenSpawn.Spawn(pawn, cell, map);
                return true;
            }
            cell = near.IsValid ? near : DropCellFinder.TradeDropSpot(map);
            DropPodUtility.DropThingsNear(cell, map, new Thing[] { pawn }, 110, false, false, true);
            return true;
        }

        /// <summary>The resident lord on this map, created when missing.</summary>
        public static Lord ResidentLord(Map map, Faction faction)
        {
            foreach (var l in map.lordManager.lords)
                if (l.LordJob is LordJob_AgentResident && l.faction == faction) return l;
            return LordMaker.MakeNewLord(faction, new LordJob_AgentResident(), map);
        }

        /// <summary>
        /// Puts a spawned, standing resident into the resident lord of its map (after a load, a rescue, a
        /// map change or anything else that took it out).
        /// </summary>
        public static void EnsureInLord(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Faction == null) return;
            var lord = pawn.GetLord();
            if (lord != null && lord.LordJob is LordJob_AgentResident && lord.Map == pawn.Map) return;
            if (pawn.Downed) return; // join again once standing; downed residents keep their lord
            if (lord != null) lord.RemovePawn(pawn);
            var mine = ResidentLord(pawn.Map, pawn.Faction);
            if (!mine.CanAddPawn(pawn)) return;
            mine.AddPawn(pawn);
            // Drop whatever it was doing without a lord (e.g. the vanilla "leave the map" fallback).
            if (pawn.jobs?.curJob != null) pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, true);
        }

        /// <summary>A rescued resident becomes the colony's guest; clear that once it stands again.</summary>
        public static void ClearGuestStatus(Pawn pawn)
        {
            if (pawn?.guest == null || pawn.Downed || pawn.IsPrisoner || pawn.IsSlave) return;
            if (pawn.guest.HostFaction != null) pawn.guest.SetGuestStatus(null);
        }

        /// <summary>Hands the pawn to an exit-map lord. Called for residents without a desk for too long.</summary>
        public static void StartLeaving(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead) return;
            var lord = pawn.GetLord();
            if (lord != null) lord.RemovePawn(pawn);
            if (pawn.mindState != null) pawn.mindState.duty = null;
            LordMaker.MakeNewLord(pawn.Faction, new LordJob_ExitMapBest(LocomotionUrgency.Walk, false, true), pawn.Map,
                new List<Pawn> { pawn });
        }
    }
}
