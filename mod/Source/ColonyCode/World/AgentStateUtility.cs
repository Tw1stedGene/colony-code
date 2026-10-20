using ColonyCode.Agents;
using UnityEngine;
using Verse;

namespace ColonyCode.World
{
    /// <summary>Colors, labels and small helpers for agent states, shared by world code (and usable by UI).</summary>
    public static class AgentStateUtility
    {
        public static Color StateColor(AgentState s)
        {
            switch (s)
            {
                case AgentState.Thinking: return new Color(0.70f, 0.55f, 0.95f);
                case AgentState.Reading: return new Color(0.35f, 0.65f, 1.00f);
                case AgentState.Editing: return new Color(1.00f, 0.80f, 0.25f);
                case AgentState.Running: return new Color(1.00f, 0.55f, 0.20f);
                case AgentState.Testing: return new Color(0.30f, 0.85f, 0.85f);
                case AgentState.WaitingUser: return new Color(1.00f, 0.30f, 0.80f);
                case AgentState.Blocked: return new Color(0.85f, 0.45f, 0.30f);
                case AgentState.Done: return new Color(0.35f, 0.90f, 0.40f);
                case AgentState.Error: return new Color(0.95f, 0.25f, 0.25f);
                default: return new Color(0.60f, 0.60f, 0.60f);
            }
        }

        public static string Label(AgentState s)
        {
            return ("ColonyCodeW_State_" + s).Translate();
        }

        /// <summary>The agent is busy but its pawn is not at the desk (asleep, downed, drafted, doing other work).</summary>
        public static bool IsWorkingRemotely(AgentInfo a)
        {
            if (a == null || !AgentRegistry.IsActive(a.state)) return false;
            var p = a.pawn;
            if (p == null) return true;
            return p.CurJobDef != ColonyCodeDefOf.ColonyCode_Code;
        }

        public static Texture2D CommandIcon(ThingDef def)
        {
            if (def != null && def.uiIcon != null) return def.uiIcon;
            return BaseContent.BadTex;
        }
    }
}
