using System.Collections.Generic;
using ColonyCode.Agents;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyCode.World
{
    /// <summary>
    /// A deterministic colour per resident name: the overhead name uses it, and the resident's torso
    /// apparel is dyed with a darker shade of it (applied by <see cref="GameComponent_ColonyLife"/>).
    /// Roster names (Marlow, Juniper, ...) get hand-picked, well-separated hues; "Marlow 2" shifts the
    /// hue a little; any other name hashes to a hue.
    /// </summary>
    public static class AgentLook
    {
        private static readonly string[] RosterNames = { "Marlow", "Juniper", "Kit", "Wren", "Rowan", "Tove" };
        private static readonly float[] RosterHues = { 0.58f, 0.33f, 0.08f, 0.80f, 0.98f, 0.48f };

        private static readonly Dictionary<string, float> hueCache = new Dictionary<string, float>();

        /// <summary>Hue 0..1 for a resident name. Cached, so no allocation after the first call per name.</summary>
        public static float HueFor(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0.6f;
            float hue;
            if (hueCache.TryGetValue(name, out hue)) return hue;
            hue = ComputeHue(name);
            hueCache[name] = hue;
            return hue;
        }

        private static float ComputeHue(string name)
        {
            string baseName = name;
            int n = 1;
            int sp = name.LastIndexOf(' ');
            if (sp > 0 && int.TryParse(name.Substring(sp + 1), out int parsed) && parsed > 1)
            {
                baseName = name.Substring(0, sp);
                n = parsed;
            }
            for (int i = 0; i < RosterNames.Length; i++)
            {
                if (RosterNames[i] != baseName) continue;
                return Mathf.Repeat(RosterHues[i] + 0.07f * (n - 1), 1f);
            }
            // FNV-1a: stable across runs and platforms (string.GetHashCode is not guaranteed to be).
            uint h = 2166136261;
            for (int i = 0; i < name.Length; i++)
            {
                h ^= name[i];
                h *= 16777619;
            }
            return (h % 360u) / 360f;
        }

        /// <summary>Bright colour for text on a dark background.</summary>
        public static Color NameColor(string name)
        {
            return Color.HSVToRGB(HueFor(name), 0.45f, 1f);
        }

        /// <summary>Muted shade for clothes.</summary>
        public static Color ApparelColor(string name)
        {
            return Color.HSVToRGB(HueFor(name), 0.55f, 0.72f);
        }

        /// <summary>The roster name of a resident, else its short name.</summary>
        public static string NameFor(Pawn pawn)
        {
            if (pawn == null) return null;
            var rec = AgentRegistry.Instance?.ResidentFor(pawn);
            if (rec != null && !string.IsNullOrEmpty(rec.name)) return rec.name;
            return pawn.LabelShort;
        }

        public static Color NameColor(Pawn pawn)
        {
            return NameColor(NameFor(pawn));
        }

        /// <summary>
        /// Dyes every worn apparel that covers the torso (shirts, jackets) in the resident's colour.
        /// Idempotent: CompColorableUtility.SetColor skips a colour that is already set.
        /// </summary>
        public static void ApplyApparelColor(Pawn pawn)
        {
            var worn = pawn?.apparel?.WornApparel;
            if (worn == null) return;
            Color c = ApparelColor(NameFor(pawn));
            for (int i = 0; i < worn.Count; i++)
            {
                var ap = worn[i];
                var groups = ap?.def?.apparel?.bodyPartGroups;
                if (groups == null || !groups.Contains(BodyPartGroupDefOf.Torso)) continue;
                if (ap.GetComp<CompColorable>() == null) continue;
                ap.SetColor(c, false);
            }
        }
    }
}
