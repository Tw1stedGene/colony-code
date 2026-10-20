using System.Collections.Generic;
using ColonyCode.Agents;
using UnityEngine;
using Verse;

namespace ColonyCode.UI
{
    /// <summary>
    /// Draws one open decision as a card: permission (option buttons, "Always allow" with what it
    /// covers, optional reason) or question (option buttons, or checkboxes + Submit for multiSelect,
    /// plus a free-text "Other" answer when allowText). Used by Window_Decisions and Dialog_Decision.
    /// The typed text and ticked options are kept per decision id, so both windows share them.
    /// </summary>
    public static class DecisionCard
    {
        public const float ContextHeight = 90f;
        public const float RowHeight = 28f;
        public const float ButtonHeight = 30f;
        public const float CheckRowHeight = 26f;
        private const float ButtonWidth = 160f;
        private const float Gap = 8f;

        private static readonly Color ColAlways = new Color(0.55f, 0.8f, 1f);
        private static readonly Color CoversBg = new Color(0.12f, 0.18f, 0.25f, 0.9f);

        private static readonly Dictionary<string, string> texts = new Dictionary<string, string>();
        private static readonly Dictionary<string, HashSet<string>> ticked = new Dictionary<string, HashSet<string>>();
        private static readonly Dictionary<string, Vector2> scrolls = new Dictionary<string, Vector2>();

        public struct Answer
        {
            public string option;
            public string text;
        }

        /// <summary>Forget the typed state of a decision (after it was answered or closed).</summary>
        public static void Forget(string id)
        {
            if (id == null) return;
            texts.Remove(id);
            ticked.Remove(id);
            scrolls.Remove(id);
        }

        public static bool IsAlwaysAllow(string opt) => opt != null && opt.Trim().ToLowerInvariant() == "always allow";

        private static bool IsDeny(string opt) => opt != null && opt.ToLowerInvariant().Contains("deny");

        private static List<string> Options(DecisionInfo d) => d.options ?? new List<string>();

        private static float QuestionHeight(DecisionInfo d, float w)
        {
            Text.Font = GameFont.Small;
            return Mathf.Max(24f, Text.CalcHeight(UIUtil.Safe(d.question ?? ""), w));
        }

        private static float CoversHeight(DecisionInfo d, float w)
        {
            if (string.IsNullOrEmpty(d.covers)) return 0f;
            Text.Font = GameFont.Tiny;
            float h = Text.CalcHeight(UIUtil.Safe(d.covers), w - 8f) + 6f;
            Text.Font = GameFont.Small;
            return h;
        }

        private static int ButtonRows(int n, float w)
        {
            if (n == 0) return 1;
            int perRow = Mathf.Max(1, Mathf.FloorToInt((w + Gap) / (ButtonWidth + Gap)));
            return (n + perRow - 1) / perRow;
        }

        /// <summary>Height of the card for an inner width (card width minus 20).</summary>
        public static float Height(DecisionInfo d, float cardWidth)
        {
            float w = cardWidth - 20f;
            float h = 10f + 22f + QuestionHeight(d, w) + 6f;
            if (!string.IsNullOrEmpty(d.context)) h += ContextHeight + 6f;
            float cov = CoversHeight(d, w);
            if (cov > 0f) h += cov + 6f;
            int n = Options(d).Count;
            if (d.IsQuestion)
            {
                if (d.multiSelect) h += n * CheckRowHeight + 4f;
                else h += ButtonHeight * ButtonRows(n, w);
                if (d.allowText) h += RowHeight + 6f;
                if (d.multiSelect) h += ButtonHeight;
            }
            else
            {
                h += RowHeight + 6f;
                h += ButtonHeight * ButtonRows(n, w);
            }
            return h + 10f;
        }

        /// <returns>true when the player answered; the answer is in <paramref name="answer"/>.</returns>
        public static bool Draw(Rect rect, DecisionInfo d, out Answer answer, bool showJump = true)
        {
            answer = default(Answer);
            Widgets.DrawMenuSection(rect);
            var inner = rect.ContractedBy(10f);
            float y = inner.y;

            // Header: [session] agent - kind.
            var agent = UIUtil.Registry?.Get(d.agentId);
            Text.Font = GameFont.Tiny;
            GUI.color = UIUtil.ColMuted;
            string kind = d.IsQuestion ? "ColonyCode_KindQuestion".Translate() : "ColonyCode_KindPermission".Translate();
            string who = agent != null ? UIUtil.Safe(UIUtil.AgentName(agent)) : UIUtil.Safe(d.agentId ?? "?");
            Widgets.Label(new Rect(inner.x, y, inner.width - 90f, 20f), UIUtil.SessionPrefix(d.sessionId) + who + " - " + kind);
            GUI.color = Color.white;
            if (showJump && UIUtil.CanJump(agent) && Widgets.ButtonText(new Rect(inner.xMax - 84f, y - 2f, 84f, 22f), "ColonyCode_Jump".Translate()))
                UIUtil.JumpTo(agent);
            y += 22f;

            Text.Font = GameFont.Small;
            float qh = QuestionHeight(d, inner.width);
            Widgets.Label(new Rect(inner.x, y, inner.width, qh), UIUtil.Safe(d.question ?? ""));
            y += qh + 6f;

            if (!string.IsNullOrEmpty(d.context))
            {
                var ctxRect = new Rect(inner.x, y, inner.width, ContextHeight);
                Widgets.DrawBoxSolid(ctxRect, new Color(0.06f, 0.06f, 0.07f, 0.9f));
                Vector2 sp;
                scrolls.TryGetValue(d.id, out sp);
                Text.Font = GameFont.Tiny;
                GUI.color = UIUtil.ColResult;
                Widgets.LabelScrollable(ctxRect.ContractedBy(4f), UIUtil.Safe(d.context), ref sp);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                scrolls[d.id] = sp;
                y += ContextHeight + 6f;
            }

            float cov = CoversHeight(d, inner.width);
            if (cov > 0f)
            {
                var cr = new Rect(inner.x, y, inner.width, cov);
                Widgets.DrawBoxSolid(cr, CoversBg);
                Text.Font = GameFont.Tiny;
                GUI.color = ColAlways;
                Widgets.Label(cr.ContractedBy(4f, 3f), UIUtil.Safe(d.covers));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                y += cov + 6f;
            }

            string text;
            texts.TryGetValue(d.id, out text);
            text = text ?? "";
            var options = Options(d);

            if (!d.IsQuestion)
            {
                // Permission: optional reason (used as the deny reason) + option buttons.
                var labelRect = new Rect(inner.x, y, 150f, RowHeight);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(labelRect, "ColonyCode_Reason".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                texts[d.id] = Widgets.TextField(new Rect(labelRect.xMax, y, inner.width - labelRect.width, RowHeight), text, 500);
                y += RowHeight + 6f;
                if (options.Count == 0)
                {
                    GUI.color = UIUtil.ColMuted;
                    Widgets.Label(new Rect(inner.x, y, inner.width, ButtonHeight), "ColonyCode_NoOptions".Translate());
                    GUI.color = Color.white;
                    return false;
                }
                string chosen = DrawOptionButtons(inner, y, d, options);
                if (chosen == null) return false;
                answer.option = chosen;
                answer.text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                return true;
            }

            // Question.
            if (d.multiSelect)
            {
                HashSet<string> set;
                if (!ticked.TryGetValue(d.id, out set)) ticked[d.id] = set = new HashSet<string>();
                foreach (var opt in options)
                {
                    var row = new Rect(inner.x, y, inner.width, CheckRowHeight);
                    bool on = set.Contains(opt);
                    bool was = on;
                    Widgets.CheckboxLabeled(row, UIUtil.OneLine(opt, 120), ref on, false, null, null, true);
                    if (on != was) { if (on) set.Add(opt); else set.Remove(opt); }
                    TooltipHandler.TipRegion(row, UIUtil.Safe(opt));
                    y += CheckRowHeight;
                }
                y += 4f;
            }
            else if (options.Count > 0)
            {
                string chosen = DrawOptionButtons(inner, y, d, options);
                y += ButtonHeight * ButtonRows(options.Count, inner.width);
                if (chosen != null)
                {
                    answer.option = chosen;
                    answer.text = d.allowText && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
                    return true;
                }
            }
            else
            {
                y += ButtonHeight;
            }

            if (d.allowText)
            {
                var labelRect = new Rect(inner.x, y, 110f, RowHeight);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(labelRect, "ColonyCode_OtherAnswer".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                float sendW = d.multiSelect ? 0f : 130f;
                var field = new Rect(labelRect.xMax, y, inner.width - labelRect.width - (sendW > 0f ? sendW + Gap : 0f), RowHeight);
                text = Widgets.TextField(field, text, 2000);
                texts[d.id] = text;
                if (sendW > 0f)
                {
                    bool has = !string.IsNullOrWhiteSpace(text);
                    if (Widgets.ButtonText(new Rect(field.xMax + Gap, y, sendW, RowHeight), "ColonyCode_SendOther".Translate(), true, true, has) && has)
                    {
                        answer.option = null;
                        answer.text = text.Trim();
                        return true;
                    }
                }
                y += RowHeight + 6f;
            }

            if (d.multiSelect)
            {
                HashSet<string> set = ticked[d.id];
                bool hasText = d.allowText && !string.IsNullOrWhiteSpace(text);
                bool can = set.Count > 0 || hasText;
                var sub = new Rect(inner.x, y, ButtonWidth, ButtonHeight - 4f);
                if (Widgets.ButtonText(sub, "ColonyCode_SubmitAnswer".Translate(), true, true, UIUtil.ColGood, can) && can)
                {
                    // Keep the options' own order.
                    var picked = new List<string>();
                    foreach (var opt in options) if (set.Contains(opt)) picked.Add(opt);
                    answer.option = picked.Count > 0 ? string.Join("\n", picked) : null;
                    answer.text = hasText ? text.Trim() : null;
                    return true;
                }
                if (!can) TooltipHandler.TipRegion(sub, "ColonyCode_SubmitNeedsChoice".Translate());
            }
            return false;
        }

        private static string DrawOptionButtons(Rect inner, float y, DecisionInfo d, List<string> options)
        {
            string chosen = null;
            int perRow = Mathf.Max(1, Mathf.FloorToInt((inner.width + Gap) / (ButtonWidth + Gap)));
            for (int i = 0; i < options.Count; i++)
            {
                int row = i / perRow, col = i % perRow;
                var br = new Rect(inner.x + col * (ButtonWidth + Gap), y + row * ButtonHeight, ButtonWidth, ButtonHeight - 4f);
                string opt = options[i] ?? "";
                Color c = Color.white;
                if (!d.IsQuestion)
                {
                    if (IsDeny(opt)) c = UIUtil.ColBad;
                    else if (IsAlwaysAllow(opt)) c = ColAlways;
                    else if (i == 0) c = UIUtil.ColGood;
                }
                if (Widgets.ButtonText(br, UIUtil.OneLine(opt, 40), true, true, c)) chosen = opt;
                string tip = UIUtil.Safe(opt);
                if (IsAlwaysAllow(opt) && !string.IsNullOrEmpty(d.covers)) tip += "\n\n" + UIUtil.Safe(d.covers);
                TooltipHandler.TipRegion(br, tip);
            }
            return chosen;
        }

        /// <summary>Sends the answer, forgets the typed state and removes the decision's letter.</summary>
        public static void Submit(DecisionInfo d, Answer a)
        {
            if (d == null) return;
            UIUtil.Registry?.AnswerDecision(d.id, a.option, a.text);
            Forget(d.id);
            LetterHub.RemoveLetterFor(d.id);
        }
    }

    /// <summary>One decision in its own dialog (opened from its letter).</summary>
    public class Dialog_Decision : Window
    {
        public readonly string decisionId;
        private Vector2 scroll;

        public Dialog_Decision(string decisionId)
        {
            this.decisionId = decisionId;
            doCloseX = true;
            closeOnClickedOutside = false;
            closeOnAccept = false;
            absorbInputAroundWindow = false;
            draggable = true;
            resizeable = true;
            preventCameraMotion = false;
            forcePause = false;
            optionalTitle = "ColonyCode_DecisionsTitle".Translate();
        }

        public override Vector2 InitialSize => new Vector2(640f, 520f);

        private DecisionInfo FindDecision()
        {
            var list = UIUtil.Registry?.OpenDecisions;
            if (list == null) return null;
            foreach (var d in list) if (d != null && d.id == decisionId && !d.answered) return d;
            return null;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var d = FindDecision();
            if (d == null)
            {
                Close();
                return;
            }
            float w = inRect.width - 16f;
            float h = DecisionCard.Height(d, w);
            var view = new Rect(0f, 0f, w, h);
            Widgets.BeginScrollView(inRect, ref scroll, view);
            DecisionCard.Answer a;
            bool answered = DecisionCard.Draw(new Rect(0f, 0f, w, h), d, out a);
            Widgets.EndScrollView();
            if (answered)
            {
                DecisionCard.Submit(d, a);
                Close();
            }
        }
    }
}
