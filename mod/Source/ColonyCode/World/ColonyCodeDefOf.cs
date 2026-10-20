using RimWorld;
using Verse;
using Verse.AI;

namespace ColonyCode.World
{
    [DefOf]
    public static class ColonyCodeDefOf
    {
        public static ThingDef ColonyCode_AgentDesk;
        public static ThingDef ColonyCode_ServerRack;
        public static ThingDef ColonyCode_DecisionTerminal;
        public static ThingDef ColonyCode_TaskBoard;
        public static ThingDef ColonyCode_OfficeUplink;
        public static ResearchProjectDef ColonyCode_Networking;
        public static JobDef ColonyCode_Code;
        public static JobDef ColonyCode_AwaitDecision;
        public static ThoughtDef ColonyCode_ShippedIt;
        public static ThoughtDef ColonyCode_BrokenBuild;
        public static FactionDef ColonyCode_RemoteWorkers;
        public static PawnKindDef ColonyCode_RemoteWorker;
        public static HediffDef ColonyCode_RemoteLink;
        public static DutyDef ColonyCode_CodeAtDesk;
        public static DutyDef ColonyCode_AwaitAtTerminal;
        public static DutyDef ColonyCode_IdleNearDesk;
        public static JobDef ColonyCode_Celebrate;
        public static FleckDef ColonyCode_Fleck_Code;
        public static FleckDef ColonyCode_Fleck_Gear;
        public static FleckDef ColonyCode_Fleck_TestTube;
        public static FleckDef ColonyCode_Fleck_Sparkle;

        static ColonyCodeDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(ColonyCodeDefOf));
        }
    }
}
