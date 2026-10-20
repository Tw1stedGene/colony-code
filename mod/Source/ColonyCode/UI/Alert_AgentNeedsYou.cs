using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace ColonyCode.UI
{
    /// <summary>Critical alert while any agent decision waits for the player. Clicking opens the decisions window.</summary>
    public class Alert_AgentNeedsYou : Alert
    {
        private readonly List<Pawn> culprits = new List<Pawn>();

        public Alert_AgentNeedsYou()
        {
            defaultPriority = AlertPriority.Critical;
            defaultLabel = "ColonyCode_AlertLabel".Translate(1);
        }

        public override AlertReport GetReport()
        {
            if (UIUtil.OpenDecisionCount() == 0) return AlertReport.Inactive;
            culprits.Clear();
            var reg = UIUtil.Registry;
            foreach (var d in reg.OpenDecisions)
            {
                if (d == null || d.answered) continue;
                var p = reg.Get(d.agentId)?.pawn;
                if (p != null && p.Spawned && !culprits.Contains(p)) culprits.Add(p);
            }
            return culprits.Count > 0 ? AlertReport.CulpritsAre(culprits) : AlertReport.Active;
        }

        public override string GetLabel()
        {
            return "ColonyCode_AlertLabel".Translate(UIUtil.OpenDecisionCount());
        }

        public override TaggedString GetExplanation()
        {
            var sb = new StringBuilder();
            sb.Append("ColonyCode_AlertExplanation".Translate());
            var reg = UIUtil.Registry;
            if (reg?.OpenDecisions != null)
            {
                sb.AppendLine();
                foreach (var d in reg.OpenDecisions)
                {
                    if (d == null || d.answered) continue;
                    sb.AppendLine();
                    sb.Append("  - ").Append(UIUtil.Safe(UIUtil.AgentName(d.agentId))).Append(": ").Append(UIUtil.OneLine(d.question, 90));
                }
            }
            return sb.ToString();
        }

        protected override void OnClick()
        {
            UIEntry.OpenDecisions();
        }
    }
}
