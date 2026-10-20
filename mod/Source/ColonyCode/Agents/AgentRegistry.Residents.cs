using System;
using System.Collections.Generic;
using ColonyCode.Http;
using ColonyCode.World;
using RimWorld;
using Verse;

namespace ColonyCode.Agents
{
    /// <summary>A remote-worker pawn living in the colony, and its desk. Saved with the registry.</summary>
    public class ResidentRecord : IExposable
    {
        public Pawn pawn;
        public Building_AgentDesk desk;
        /// <summary>Roster name (the pawn's nickname).</summary>
        public string name;
        /// <summary>Agent this resident embodied last; it gets the same resident back after a reload.</summary>
        public string lastAgentId;
        public int arrivedTick;
        /// <summary>Tick since which the desk has had no power, or -1.</summary>
        public int unpoweredSinceTick = -1;
        /// <summary>Tick since which the resident has had no desk, or -1.</summary>
        public int noDeskSinceTick = -1;
        /// <summary>Walking off the map (no desk for too long). No longer counts as a resident.</summary>
        public bool leaving;

        public bool Valid => pawn != null && !pawn.Dead && !pawn.Destroyed && !leaving;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_References.Look(ref desk, "desk");
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref lastAgentId, "lastAgentId");
            Scribe_Values.Look(ref arrivedTick, "arrivedTick");
            Scribe_Values.Look(ref unpoweredSinceTick, "unpoweredSinceTick", -1);
            Scribe_Values.Look(ref noDeskSinceTick, "noDeskSinceTick", -1);
            Scribe_Values.Look(ref leaving, "leaving");
        }
    }

    public partial class AgentRegistry
    {
        /// <summary>A desk unpowered this long loses its resident.</summary>
        public const int DeskUnpoweredGraceTicks = 15000;
        /// <summary>A resident without a desk this long walks off the map (one in-game day).</summary>
        public const int NoDeskLeaveTicks = 60000;
        /// <summary>Grace for a resident still in a drop pod before it counts as gone.</summary>
        public const int ArrivalGraceTicks = 2500;

        public IReadOnlyList<ResidentRecord> Residents => residents;

        public ResidentRecord ResidentFor(Pawn pawn)
        {
            if (pawn == null) return null;
            for (int i = 0; i < residents.Count; i++)
                if (residents[i].pawn == pawn) return residents[i];
            return null;
        }

        public bool IsResident(Pawn pawn)
        {
            var r = ResidentFor(pawn);
            return r != null && r.Valid;
        }

        public ResidentRecord ResidentForDesk(Building_AgentDesk desk)
        {
            if (desk == null) return null;
            for (int i = 0; i < residents.Count; i++)
                if (residents[i].desk == desk && residents[i].Valid) return residents[i];
            return null;
        }

        /// <summary>The agent embodied by the resident of this desk, or null.</summary>
        public AgentInfo AgentForDesk(Building_AgentDesk desk)
        {
            var r = ResidentForDesk(desk);
            return r == null ? null : GetForPawn(r.pawn);
        }

        public int ActiveResidentCount()
        {
            int n = 0;
            foreach (var r in residents) if (r.Valid) n++;
            return n;
        }

        // ------------------------------------------------------------------ agent <-> resident mapping

        /// <summary>
        /// Leads of live sessions keep a resident until session.end; workers get one while busy and give
        /// it back after <see cref="WorkerReleaseMs"/> done/idle. Residents are handed out in desk order
        /// (lowest desk id first). An agent that finds no free resident has no body ("working remotely").
        /// </summary>
        public void Remap()
        {
            if (Current.ProgramState != ProgramState.Playing) return;
            long now = Json.NowMs();

            // 1. Release bodies that are gone or no longer wanted.
            var taken = new HashSet<Pawn>();
            foreach (var a in agents)
            {
                if (a.pawn == null) continue;
                if (!IsResident(a.pawn) || !WantsBody(a, now) || taken.Contains(a.pawn)) Release(a);
                else taken.Add(a.pawn);
            }

            // 2. Hand out free residents: leads first, then workers, each in creation order.
            List<AgentInfo> waiting = null;
            foreach (var a in agents)
            {
                if (a.pawn != null || !WantsBody(a, now)) continue;
                if (waiting == null) waiting = new List<AgentInfo>();
                waiting.Add(a);
            }
            if (waiting == null) return;
            waiting.Sort((x, y) =>
            {
                int rx = x.role == "lead" ? 0 : 1, ry = y.role == "lead" ? 0 : 1;
                return rx != ry ? rx.CompareTo(ry) : x.seq.CompareTo(y.seq);
            });

            var ordered = OrderedResidents();
            foreach (var a in waiting)
            {
                ResidentRecord pick = null;
                foreach (var r in ordered)
                    if (!taken.Contains(r.pawn) && r.lastAgentId == a.id) { pick = r; break; }
                if (pick == null)
                    foreach (var r in ordered)
                        if (!taken.Contains(r.pawn)) { pick = r; break; }
                if (pick == null && a.role == "lead")
                {
                    // A lead without a body takes the resident of the newest worker.
                    AgentInfo victim = null;
                    foreach (var w in agents)
                        if (w.role == "worker" && w.pawn != null && (victim == null || w.seq > victim.seq)) victim = w;
                    if (victim != null)
                    {
                        pick = ResidentFor(victim.pawn);
                        taken.Remove(victim.pawn);
                        Release(victim);
                    }
                }
                if (pick == null) continue;
                Assign(a, pick);
                taken.Add(pick.pawn);
            }
        }

        private bool WantsBody(AgentInfo a, long now)
        {
            if (a.sessionId != null && endedSessions.Contains(a.sessionId)) return false;
            if (a.role == "lead") return true;
            bool resting = a.state == AgentState.Idle || a.state == AgentState.Done;
            if (!resting) return true;
            return a.idleSinceMs > 0 && now - a.idleSinceMs < WorkerReleaseMs;
        }

        private List<ResidentRecord> OrderedResidents()
        {
            var list = new List<ResidentRecord>();
            foreach (var r in residents) if (r.Valid) list.Add(r);
            list.Sort((x, y) =>
            {
                int dx = x.desk != null ? x.desk.thingIDNumber : int.MaxValue;
                int dy = y.desk != null ? y.desk.thingIDNumber : int.MaxValue;
                return dx != dy ? dx.CompareTo(dy) : x.arrivedTick.CompareTo(y.arrivedTick);
            });
            return list;
        }

        private void Assign(AgentInfo a, ResidentRecord r)
        {
            a.pawn = r.pawn;
            r.lastAgentId = a.id;
            RefreshDisplayName(a);
            Touch(a);
            ResidentAI.RefreshDuty(r.pawn, true);
        }

        private void Release(AgentInfo a)
        {
            var p = a.pawn;
            a.pawn = null;
            RefreshDisplayName(a);
            Touch(a);
            if (p != null) ResidentAI.RefreshDuty(p, true);
        }

        // ------------------------------------------------------------------ resident upkeep (game ticks)

        private void PruneResidentRecords()
        {
            if (residents == null) return;
            residents.RemoveAll(r => r == null || r.pawn == null || r.pawn.Dead || r.pawn.Discarded);
        }

        /// <summary>Every <see cref="MaintenanceIntervalTicks"/>: faction relation, records, desks, lords, duties.</summary>
        private void MaintainResidents(int now)
        {
            var faction = ResidentUtility.EnsureFaction();
            if (faction == null) return;
            bool changed = false;
            for (int i = residents.Count - 1; i >= 0; i--)
            {
                var r = residents[i];
                var p = r?.pawn;
                if (p == null || p.Dead || p.Destroyed || p.Faction != faction || p.IsPrisoner || p.IsSlave)
                {
                    if (p != null && p.Dead && r != null && !r.leaving)
                        Messages.Message("ColonyCodeW_ResidentDied".Translate(r.name ?? p.LabelShort), MessageTypeDefOf.NegativeEvent, false);
                    residents.RemoveAt(i);
                    changed = true;
                    continue;
                }
                if (!p.SpawnedOrAnyParentSpawned)
                {
                    // Left the map (walked off, or carried away). A pawn still in its drop pod gets a grace period.
                    if (r.leaving || now - r.arrivedTick > ArrivalGraceTicks)
                    {
                        residents.RemoveAt(i);
                        changed = true;
                    }
                    continue;
                }
                if (r.leaving) continue;
                ResidentUtility.EnsureHediff(p);
                if (!p.Spawned) continue; // carried, or in a drop pod

                UpdateDesk(r, now);
                if (r.desk == null)
                {
                    if (r.noDeskSinceTick < 0) r.noDeskSinceTick = now;
                    else if (now - r.noDeskSinceTick > NoDeskLeaveTicks)
                    {
                        r.leaving = true;
                        changed = true;
                        Messages.Message("ColonyCodeW_ResidentLeaves".Translate(r.name ?? p.LabelShort), p, MessageTypeDefOf.NeutralEvent, false);
                        ResidentUtility.StartLeaving(p);
                        continue;
                    }
                }
                else r.noDeskSinceTick = -1;

                ResidentUtility.ClearGuestStatus(p);
                ResidentUtility.EnsureInLord(p);
                ResidentAI.RefreshDuty(p, false);
            }
            if (changed) Remap();
        }

        /// <summary>Drops a destroyed or long-unpowered desk; a deskless resident takes a free powered desk on its map.</summary>
        private void UpdateDesk(ResidentRecord r, int now)
        {
            var p = r.pawn;
            var desk = r.desk;
            if (desk != null && (desk.Destroyed || !desk.Spawned || desk.Map != p.Map || desk.Faction != Faction.OfPlayer))
            {
                r.desk = desk = null;
                r.unpoweredSinceTick = -1;
            }
            if (desk != null)
            {
                if (desk.PowerOn) r.unpoweredSinceTick = -1;
                else if (r.unpoweredSinceTick < 0) r.unpoweredSinceTick = now;
                else if (now - r.unpoweredSinceTick > DeskUnpoweredGraceTicks)
                {
                    r.desk = null;
                    r.unpoweredSinceTick = -1;
                }
            }
            if (r.desk == null)
            {
                var free = FreePoweredDesk(p.Map);
                if (free != null)
                {
                    r.desk = free;
                    r.unpoweredSinceTick = -1;
                    ResidentAI.RefreshDuty(p, true);
                }
            }
        }

        private static List<Building_AgentDesk> PoweredDesks(Map map)
        {
            var res = new List<Building_AgentDesk>();
            var def = ColonyCodeDefOf.ColonyCode_AgentDesk;
            if (def == null || map == null) return res;
            foreach (var b in map.listerBuildings.AllBuildingsColonistOfDef(def))
            {
                var d = b as Building_AgentDesk;
                if (d != null && d.Spawned && d.PowerOn) res.Add(d);
            }
            res.Sort((x, y) => x.thingIDNumber.CompareTo(y.thingIDNumber));
            return res;
        }

        private Building_AgentDesk FreePoweredDesk(Map map)
        {
            foreach (var d in PoweredDesks(map))
                if (ResidentForDesk(d) == null) return d;
            return null;
        }

        /// <summary>
        /// Every <see cref="ArrivalIntervalTicks"/>: once Networking is researched, one new resident walks
        /// in when a home map has a powered desk without a resident, up to <see cref="AgentPawnCap"/>.
        /// </summary>
        private void TryArrival(int now)
        {
            var research = ColonyCodeDefOf.ColonyCode_Networking;
            if (research == null || !research.IsFinished) return;
            int count = ActiveResidentCount();
            if (count >= AgentPawnCap()) return;

            int powered = 0;
            Map map = null;
            Building_AgentDesk desk = null;
            foreach (var m in Find.Maps)
            {
                if (!m.IsPlayerHome) continue;
                powered += PoweredDesks(m).Count;
                if (desk == null)
                {
                    desk = FreePoweredDesk(m);
                    if (desk != null) map = m;
                }
            }
            if (desk == null || count >= powered) return;

            string name = NextResidentName();
            Pawn pawn = ResidentUtility.GenerateResident(map, name);
            if (pawn == null) return;
            var rec = new ResidentRecord { pawn = pawn, desk = desk, name = name, arrivedTick = now };
            residents.Add(rec);
            ResidentUtility.SpawnAtEdge(pawn, map, desk.InteractionCell);
            if (pawn.Spawned) ResidentUtility.EnsureInLord(pawn);

            Find.LetterStack.ReceiveLetter("ColonyCodeW_LetterArrivalLabel".Translate(name),
                "ColonyCodeW_LetterArrivalText".Translate(name), LetterDefOf.PositiveEvent, new LookTargets(pawn));
            Remap();
        }

        private string NextResidentName()
        {
            var used = new HashSet<string>();
            foreach (var r in residents) if (r.Valid && r.name != null) used.Add(r.name);
            foreach (var n in Roster) if (!used.Contains(n)) return n;
            for (int k = 2; ; k++)
                foreach (var n in Roster)
                    if (!used.Contains(n + " " + k)) return n + " " + k;
        }
    }
}
