using System;
using System.Collections.Generic;
using ColonyCode.Agents;
using ColonyCode.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyCode.World
{
    public enum OfficeSize { Small, Large }

    /// <summary>
    /// Map generation step (GenStepDef ColonyCode_RemoteOffice, added to Base_Player by
    /// Patches/ColonyCode_RemoteOffice.xml). On the starting map of a new industrial-or-later game it
    /// builds a small, powered, roofed office near the colonists' start spot. Residents are not spawned
    /// here: the office is recorded in the <see cref="AgentRegistry"/>, which spawns one resident per desk
    /// once the game has started (AgentRegistry.Office.cs).
    /// </summary>
    public class GenStep_RemoteOffice : GenStep
    {
        public override int SeedPart => 0x6c0de0f;

        public override void Generate(Map map, GenStepParams parms)
        {
            try
            {
                var reg = Current.Game?.GetComponent<AgentRegistry>();
                if (reg == null || !ShouldGenerate(map, reg)) return;
                var size = ColonyCodeMod.Settings.officeSize;
                if (Faction.OfPlayerSilentFail == null || Faction.OfPlayer.def.techLevel < TechLevel.Industrial)
                {
                    reg.NoteOfficeSkipped(RemoteOfficeState.SkippedTech);
                    Log.Message("[ColonyCode] Remote office skipped: the player faction is pre-industrial.");
                    return;
                }
                var built = RemoteOfficeBuilder.TryBuild(map, size);
                if (built == null)
                {
                    reg.NoteOfficeSkipped(RemoteOfficeState.NoRoom);
                    Log.Warning("[ColonyCode] Remote office skipped: no flat, open site 10-25 cells from the start spot.");
                    return;
                }
                reg.NoteOfficeBuilt(map, built);
                Log.Message("[ColonyCode] Remote office (" + size + ") built at " + built.rect + " with " + built.desks.Count + " desks.");
            }
            catch (Exception e)
            {
                Log.Error("[ColonyCode] Remote office generation failed: " + e);
            }
        }

        /// <summary>Only the starting map of a brand-new game, once, with the setting on.</summary>
        private static bool ShouldGenerate(Map map, AgentRegistry reg)
        {
            if (!ColonyCodeMod.Settings.startWithOffice) return false;
            if (reg.OfficeState != RemoteOfficeState.None) return false;
            // New-game creation (Game.InitNewGame): ProgramState is MapInitializing and InitData is set
            // until GameComponentUtility.StartedNewGame has run. Loading a save never generates a map.
            if (Current.ProgramState != ProgramState.MapInitializing || Find.GameInitData == null) return false;
            if (!map.IsStartingMap) return false;
            return MapGenerator.PlayerStartSpotValid;
        }
    }

    /// <summary>Result of a successful build, handed to the registry.</summary>
    public class BuiltOffice
    {
        public CellRect rect;
        public IntVec3 door;
        public List<Building_AgentDesk> desks = new List<Building_AgentDesk>();
        public List<Thing> things = new List<Thing>();
    }

    /// <summary>Site search and construction of the starting remote office.</summary>
    public static class RemoteOfficeBuilder
    {
        public const int MinDistance = 10;
        public const int MaxDistance = 25;
        public const int PreferredDistance = 14;
        /// <summary>Cells kept free between the office walls and the start spot (arrival pods, starting items).</summary>
        public const int MinGapToStart = 5;
        public const int MapEdgeMargin = 6;

        private enum Kind { Desk, Rack, Terminal, Board, Uplink, Lamp, Armchair, EndTable }

        /// <summary>One piece of furniture in the canonical frame (door on the south wall).</summary>
        private struct Piece
        {
            public Kind kind;
            public int x, z;
            /// <summary>Canonical footprint: 2 for desks and task boards (horizontal), else 1.</summary>
            public int w;
            /// <summary>Canonical rotation for pieces without an interaction cell.</summary>
            public Rot4 rot;
            public bool optional;

            public Piece(Kind kind, int x, int z, int w = 1, Rot4? rot = null, bool optional = false)
            {
                this.kind = kind; this.x = x; this.z = z; this.w = w; this.rot = rot ?? Rot4.South; this.optional = optional;
            }
        }

        private class Layout
        {
            public int width, height, doorX;
            public List<Piece> pieces = new List<Piece>();
            /// <summary>Uplink slots, used in order until the uplinks cover the office's power draw.</summary>
            public List<IntVec2> uplinkSlots = new List<IntVec2>();
            public List<IntVec2> conduits = new List<IntVec2>();
        }

        // Small: 9x7 outer, 7x5 inside.          Large: 13x9 outer, 11x7 inside.
        //  z5  D D U U D D R                       z7  D D R D D U D D R D D
        //  z4  . . . . . . .                       z6  . . . . . . . . . . .
        //  z3  . . . . . . T                       z5  . . . . . . . . . . U
        //  z2  . . . . . . U                       z4  L . . . . . . . . . T
        //  z1  B B . . . . L                       z3  A . . . . . . . . . U
        //       door at x=4                        z2  E . . . . . . . . . U
        //                                          z1  A . . . . . . B B . L
        //                                               door at x=6
        // D desk, R server rack, T decision terminal, B task board, U uplink slot, L lamp,
        // A armchair, E end table. Chairs (interaction cells) face the room.
        private static Layout SmallLayout()
        {
            var l = new Layout { width = 9, height = 7, doorX = 4 };
            l.pieces.Add(new Piece(Kind.Desk, 1, 5, 2));
            l.pieces.Add(new Piece(Kind.Desk, 5, 5, 2));
            l.pieces.Add(new Piece(Kind.Rack, 7, 5));
            l.pieces.Add(new Piece(Kind.Terminal, 7, 3));
            l.pieces.Add(new Piece(Kind.Board, 1, 1, 2, Rot4.North));
            l.pieces.Add(new Piece(Kind.Lamp, 7, 1));
            l.uplinkSlots.Add(new IntVec2(3, 5));
            l.uplinkSlots.Add(new IntVec2(4, 5));
            l.uplinkSlots.Add(new IntVec2(7, 2));
            for (int x = 1; x <= 7; x++) l.conduits.Add(new IntVec2(x, 5));
            for (int z = 1; z <= 4; z++) l.conduits.Add(new IntVec2(7, z));
            return l;
        }

        private static Layout LargeLayout()
        {
            var l = new Layout { width = 13, height = 9, doorX = 6 };
            l.pieces.Add(new Piece(Kind.Desk, 1, 7, 2));
            l.pieces.Add(new Piece(Kind.Rack, 3, 7));
            l.pieces.Add(new Piece(Kind.Desk, 4, 7, 2));
            l.pieces.Add(new Piece(Kind.Desk, 7, 7, 2));
            l.pieces.Add(new Piece(Kind.Rack, 9, 7));
            l.pieces.Add(new Piece(Kind.Desk, 10, 7, 2));
            l.pieces.Add(new Piece(Kind.Terminal, 11, 4));
            l.pieces.Add(new Piece(Kind.Board, 8, 1, 2, Rot4.North));
            l.pieces.Add(new Piece(Kind.Lamp, 11, 1));
            l.pieces.Add(new Piece(Kind.Lamp, 1, 4));
            // Lounge corner.
            l.pieces.Add(new Piece(Kind.Armchair, 1, 1, 1, Rot4.East, true));
            l.pieces.Add(new Piece(Kind.EndTable, 1, 2, 1, Rot4.East, true));
            l.pieces.Add(new Piece(Kind.Armchair, 1, 3, 1, Rot4.East, true));
            l.uplinkSlots.Add(new IntVec2(6, 7));
            l.uplinkSlots.Add(new IntVec2(11, 5));
            l.uplinkSlots.Add(new IntVec2(11, 3));
            l.uplinkSlots.Add(new IntVec2(11, 2));
            for (int x = 1; x <= 11; x++) l.conduits.Add(new IntVec2(x, 7));
            for (int z = 1; z <= 6; z++) l.conduits.Add(new IntVec2(11, z));
            for (int z = 4; z <= 6; z++) l.conduits.Add(new IntVec2(1, z));
            return l;
        }

        private static ThingDef DefFor(Kind k)
        {
            switch (k)
            {
                case Kind.Desk: return ColonyCodeDefOf.ColonyCode_AgentDesk;
                case Kind.Rack: return ColonyCodeDefOf.ColonyCode_ServerRack;
                case Kind.Terminal: return ColonyCodeDefOf.ColonyCode_DecisionTerminal;
                case Kind.Board: return ColonyCodeDefOf.ColonyCode_TaskBoard;
                case Kind.Uplink: return ColonyCodeDefOf.ColonyCode_OfficeUplink;
                case Kind.Lamp: return ThingDefOf.StandingLamp;
                case Kind.Armchair: return DefDatabase<ThingDef>.GetNamedSilentFail("Armchair");
                case Kind.EndTable: return DefDatabase<ThingDef>.GetNamedSilentFail("EndTable");
            }
            return null;
        }

        /// <summary>Watts drawn by a def (0 for producers and unpowered defs).</summary>
        private static float Draw(ThingDef def)
        {
            var p = def?.GetCompProperties<CompProperties_Power>();
            return p == null ? 0f : Mathf.Max(0f, p.PowerConsumption);
        }

        // ------------------------------------------------------------------ frame transform

        /// <summary>Maps canonical cells (door on the south wall) onto the world rect, door on <c>doorSide</c>.</summary>
        private struct Frame
        {
            public CellRect rect;
            public int w, h, turns;

            public Frame(CellRect rect, int w, int h, Rot4 doorSide)
            {
                this.rect = rect; this.w = w; this.h = h;
                // Rot4 ints grow clockwise (N0 E1 S2 W3); the canonical door faces south.
                turns = (doorSide.AsInt - Rot4.SouthInt + 4) % 4;
            }

            public IntVec3 Cell(int x, int z)
            {
                // Doubled, centred coordinates so even sizes rotate exactly.
                int u = 2 * x + 1 - w, v = 2 * z + 1 - h;
                for (int i = 0; i < turns; i++) { int t = u; u = v; v = -t; } // 90 degrees clockwise
                int ww = turns % 2 == 0 ? w : h, hh = turns % 2 == 0 ? h : w;
                return new IntVec3(rect.minX + (u + ww - 1) / 2, 0, rect.minZ + (v + hh - 1) / 2);
            }

            public Rot4 Rot(Rot4 canonical) => new Rot4((canonical.AsInt + turns) % 4);
        }

        // ------------------------------------------------------------------ entry

        public static BuiltOffice TryBuild(Map map, OfficeSize size)
        {
            var layout = size == OfficeSize.Large ? LargeLayout() : SmallLayout();
            if (ColonyCodeDefOf.ColonyCode_AgentDesk == null || ColonyCodeDefOf.ColonyCode_OfficeUplink == null) return null;
            if (!TryFindSite(map, layout, out CellRect rect, out Rot4 doorSide)) return null;
            return Build(map, layout, rect, doorSide);
        }

        // ------------------------------------------------------------------ site search

        private static bool TryFindSite(Map map, Layout layout, out CellRect best, out Rot4 bestSide)
        {
            best = default(CellRect);
            bestSide = Rot4.South;
            IntVec3 start = MapGenerator.PlayerStartSpot;
            var candidates = new List<IntVec3>();
            foreach (var c in CellRect.CenteredOn(start, MaxDistance).Cells)
            {
                float d = Dist(c, start);
                if (d >= MinDistance && d <= MaxDistance && c.InBounds(map)) candidates.Add(c);
            }
            // Closest to the preferred distance first, random among equals (map-gen Rand is seeded).
            var keys = new Dictionary<IntVec3, float>();
            foreach (var c in candidates) keys[c] = Mathf.Abs(Dist(c, start) - PreferredDistance) + Rand.Value;
            candidates.Sort((a, b) => keys[a].CompareTo(keys[b]));

            var used = MapGenerator.UsedRects;
            foreach (var c in candidates)
            {
                Rot4 side = SideFacing(c, start);
                bool vertical = side == Rot4.East || side == Rot4.West;
                int ww = vertical ? layout.height : layout.width, hh = vertical ? layout.width : layout.height;
                var rect = new CellRect(c.x - ww / 2, c.z - hh / 2, ww, hh);
                if (SiteOk(map, rect, start, used))
                {
                    best = rect;
                    bestSide = side;
                    return true;
                }
            }
            return false;
        }

        private static float Dist(IntVec3 a, IntVec3 b)
        {
            int dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>The wall of a rect centred on <paramref name="c"/> that faces <paramref name="target"/>.</summary>
        private static Rot4 SideFacing(IntVec3 c, IntVec3 target)
        {
            int dx = target.x - c.x, dz = target.z - c.z;
            if (Math.Abs(dx) > Math.Abs(dz)) return dx > 0 ? Rot4.East : Rot4.West;
            return dz > 0 ? Rot4.North : Rot4.South;
        }

        private static bool SiteOk(Map map, CellRect rect, IntVec3 start, List<CellRect> used)
        {
            var outer = rect.ExpandedBy(1);
            var bounds = CellRect.WholeMap(map).ContractedBy(MapEdgeMargin);
            if (!outer.FullyContainedWithin(bounds)) return false;
            if (rect.ExpandedBy(MinGapToStart).Contains(start)) return false;
            if (used != null)
                foreach (var u in used)
                    if (u.Overlaps(outer)) return false;
            foreach (var c in outer.Cells)
            {
                var terrain = c.GetTerrain(map);
                if (terrain == null || terrain.IsWater || terrain.passability == Traversability.Impassable) return false;
                if (c.Roofed(map)) return false;
                if (rect.Contains(c) && !c.SupportsStructureType(map, TerrainAffordanceDefOf.Heavy)) return false;
                if (c.GetEdifice(map) != null) return false;
                var things = map.thingGrid.ThingsListAtFast(c);
                for (int i = 0; i < things.Count; i++)
                    if (!Clearable(things[i])) return false;
            }
            return true;
        }

        /// <summary>Things the office may remove from its footprint: plants, chunks, filth, motes.</summary>
        private static bool Clearable(Thing t)
        {
            var def = t.def;
            switch (def.category)
            {
                case ThingCategory.Plant:
                case ThingCategory.Filth:
                case ThingCategory.Mote:
                case ThingCategory.Ethereal:
                case ThingCategory.Gas:
                    return true;
                case ThingCategory.Item:
                    return def.IsWithinCategory(ThingCategoryDefOf.Chunks);
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------ construction

        private static BuiltOffice Build(Map map, Layout layout, CellRect rect, Rot4 doorSide)
        {
            var frame = new Frame(rect, layout.width, layout.height, doorSide);
            var result = new BuiltOffice { rect = rect, door = frame.Cell(layout.doorX, 0) };
            var faction = Faction.OfPlayer;

            // 1. Clear the footprint and a one-cell margin (the doorstep stays walkable).
            var clear = new List<Thing>();
            foreach (var c in rect.ExpandedBy(1).Cells)
                foreach (var t in map.thingGrid.ThingsListAtFast(c))
                    if (Clearable(t) && !clear.Contains(t)) clear.Add(t);
            foreach (var t in clear)
                if (!t.Destroyed) t.Destroy(DestroyMode.Vanish);

            // 2. Floor, roof, walls, door.
            var floor = DefDatabase<TerrainDef>.GetNamedSilentFail("MetalTile") ?? DefDatabase<TerrainDef>.GetNamedSilentFail("Concrete");
            var interior = rect.ContractedBy(1);
            foreach (var c in interior.Cells)
            {
                if (floor != null) map.terrainGrid.SetTerrain(c, floor);
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            var steel = ThingDefOf.Steel;
            foreach (var c in rect.EdgeCells)
            {
                bool isDoor = c == result.door;
                var t = ThingMaker.MakeThing(isDoor ? ThingDefOf.Door : ThingDefOf.Wall, steel);
                Spawn(t, c, Rot4.North, map, faction, result);
            }

            // 3. Conduits under every powered building.
            foreach (var p in layout.conduits)
                Spawn(ThingMaker.MakeThing(ThingDefOf.PowerConduit), frame.Cell(p.x, p.z), Rot4.North, map, faction, result);

            // 4. Furniture. Pieces with an interaction cell get the rotation whose cell is inside and free.
            var blocked = new HashSet<IntVec3>();
            var pieces = new List<Piece>(layout.pieces);
            float draw = 0f;
            foreach (var p in pieces) draw += Draw(DefFor(p.kind));
            var uplinkDef = ColonyCodeDefOf.ColonyCode_OfficeUplink;
            float output = -(uplinkDef.GetCompProperties<CompProperties_Power>()?.PowerConsumption ?? 0f);
            int uplinks = output > 0f ? Mathf.CeilToInt(draw / output) : 1;
            uplinks = Mathf.Clamp(uplinks, 1, layout.uplinkSlots.Count);
            if (output * uplinks < draw)
                Log.Warning("[ColonyCode] Remote office uplinks give " + output * uplinks + " W for a draw of " + draw + " W.");
            for (int i = 0; i < uplinks; i++)
                pieces.Add(new Piece(Kind.Uplink, layout.uplinkSlots[i].x, layout.uplinkSlots[i].z));

            foreach (var p in pieces)
                for (int dx = 0; dx < p.w; dx++) blocked.Add(frame.Cell(p.x + dx, p.z));
            // The walk from the door into the room stays clear.
            var path = frame.Cell(layout.doorX, 1);

            foreach (var p in pieces)
            {
                var def = DefFor(p.kind);
                if (def == null)
                {
                    if (!p.optional) Log.Warning("[ColonyCode] Remote office: no def for " + p.kind);
                    continue;
                }
                var cells = new List<IntVec3>();
                for (int dx = 0; dx < p.w; dx++) cells.Add(frame.Cell(p.x + dx, p.z));
                var target = CellRect.FromCellList(cells);
                if (!TryPlacement(def, target, frame.Rot(p.rot), interior, blocked, path, map, out IntVec3 center, out Rot4 rot))
                {
                    Log.Warning("[ColonyCode] Remote office: could not place " + def.defName + " at " + target);
                    continue;
                }
                ThingDef stuff = null;
                if (def.MadeFromStuff)
                    stuff = System.Linq.Enumerable.Contains(GenStuff.AllowedStuffsFor(def), steel) ? steel : GenStuff.DefaultStuffFor(def);
                var thing = ThingMaker.MakeThing(def, stuff);
                Spawn(thing, center, rot, map, faction, result);
                if (thing is Building_AgentDesk desk && thing.Spawned)
                {
                    result.desks.Add(desk);
                    SpawnChair(desk, steel, map, faction, result);
                    blocked.Add(desk.InteractionCell);
                }
            }
            result.desks.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));

            // 5. Later gen steps keep out; fog generation reveals the room.
            MapGenerator.UsedRects?.Add(rect.ExpandedBy(1));
            MapGenerator.rootsToUnfog?.Add(interior.CenterCell);
            return result;
        }

        /// <summary>
        /// Finds a centre and rotation for <paramref name="def"/> that occupy exactly <paramref name="target"/>.
        /// For defs with an interaction cell, that cell must be inside the room and not blocked.
        /// </summary>
        private static bool TryPlacement(ThingDef def, CellRect target, Rot4 preferred, CellRect interior,
            HashSet<IntVec3> blocked, IntVec3 doorPath, Map map, out IntVec3 center, out Rot4 rot)
        {
            center = IntVec3.Invalid;
            rot = preferred;
            var rots = new List<Rot4> { preferred };
            if (def.rotatable || def.hasInteractionCell)
                for (int i = 0; i < 4; i++) { var r = new Rot4(i); if (r != preferred) rots.Add(r); }
            foreach (var r in rots)
            {
                foreach (var c in target.Cells)
                {
                    if (GenAdj.OccupiedRect(c, r, def.size) != target) continue;
                    if (def.hasInteractionCell)
                    {
                        // The exact cell, not InteractionCellWhenAt: that one silently falls back to another
                        // cell when the real one is blocked (e.g. by the wall just built), which let desks face
                        // the wall while their pawns stood outside.
                        var ic = ThingUtility.InteractionCell(def.interactionCellOffset, c, r);
                        if (!interior.Contains(ic) || blocked.Contains(ic) || ic == doorPath) continue;
                    }
                    center = c;
                    rot = r;
                    return true;
                }
                if (!def.rotatable && !def.hasInteractionCell) break;
            }
            return false;
        }

        /// <summary>A dining chair on the desk's interaction cell, facing the desk.</summary>
        private static void SpawnChair(Building_AgentDesk desk, ThingDef stuff, Map map, Faction faction, BuiltOffice result)
        {
            var chairDef = DefDatabase<ThingDef>.GetNamedSilentFail("DiningChair");
            if (chairDef == null) return;
            var cell = desk.InteractionCell;
            if (!cell.InBounds(map) || cell.GetEdifice(map) != null) return;
            if (chairDef.MadeFromStuff && !System.Linq.Enumerable.Contains(GenStuff.AllowedStuffsFor(chairDef), stuff))
                stuff = GenStuff.DefaultStuffFor(chairDef);
            var chair = ThingMaker.MakeThing(chairDef, chairDef.MadeFromStuff ? stuff : null);
            // A seated pawn faces the chair's rotation; the desk lies in the direction of the desk's rotation.
            Spawn(chair, cell, desk.Rotation, map, faction, result);
        }

        private static void Spawn(Thing thing, IntVec3 cell, Rot4 rot, Map map, Faction faction, BuiltOffice result)
        {
            if (thing.def.CanHaveFaction) thing.SetFaction(faction);
            GenSpawn.Spawn(thing, cell, map, rot, WipeMode.Vanish);
            result.things.Add(thing);
        }
    }
}
