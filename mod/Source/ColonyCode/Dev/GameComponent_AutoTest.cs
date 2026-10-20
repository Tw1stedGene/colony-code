using ColonyCode.Agents;
using ColonyCode.World;
using RimWorld;
using Verse;

namespace ColonyCode.Dev
{
    /// <summary>
    /// Dev-only smoke test, active only when the game is launched with <c>-colonycodetest</c>
    /// (usually together with <c>-quicktest</c>, which starts a Crashlanded game on Base_Player map
    /// generation, so the starting remote office is generated as usual when the setting is on).
    /// If the game has a starting remote office, it only logs the office and its residents, jumps the
    /// camera there and runs at superfast speed: the real office, power and residents are exercised.
    /// Otherwise it finishes the Networking research, turns on unlimited power, builds two agent desks,
    /// a decision terminal and a server rack on a conduit line near the map centre, so residents arrive
    /// without any clicks. Progress is logged with the prefix [ColonyCodeTest].
    /// </summary>
    public class GameComponent_AutoTest : GameComponent
    {
        private const string Arg = "colonycodetest";
        private const int SetupDelayTicks = 120;

        private const float ShotIntervalSeconds = 10f;
        private const int MaxShots = 30;

        private bool done;
        private int frames;
        private float nextShot;
        private int shotIndex;

        public GameComponent_AutoTest(Game game) { }

        public static bool Enabled => GenCommandLine.CommandLineArgPassed(Arg);

        // Update, not Tick: a quicktest game starts paused, and ticks do not run while paused.
        public override void GameComponentUpdate()
        {
            if (!Enabled) return;
            Map map = Find.CurrentMap;
            if (map == null) return;
            if (!done && ++frames >= SetupDelayTicks)
            {
                done = true;
                try
                {
                    Setup(map);
                }
                catch (System.Exception ex)
                {
                    Log.Error("[ColonyCodeTest] setup failed: " + ex);
                }
            }
            if (done) TakeShotIfDue();
        }

        // Screenshots go to ~/.colony-code/shots/shot_NN.png (cycling), so a test run can be inspected
        // without the OS screen-recording permission an external capture tool would need.
        private void TakeShotIfDue()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < nextShot) return;
            nextShot = now + ShotIntervalSeconds;
            string dir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".colony-code", "shots");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "shot_" + (shotIndex % MaxShots).ToString("00") + ".png");
            shotIndex++;
            UnityEngine.ScreenCapture.CaptureScreenshot(path);
            Log.Message("[ColonyCodeTest] screenshot " + path);
        }

        private static void Setup(Map map)
        {
            var reg = AgentRegistry.Instance;
            if (reg != null && reg.OfficeState == RemoteOfficeState.Built)
            {
                LogOffice(reg, map);
                return;
            }
            Log.Message("[ColonyCodeTest] no starting remote office (state=" + (reg?.OfficeState.ToString() ?? "no registry") +
                        "), building the test desks");
            DebugSettings.unlimitedPower = true;
            var research = ColonyCodeDefOf.ColonyCode_Networking;
            if (research != null && !research.IsFinished)
                Find.ResearchManager.FinishProject(research, false, null, false);

            if (!TryFindSite(map, out IntVec3 origin))
            {
                Log.Error("[ColonyCodeTest] no clear 10x4 site near the map centre");
                return;
            }

            // Row at origin.z: desk (2 wide), gap, desk, gap, terminal, gap, rack; conduits under the row.
            for (int dx = 0; dx <= 8; dx++)
                Place(ThingDefOf.PowerConduit, origin + new IntVec3(dx, 0, 0), map);
            Place(ColonyCodeDefOf.ColonyCode_AgentDesk, origin, map);
            Place(ColonyCodeDefOf.ColonyCode_AgentDesk, origin + new IntVec3(3, 0, 0), map);
            Place(ColonyCodeDefOf.ColonyCode_DecisionTerminal, origin + new IntVec3(6, 0, 0), map);
            Place(ColonyCodeDefOf.ColonyCode_ServerRack, origin + new IntVec3(8, 0, 0), map);

            Find.CameraDriver.JumpToCurrentMapLoc(origin + new IntVec3(4, 0, 0));
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
            Log.Message("[ColonyCodeTest] setup done at " + origin + "; research finished=" +
                (research != null && research.IsFinished) + ", unlimitedPower=" + DebugSettings.unlimitedPower);
        }

        private static void LogOffice(AgentRegistry reg, Map map)
        {
            var rect = reg.OfficeRect;
            int desks = 0, powered = 0, uplinks = 0, seatsInside = 0, chairs = 0;
            var interior = rect.ContractedBy(1);
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                if (!rect.Contains(b.Position)) continue;
                if (b is Building_AgentDesk d)
                {
                    desks++;
                    if (d.PowerOn) powered++;
                    if (interior.Contains(d.InteractionCell)) seatsInside++;
                    var seat = d.InteractionCell.GetEdifice(map);
                    if (seat != null && seat.def.building != null && seat.def.building.isSittable) chairs++;
                }
                if (b.def == ColonyCodeDefOf.ColonyCode_OfficeUplink) uplinks++;
            }
            var sb = new System.Text.StringBuilder();
            foreach (var r in reg.Residents)
                sb.Append(r.name).Append(r.Valid ? "" : " (invalid)").Append(" @ ")
                  .Append(r.desk != null ? r.desk.Position.ToString() : "no desk").Append("; ");
            var research = ColonyCodeDefOf.ColonyCode_Networking;
            Log.Message("[ColonyCodeTest] starting office at " + rect + ": desks=" + desks + " (powered " + powered +
                        ", seats inside " + seatsInside + ", chairs " + chairs +
                        "), uplinks=" + uplinks + ", research finished=" + (research != null && research.IsFinished) +
                        ", residents: " + sb);
            Find.CameraDriver.JumpToCurrentMapLoc(rect.CenterCell);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
        }

        private static void Place(ThingDef def, IntVec3 cell, Map map)
        {
            if (def == null) return;
            Thing thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
            thing.SetFaction(Faction.OfPlayer);
            GenSpawn.Spawn(thing, cell, map, Rot4.North, WipeMode.Vanish);
        }

        private static bool TryFindSite(Map map, out IntVec3 origin)
        {
            IntVec3 center = map.Center;
            for (int radius = 0; radius < 60; radius++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        if (System.Math.Abs(dx) != radius && System.Math.Abs(dz) != radius) continue;
                        IntVec3 c = center + new IntVec3(dx, 0, dz);
                        if (SiteClear(c, map))
                        {
                            origin = c;
                            return true;
                        }
                    }
                }
            }
            origin = IntVec3.Invalid;
            return false;
        }

        private static bool SiteClear(IntVec3 origin, Map map)
        {
            for (int dx = -1; dx <= 10; dx++)
            {
                for (int dz = -2; dz <= 1; dz++)
                {
                    IntVec3 c = origin + new IntVec3(dx, 0, dz);
                    if (!c.InBounds(map) || !c.Standable(map) || c.GetEdifice(map) != null) return false;
                    if (c.GetTerrain(map).IsWater || c.Roofed(map) && c.GetRoof(map).isThickRoof) return false;
                }
            }
            return true;
        }
    }
}
