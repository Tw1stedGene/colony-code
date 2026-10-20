using System;
using System.Collections.Generic;
using ColonyCode.World;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ColonyCode.Agents
{
    /// <summary>What happened to the starting remote office of this game (saved).</summary>
    public enum RemoteOfficeState
    {
        /// <summary>Not generated: setting off, or a game from before this feature.</summary>
        None,
        Built,
        /// <summary>No flat, open site near the start spot.</summary>
        NoRoom,
        /// <summary>Pre-industrial player faction (tribal starts).</summary>
        SkippedTech,
    }

    public partial class AgentRegistry
    {
        private RemoteOfficeState officeState = RemoteOfficeState.None;
        private CellRect officeRect = CellRect.Empty;

        // Transient, only between GenStep_RemoteOffice and StartedNewGame (no save can happen in between).
        private Map pendingOfficeMap;
        private List<Building_AgentDesk> pendingOfficeDesks;
        private bool officeLetterPending;

        public RemoteOfficeState OfficeState => officeState;

        /// <summary>Outer rect of the starting office (walls included), or empty.</summary>
        public CellRect OfficeRect => officeRect;

        private void ExposeOffice()
        {
            Scribe_Values.Look(ref officeState, "officeState", RemoteOfficeState.None);
            Scribe_Values.Look(ref officeRect, "officeRect", CellRect.Empty);
        }

        /// <summary>Called by GenStep_RemoteOffice once the building is standing.</summary>
        public void NoteOfficeBuilt(Map map, BuiltOffice office)
        {
            officeState = RemoteOfficeState.Built;
            officeRect = office.rect;
            pendingOfficeMap = map;
            pendingOfficeDesks = new List<Building_AgentDesk>(office.desks);
            officeLetterPending = true;
        }

        public void NoteOfficeSkipped(RemoteOfficeState why)
        {
            officeState = why;
            officeLetterPending = why == RemoteOfficeState.NoRoom;
        }

        /// <summary>
        /// Runs at the end of Game.InitNewGame (after FinalizeInit, so the Remote Workers faction exists):
        /// finishes Networking, moves one resident into each office desk and sends one welcome letter.
        /// </summary>
        public override void StartedNewGame()
        {
            base.StartedNewGame();
            try { FinishOfficeSetup(); }
            catch (Exception e) { Log.Error("[ColonyCode] Remote office setup failed: " + e); }
            finally
            {
                pendingOfficeMap = null;
                pendingOfficeDesks = null;
                officeLetterPending = false;
            }
        }

        private void FinishOfficeSetup()
        {
            if (officeState == RemoteOfficeState.NoRoom && officeLetterPending)
            {
                Find.LetterStack.ReceiveLetter("ColonyCodeW_LetterOfficeNoRoomLabel".Translate(),
                    "ColonyCodeW_LetterOfficeNoRoomText".Translate(), LetterDefOf.NeutralEvent);
                return;
            }
            if (officeState != RemoteOfficeState.Built || pendingOfficeMap == null || pendingOfficeDesks == null) return;

            var research = ColonyCodeDefOf.ColonyCode_Networking;
            if (research != null && !research.IsFinished)
                Find.ResearchManager.FinishProject(research, false, null, false);

            var map = pendingOfficeMap;
            int now = Find.TickManager.TicksGame;
            // Colonists clean, repair and fight fires in the office like in any other building of theirs.
            var home = map.areaManager?.Home;
            if (home != null)
                foreach (var c in officeRect.ExpandedBy(1).Cells)
                    if (c.InBounds(map)) home[c] = true;
            var names = new List<string>();
            var targets = new List<Thing>();
            foreach (var desk in pendingOfficeDesks)
            {
                if (desk == null || !desk.Spawned || desk.Map != map || ResidentForDesk(desk) != null) continue;
                string name = NextResidentName();
                Pawn pawn = ResidentUtility.GenerateResident(map, name);
                if (pawn == null) break; // no faction or pawn kind; nothing more will work
                IntVec3 cell = desk.InteractionCell;
                if (!cell.Standable(map)) cell = CellFinder.StandableCellNear(desk.Position, map, 3f);
                if (!cell.IsValid) cell = desk.Position;
                residents.Add(new ResidentRecord { pawn = pawn, desk = desk, name = name, arrivedTick = now });
                GenSpawn.Spawn(pawn, cell, map, Rot4.South);
                ResidentUtility.EnsureInLord(pawn);
                names.Add(name);
                targets.Add(pawn);
            }
            if (names.Count == 0) return;

            // Offset the next arrival check so the first tick does not look at desks before power is up.
            nextArrivalTick = now + ArrivalIntervalTicks;
            var look = new List<GlobalTargetInfo>();
            look.Add(new GlobalTargetInfo(officeRect.CenterCell, map));
            foreach (var t in targets) look.Add(t);
            Find.LetterStack.ReceiveLetter("ColonyCodeW_LetterOfficeLabel".Translate(),
                "ColonyCodeW_LetterOfficeText".Translate(names.ToCommaList(true)), LetterDefOf.PositiveEvent,
                new LookTargets(look));
            Log.Message("[ColonyCode] Remote office online: " + string.Join(", ", names.ToArray()));
        }
    }
}
