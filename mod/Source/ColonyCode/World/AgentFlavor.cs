using ColonyCode.Core;
using RimWorld;
using Verse;

namespace ColonyCode.World
{
    /// <summary>Optional cosmetic mood effects (setting "flavorThoughts").</summary>
    public static class AgentFlavor
    {
        public const int BrokenBuildCooldownTicks = 2500;

        private static bool Enabled => ColonyCodeMod.Settings == null || ColonyCodeMod.Settings.flavorThoughts;

        public static void ShippedIt()
        {
            if (!Enabled) return;
            GiveAll(ColonyCodeDefOf.ColonyCode_ShippedIt);
        }

        public static void BrokenBuild(ref int lastTick)
        {
            if (!Enabled) return;
            int now = Find.TickManager?.TicksGame ?? 0;
            if (now - lastTick < BrokenBuildCooldownTicks) return;
            lastTick = now;
            GiveAll(ColonyCodeDefOf.ColonyCode_BrokenBuild);
        }

        private static void GiveAll(ThoughtDef def)
        {
            if (def == null || Current.Game == null) return;
            foreach (var p in PawnsFinder.AllMaps_FreeColonists)
            {
                p.needs?.mood?.thoughts?.memories?.TryGainMemory(def);
            }
        }
    }
}
