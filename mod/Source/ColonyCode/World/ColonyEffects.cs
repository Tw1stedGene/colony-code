using System.Reflection;
using ColonyCode.Agents;
using ColonyCode.Core;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ColonyCode.World
{
    /// <summary>
    /// Cosmetic effects: activity flecks at the desk, error/done bursts, speech, the monitor glow and
    /// sounds. Everything is throttled by the callers and skipped when off screen; nothing here
    /// allocates per frame (materials and textures are created once at startup).
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ColonyEffects
    {
        // ------------------------------------------------------------------ assets

        public static readonly Texture2D CardIcon = ContentFinder<Texture2D>.Get("Things/ColonyCode/Commands/AgentCard", false) ?? BaseContent.BadTex;
        public static readonly Texture2D ReviewIcon = ContentFinder<Texture2D>.Get("Things/ColonyCode/Commands/Review", false) ?? BaseContent.BadTex;
        public static readonly Texture2D SpeechIcon = ContentFinder<Texture2D>.Get("Things/ColonyCode/Fleck/Code", false) ?? BaseContent.BadTex;

        private const string GlowTex = "Things/ColonyCode/MonitorGlow";
        private static readonly Material GlowWorking = MaterialPool.MatFrom(GlowTex, ShaderDatabase.MoteGlow, new Color(0.25f, 0.85f, 0.40f, 0.9f));
        private static readonly Material GlowDone = MaterialPool.MatFrom(GlowTex, ShaderDatabase.MoteGlow, new Color(0.45f, 0.95f, 0.75f, 0.9f));
        private static readonly Material GlowWaiting = MaterialPool.MatFrom(GlowTex, ShaderDatabase.MoteGlow, new Color(1.00f, 0.65f, 0.10f, 0.9f));
        private static readonly Material GlowError = MaterialPool.MatFrom(GlowTex, ShaderDatabase.MoteGlow, new Color(1.00f, 0.15f, 0.12f, 0.95f));
        private static readonly Material GlowErrorDim = MaterialPool.MatFrom(GlowTex, ShaderDatabase.MoteGlow, new Color(0.45f, 0.06f, 0.05f, 0.9f));
        private static readonly Material GlowIdle = MaterialPool.MatFrom(GlowTex, ShaderDatabase.MoteGlow, new Color(0.12f, 0.18f, 0.24f, 0.8f));

        /// <summary>Vanilla flecks looked up by name (null if a future version renames them).</summary>
        private static readonly FleckDef ResearchPage = DefDatabase<FleckDef>.GetNamedSilentFail("ResearchPage");
        private static readonly FleckDef MusicNote = DefDatabase<FleckDef>.GetNamedSilentFail("Music");

        public static readonly Color ColTyping = new Color(1.00f, 0.85f, 0.35f);
        public static readonly Color ColThinking = new Color(0.75f, 0.60f, 1.00f);
        public static readonly Color ColRunning = new Color(1.00f, 0.60f, 0.25f);
        public static readonly Color ColTesting = new Color(0.35f, 0.90f, 0.90f);
        public static readonly Color ColError = new Color(0.95f, 0.20f, 0.20f);
        public static readonly Color ColSparkle = new Color(1.00f, 0.90f, 0.45f);
        public static readonly Color ColSpeech = new Color(0.95f, 0.95f, 0.95f);

        // ------------------------------------------------------------------ settings

        /// <summary>
        /// Colony Code sounds are on unless the settings class has a bool field "flavorSounds" set to
        /// false (looked up by reflection, so the toggle can be added in Core/ without touching this file).
        /// </summary>
        private static readonly FieldInfo SoundsField = typeof(ColonyCodeSettings).GetField("flavorSounds", BindingFlags.Public | BindingFlags.Instance);

        public static bool SoundsOn
        {
            get
            {
                var s = ColonyCodeMod.Settings;
                if (s == null || SoundsField == null || SoundsField.FieldType != typeof(bool)) return true;
                return (bool)SoundsField.GetValue(s);
            }
        }

        public static bool SpeechOn => ColonyCodeMod.Settings == null || ColonyCodeMod.Settings.flavorSpeech;

        public static bool ThoughtsOn => ColonyCodeMod.Settings == null || ColonyCodeMod.Settings.flavorThoughts;

        // ------------------------------------------------------------------ desk geometry

        /// <summary>
        /// Centre of the monitor screen relative to the desk's DrawPos, measured from the desk textures
        /// (south: screen at the top; north: monitor back at the bottom; east: right; west mirrors east).
        /// </summary>
        public static Vector3 MonitorOffset(Rot4 rot)
        {
            if (rot == Rot4.North) return new Vector3(0f, 0f, -0.234f);
            if (rot == Rot4.East) return new Vector3(0.234f, 0f, 0f);
            if (rot == Rot4.West) return new Vector3(-0.234f, 0f, 0f);
            return new Vector3(0f, 0f, 0.234f);
        }

        private static Vector3 GlowScale(Rot4 rot)
        {
            return rot.IsHorizontal ? new Vector3(0.40f, 1f, 1.0f) : new Vector3(1.0f, 1f, 0.40f);
        }

        public static Vector3 MonitorPos(Building_AgentDesk desk)
        {
            return desk.DrawPos + MonitorOffset(desk.Rotation);
        }

        // ------------------------------------------------------------------ monitor glow

        /// <summary>
        /// Draws the screen glow for a powered desk: green while working, mint when done, amber while the
        /// agent waits on the player, blinking red on error, dim without a busy agent.
        /// </summary>
        public static void DrawMonitorGlow(Building_AgentDesk desk, Vector3 drawLoc)
        {
            Material mat = GlowMaterial(desk.Agent);
            Rot4 rot = desk.Rotation;
            Vector3 pos = drawLoc + MonitorOffset(rot);
            pos.y = AltitudeLayer.BuildingOnTop.AltitudeFor();
            Matrix4x4 m = Matrix4x4.TRS(pos, Quaternion.identity, GlowScale(rot));
            Graphics.DrawMesh(MeshPool.plane10, m, mat, 0);
        }

        private static Material GlowMaterial(AgentInfo a)
        {
            if (a == null) return GlowIdle;
            switch (a.state)
            {
                case AgentState.Thinking:
                case AgentState.Reading:
                case AgentState.Editing:
                case AgentState.Running:
                case AgentState.Testing:
                    return GlowWorking;
                case AgentState.WaitingUser:
                case AgentState.Blocked:
                    return GlowWaiting;
                case AgentState.Error:
                    return ((int)(Time.realtimeSinceStartup * 2f) & 1) == 0 ? GlowError : GlowErrorDim;
                case AgentState.Done:
                    return GlowDone;
                default:
                    return GlowIdle;
            }
        }

        // ------------------------------------------------------------------ activity flecks

        /// <summary>Ticks until the next activity fleck for this state.</summary>
        public static int ActivityInterval(AgentState s)
        {
            switch (s)
            {
                case AgentState.Editing: return Rand.Range(70, 110);
                case AgentState.Running:
                case AgentState.Testing: return Rand.Range(100, 150);
                case AgentState.Reading: return Rand.Range(120, 180);
                default: return Rand.Range(180, 260);
            }
        }

        /// <summary>One activity fleck above the desk's monitor, chosen by the agent's state.</summary>
        public static void ThrowActivity(Building_AgentDesk desk, AgentInfo a)
        {
            if (a == null || desk == null || !desk.Spawned) return;
            Map map = desk.Map;
            Vector3 loc = MonitorPos(desk) + new Vector3(Rand.Range(-0.2f, 0.2f), 0f, 0.25f);
            if (!loc.ShouldSpawnMotesAt(map)) return;
            switch (a.state)
            {
                case AgentState.Editing:
                    ThrowGlyph(ColonyCodeDefOf.ColonyCode_Fleck_Code, loc, map, ColTyping, 0.9f);
                    break;
                case AgentState.Thinking:
                    ThrowGlyph(ColonyCodeDefOf.ColonyCode_Fleck_Code, loc, map, ColThinking, 0.7f);
                    break;
                case AgentState.Reading:
                    if (ResearchPage != null) ThrowGlyph(ResearchPage, loc, map, Color.white, 0.8f);
                    else ThrowGlyph(ColonyCodeDefOf.ColonyCode_Fleck_Code, loc, map, AgentStateUtility.StateColor(a.state), 0.7f);
                    break;
                case AgentState.Running:
                    ThrowGlyph(ColonyCodeDefOf.ColonyCode_Fleck_Gear, loc, map, ColRunning, 0.9f, Rand.Range(90f, 160f));
                    FleckMaker.ThrowMicroSparks(loc, map);
                    break;
                case AgentState.Testing:
                    ThrowGlyph(ColonyCodeDefOf.ColonyCode_Fleck_TestTube, loc, map, ColTesting, 0.9f);
                    break;
            }
        }

        /// <summary>A slowly rising, tinted glyph fleck.</summary>
        public static void ThrowGlyph(FleckDef def, Vector3 loc, Map map, Color color, float scale, float rotationRate = 0f)
        {
            if (def == null || map == null) return;
            FleckCreationData d = FleckMaker.GetDataStatic(loc, map, def, scale);
            d.instanceColor = color;
            d.velocityAngle = Rand.Range(-25f, 25f);
            d.velocitySpeed = Rand.Range(0.3f, 0.45f);
            d.rotationRate = rotationRate != 0f ? rotationRate : Rand.Range(-15f, 15f);
            map.flecks.CreateFleck(d);
        }

        /// <summary>Gold sparkles bursting from a point.</summary>
        public static void ThrowSparkles(Vector3 loc, Map map, int count)
        {
            var def = ColonyCodeDefOf.ColonyCode_Fleck_Sparkle;
            if (def == null || map == null || !loc.ShouldSpawnMotesAt(map)) return;
            for (int i = 0; i < count; i++)
            {
                FleckCreationData d = FleckMaker.GetDataStatic(loc + new Vector3(Rand.Range(-0.3f, 0.3f), 0f, Rand.Range(0f, 0.5f)), map, def, Rand.Range(0.6f, 1.1f));
                d.instanceColor = ColSparkle;
                d.velocityAngle = Rand.Range(-70f, 70f);
                d.velocitySpeed = Rand.Range(0.4f, 0.9f);
                d.rotationRate = Rand.Range(-90f, 90f);
                map.flecks.CreateFleck(d);
            }
        }

        /// <summary>A music note (vanilla fleck), used while celebrating.</summary>
        public static void ThrowNote(Vector3 loc, Map map)
        {
            if (MusicNote == null || map == null || !loc.ShouldSpawnMotesAt(map)) return;
            ThrowGlyph(MusicNote, loc, map, AgentStateUtility.StateColor(AgentState.Done), 0.8f);
        }

        /// <summary>Where an agent's effects appear: its resident if spawned, else the desk of that resident.</summary>
        private static bool TryGetSpot(AgentInfo a, out Vector3 loc, out Map map)
        {
            var p = a?.pawn;
            if (p != null && p.Spawned && p.Map != null)
            {
                loc = p.DrawPos;
                map = p.Map;
                return true;
            }
            loc = Vector3.zero;
            map = null;
            return false;
        }

        /// <summary>The agent just hit an error: red puff and a red "!".</summary>
        public static void ErrorBurst(AgentInfo a)
        {
            if (!TryGetSpot(a, out Vector3 loc, out Map map) || !loc.ShouldSpawnMotesAt(map)) return;
            FleckMaker.ThrowDustPuffThick(loc + new Vector3(0f, 0f, 0.3f), map, 1.4f, ColError);
            MoteMaker.ThrowText(loc + new Vector3(0f, 0f, 0.7f), map, "!", ColError, 2.5f);
        }

        /// <summary>The agent finished its turn: a few sparkles.</summary>
        public static void DoneSparkle(AgentInfo a)
        {
            if (!TryGetSpot(a, out Vector3 loc, out Map map)) return;
            ThrowSparkles(loc + new Vector3(0f, 0f, 0.4f), map, 4);
        }

        // ------------------------------------------------------------------ speech

        /// <summary>
        /// A spoken line above a resident: vanilla speech bubble plus the text, if speech flavor is on.
        /// </summary>
        public static void Say(Pawn p, string text, Color color)
        {
            if (!SpeechOn || p == null || !p.Spawned || p.Map == null || string.IsNullOrEmpty(text)) return;
            if (text.Length > 90) text = text.Substring(0, 87) + "...";
            Vector3 loc = p.DrawPos + new Vector3(0f, 0f, 0.6f);
            if (!loc.ShouldSpawnMotesAt(p.Map)) return;
            SpeechBubble(p);
            MoteMaker.ThrowText(loc, p.Map, text, color, 4f);
        }

        /// <summary>Only the bubble icon (for lines whose text is already shown elsewhere).</summary>
        public static void SpeechBubble(Pawn p)
        {
            if (!SpeechOn || p == null || !p.Spawned || p.Map == null) return;
            if (!p.DrawPos.ShouldSpawnMotesAt(p.Map)) return;
            MoteMaker.MakeSpeechBubble(p, SpeechIcon);
        }

        // ------------------------------------------------------------------ sounds

        private static float nextKeySound;
        public const float KeySoundMinGapSeconds = 0.15f;

        /// <summary>
        /// A quiet keyboard tick from a coding desk: only while editing/running/testing, only when the
        /// desk is on screen with the camera zoomed in, at most one every <see cref="KeySoundMinGapSeconds"/>.
        /// </summary>
        public static void KeyboardTick(Building_AgentDesk desk, AgentInfo a)
        {
            if (a == null || desk == null || !desk.Spawned) return;
            if (a.state != AgentState.Editing && a.state != AgentState.Running && a.state != AgentState.Testing) return;
            float now = Time.realtimeSinceStartup;
            if (now < nextKeySound) return;
            if (!CameraNear(desk)) return;
            if (!SoundsOn) return;
            nextKeySound = now + KeySoundMinGapSeconds;
            SoundInfo info = SoundInfo.OnCamera();
            info.volumeFactor = 0.3f;
            info.pitchFactor = Rand.Range(0.9f, 1.15f);
            SoundDefOf.Tick_Tiny.PlayOneShot(info);
        }

        private static bool CameraNear(Thing t)
        {
            if (t.Map == null || t.Map != Find.CurrentMap) return false;
            var cam = Find.CameraDriver;
            if (cam == null || cam.CurrentZoom > CameraZoomRange.Middle) return false;
            return cam.CurrentViewRect.Contains(t.Position);
        }

        /// <summary>Bell when an agent opens a decision.</summary>
        public static void DecisionBell()
        {
            if (!SoundsOn || Find.CurrentMap == null) return;
            SoundDefOf.TinyBell.PlayOneShotOnCamera();
        }

        /// <summary>Chime when a goal is done.</summary>
        public static void SuccessChime()
        {
            if (!SoundsOn || Find.CurrentMap == null) return;
            SoundDefOf.Quest_Succeded.PlayOneShotOnCamera();
        }
    }
}
