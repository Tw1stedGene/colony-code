using System;
using System.Collections.Generic;
using System.IO;
using ColonyCode.Agents;
using ColonyCode.UI;
using ColonyCode.World;
using RimWorld;
using Verse;

namespace ColonyCode.Dev
{
    /// <summary>
    /// Dev-only: with <c>-colonycodeshowcase</c> (plus <c>-quicktest -colonycodetest</c> and the replay
    /// scenario running), opens each Colony Code screen in turn and saves a full-resolution
    /// screenshot to ~/.colony-code/shots/showcase_*.png for the Steam Workshop page. Each step waits
    /// until its screen has something to show; the decision step answers the decision afterwards so
    /// the replay goes on. Progress is logged with the prefix [ColonyCodeShowcase].
    /// </summary>
    public static class ShowcaseDirector
    {
        private const string Arg = "colonycodeshowcase";
        private const float SettleSeconds = 2.5f;
        private const float StepTimeoutSeconds = 240f;

        private enum Step { Office, Monitor, Decision, TaskBoard, AgentCard, Review, Done }

        private static Step step = Step.Office;
        private static float stepStarted = -1f;
        private static float shotAt = -1f;
        private static string pendingShot;
        private static Action afterShot;
        /// <summary>When the after-shot cleanup may run: CaptureScreenshot grabs the end of the frame,
        /// so closing windows in the same frame would take them out of the picture.</summary>
        private static float cleanupAt = -1f;

        private static bool monitorSelected;
        private static float monitorTabAt;

        public static bool Enabled => GenCommandLine.CommandLineArgPassed(Arg);

        /// <summary>Called every frame by the auto-test once the office is set up.</summary>
        public static void Update(Map map, CellRect office)
        {
            if (!Enabled || step == Step.Done || map == null) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (stepStarted < 0f) stepStarted = now;

            if (cleanupAt >= 0f)
            {
                if (now < cleanupAt) return;
                cleanupAt = -1f;
                var then = afterShot;
                afterShot = null;
                try { then?.Invoke(); } catch (Exception e) { Log.Warning("[ColonyCodeShowcase] after-shot failed: " + e.Message); }
                Next(now);
                return;
            }

            if (pendingShot != null)
            {
                if (now < shotAt) return;
                Capture(pendingShot);
                pendingShot = null;
                cleanupAt = now + 1f;
                return;
            }

            if (now - stepStarted > StepTimeoutSeconds)
            {
                Log.Warning("[ColonyCodeShowcase] step " + step + " timed out, skipping");
                Next(now);
                return;
            }

            var reg = AgentRegistry.Instance;
            switch (step)
            {
                case Step.Office:
                    if (now - stepStarted < 8f) return;
                    CloseOurWindows();
                    Frame(office, 13f);
                    Shoot("office");
                    break;

                case Step.Monitor:
                {
                    var desk = DeskWithBusyAgent(map);
                    if (desk == null) return;
                    CloseOurWindows();
                    Frame(office, 11f);
                    if (!monitorSelected)
                    {
                        // Select only; RimWorld opens the inspect pane itself on the next frames.
                        Find.Selector.ClearSelection();
                        Find.Selector.Select(desk, false, false);
                        monitorSelected = true;
                        monitorTabAt = now + 1f;
                        return;
                    }
                    if (now < monitorTabAt) return;
                    if (MainButtonDefOf.Inspect.TabWindow is MainTabWindow_Inspect inspect)
                        inspect.OpenTabType = typeof(ITab_AgentMonitor);
                    Shoot("monitor");
                    afterShot = () => { Find.Selector.ClearSelection(); Find.MainTabsRoot.EscapeCurrentTab(false); };
                    break;
                }

                case Step.Decision:
                {
                    if (reg == null || reg.OpenDecisions.Count == 0) return;
                    var d = reg.OpenDecisions[0];
                    CloseOurWindows();
                    Frame(office, 11f);
                    UIEntry.OpenDecisions();
                    Shoot("decision");
                    string id = d.id;
                    string option = d.options != null && d.options.Count > 0 ? d.options[0] : "Allow once";
                    afterShot = () =>
                    {
                        CloseOurWindows();
                        AgentRegistry.Instance?.AnswerDecision(id, option, null);
                    };
                    break;
                }

                case Step.TaskBoard:
                {
                    if (reg == null || !BoardIsInteresting(reg)) return;
                    CloseOurWindows();
                    UIEntry.OpenTaskBoard();
                    Shoot("taskboard");
                    break;
                }

                case Step.AgentCard:
                {
                    var lead = Lead(reg);
                    if (lead == null) return;
                    CloseOurWindows();
                    Frame(office, 11f);
                    UIEntry.OpenAgentCard(lead.id);
                    Shoot("agentcard");
                    break;
                }

                case Step.Review:
                {
                    var lead = Lead(reg);
                    if (lead == null || string.IsNullOrEmpty(lead.sessionId)) return;
                    CloseOurWindows();
                    UIEntry.OpenReview(lead.sessionId);
                    // The review window asks the plugin (or the replay) for a diff; give it time to arrive.
                    Shoot("review", 6f);
                    afterShot = CloseOurWindows;
                    break;
                }
            }
        }

        private static void Next(float now)
        {
            step = step + 1;
            stepStarted = now;
            if (step == Step.Done) Log.Message("[ColonyCodeShowcase] all screenshots taken");
        }

        private static void Shoot(string name, float settle = SettleSeconds)
        {
            pendingShot = name;
            shotAt = UnityEngine.Time.realtimeSinceStartup + settle;
        }

        private static void Capture(string name)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".colony-code", "shots");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "showcase_" + name + ".png");
            UnityEngine.ScreenCapture.CaptureScreenshot(path);
            Log.Message("[ColonyCodeShowcase] " + name + " -> " + path);
        }

        private static void Frame(CellRect office, float size)
        {
            Find.CameraDriver.SetRootPosAndSize(office.CenterCell.ToVector3Shifted(), size);
        }

        private static readonly Type[] OurWindows =
        {
            typeof(Window_TaskBoard), typeof(Window_Decisions), typeof(Window_AgentCard), typeof(Window_Review),
        };

        private static void CloseOurWindows()
        {
            foreach (var t in OurWindows) Find.WindowStack.TryRemove(t, false);
        }

        private static Building_AgentDesk DeskWithBusyAgent(Map map)
        {
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                if (!(b is Building_AgentDesk desk)) continue;
                var a = desk.Agent;
                if (a != null && a.log != null && a.log.Count >= 3) return desk;
            }
            return null;
        }

        private static bool BoardIsInteresting(AgentRegistry reg)
        {
            int todo = 0, doing = 0, done = 0;
            foreach (var t in reg.Tasks)
            {
                if (t.status == "done") done++;
                else if (t.status == "doing") doing++;
                else todo++;
            }
            return doing > 0 && done > 0 && todo + doing + done >= 4;
        }

        private static AgentInfo Lead(AgentRegistry reg)
        {
            if (reg == null) return null;
            foreach (var a in reg.Agents)
                if (a.role == "lead") return a;
            return null;
        }
    }
}
