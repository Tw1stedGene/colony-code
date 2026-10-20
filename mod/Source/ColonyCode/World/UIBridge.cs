using Verse;

namespace ColonyCode.World
{
    /// <summary>
    /// World-side entry into the UI layer (ColonyCode.UI). Both halves are compiled into the same
    /// assembly, so this calls UIEntry directly.
    /// </summary>
    public static class UIBridge
    {
        /// <summary>Opens the open-decisions window (ColonyCode.UI.UIEntry.OpenDecisions).</summary>
        public static bool OpenDecisions()
        {
            try
            {
                ColonyCode.UI.UIEntry.OpenDecisions();
                return true;
            }
            catch (System.Exception e)
            {
                Log.Error("[ColonyCode] UIEntry.OpenDecisions failed: " + e);
                return false;
            }
        }

        /// <summary>Opens the task board window (ColonyCode.UI.UIEntry.OpenTaskBoard).</summary>
        public static bool OpenTaskBoard()
        {
            try
            {
                ColonyCode.UI.UIEntry.OpenTaskBoard();
                return true;
            }
            catch (System.Exception e)
            {
                Log.Error("[ColonyCode] UIEntry.OpenTaskBoard failed: " + e);
                return false;
            }
        }

        /// <summary>Opens the agent card (ColonyCode.UI.UIEntry.OpenAgentCard).</summary>
        public static bool OpenAgentCard(string agentId)
        {
            if (string.IsNullOrEmpty(agentId)) return false;
            try
            {
                ColonyCode.UI.UIEntry.OpenAgentCard(agentId);
                return true;
            }
            catch (System.Exception e)
            {
                Log.Error("[ColonyCode] UIEntry.OpenAgentCard failed: " + e);
                return false;
            }
        }

        /// <summary>Opens the review window for a session (ColonyCode.UI.UIEntry.OpenReview).</summary>
        public static bool OpenReview(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return false;
            try
            {
                ColonyCode.UI.UIEntry.OpenReview(sessionId);
                return true;
            }
            catch (System.Exception e)
            {
                Log.Error("[ColonyCode] UIEntry.OpenReview failed: " + e);
                return false;
            }
        }
    }
}