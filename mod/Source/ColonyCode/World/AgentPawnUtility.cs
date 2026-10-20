using ColonyCode.Agents;
using Verse;

namespace ColonyCode.World
{
    public static class AgentPawnUtility
    {
        /// <summary>
        /// The agent's state changed: re-pick its resident's duty now (also while paused) and drop an
        /// idle job so the resident walks to its desk or the decision terminal at once.
        /// </summary>
        public static void NudgeToWork(AgentInfo a)
        {
            var p = a?.pawn;
            if (p == null || !p.Spawned || p.Dead) return;
            ResidentAI.RefreshDuty(p, true);
        }

        /// <summary>The agent this resident embodies right now, if any.</summary>
        public static AgentInfo AgentFor(Pawn pawn)
        {
            return AgentRegistry.Instance?.GetForPawn(pawn);
        }
    }
}
