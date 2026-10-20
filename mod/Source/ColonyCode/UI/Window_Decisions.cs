using System.Collections.Generic;
using ColonyCode.Agents;
using UnityEngine;
using Verse;

namespace ColonyCode.UI
{
    /// <summary>All open agent decisions (permission prompts and questions), drawn with <see cref="DecisionCard"/>.</summary>
    public class Window_Decisions : Window
    {
        private Vector2 scroll;
        private readonly List<DecisionInfo> snapshot = new List<DecisionInfo>();

        public Window_Decisions()
        {
            doCloseX = true;
            doCloseButton = false;
            closeOnClickedOutside = false;
            closeOnAccept = false;
            absorbInputAroundWindow = false;
            draggable = true;
            resizeable = true;
            preventCameraMotion = false;
            onlyOneOfTypeAllowed = true;
            optionalTitle = "ColonyCode_DecisionsTitle".Translate();
        }

        public override Vector2 InitialSize => new Vector2(720f, 620f);

        public override void DoWindowContents(Rect inRect)
        {
            snapshot.Clear();
            var list = UIUtil.Registry?.OpenDecisions;
            if (list != null)
            {
                foreach (var d in list) if (d != null && !d.answered) snapshot.Add(d);
            }

            Text.Font = GameFont.Small;
            if (snapshot.Count == 0)
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(inRect, "ColonyCode_NoDecisions".Translate());
                GUI.color = Color.white;
                return;
            }

            float width = inRect.width - 16f;
            float total = 0f;
            foreach (var d in snapshot) total += DecisionCard.Height(d, width) + 10f;

            var viewRect = new Rect(0f, 0f, width, total);
            Widgets.BeginScrollView(inRect, ref scroll, viewRect);
            float y = 0f;
            DecisionInfo answered = null;
            DecisionCard.Answer answer = default(DecisionCard.Answer);
            foreach (var d in snapshot)
            {
                float h = DecisionCard.Height(d, width);
                DecisionCard.Answer a;
                if (DecisionCard.Draw(new Rect(0f, y, width, h), d, out a))
                {
                    answered = d;
                    answer = a;
                }
                y += h + 10f;
            }
            Widgets.EndScrollView();

            // Answer outside the loop so the registry can mutate OpenDecisions safely.
            if (answered != null) DecisionCard.Submit(answered, answer);
        }
    }
}
