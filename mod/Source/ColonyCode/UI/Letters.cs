using System.Collections.Generic;
using ColonyCode.Agents;
using RimWorld;
using Verse;

namespace ColonyCode.UI
{
    /// <summary>
    /// Subscribes once to the AgentRegistry events and turns them into letters:
    /// a choice letter per open decision (removed when it closes) and a standard letter when a goal
    /// finishes or fails.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class LetterHub
    {
        private static readonly Dictionary<string, ChoiceLetter_AgentDecision> letters = new Dictionary<string, ChoiceLetter_AgentDecision>();
        private static readonly Dictionary<string, string> goalStatus = new Dictionary<string, string>();
        private static Game trackedGame;

        static LetterHub()
        {
            AgentRegistry.DecisionOpened += OnDecisionOpened;
            AgentRegistry.DecisionClosed += OnDecisionClosed;
            AgentRegistry.GoalChanged += OnGoalChanged;
            AgentRegistry.MergeResultReceived += OnMergeResult;
        }

        /// <summary>Called by GameComponent_ColonyCodeUI on load / new game: forget state of the previous game.</summary>
        public static void ResetForGame(Game game)
        {
            trackedGame = game;
            letters.Clear();
            goalStatus.Clear();
        }

        private static bool GameReady()
        {
            var game = Current.Game;
            if (game == null || Find.LetterStack == null) return false;
            if (!ReferenceEquals(game, trackedGame)) ResetForGame(game);
            return true;
        }

        private static void OnDecisionOpened(DecisionInfo d)
        {
            if (d == null || string.IsNullOrEmpty(d.id) || d.answered || !GameReady()) return;
            if (letters.ContainsKey(d.id)) return;

            var agent = UIUtil.Registry?.Get(d.agentId);
            string agentName = agent != null ? UIUtil.AgentName(agent) : (d.agentId ?? "?");
            var letter = new ChoiceLetter_AgentDecision();
            letter.def = LetterDefOf.NeutralEvent;
            letter.ID = Find.UniqueIDsManager.GetNextLetterID();
            letter.decisionId = d.id;
            letter.options = d.options != null ? new List<string>(d.options) : new List<string>();
            letter.isQuestion = d.IsQuestion;
            letter.Label = UIUtil.SessionPrefix(d.sessionId) +
                           (d.IsQuestion ? "ColonyCode_LetterQuestionLabel" : "ColonyCode_LetterPermissionLabel").Translate(UIUtil.Safe(agentName));
            string text = "ColonyCode_LetterText".Translate(UIUtil.Safe(agentName), UIUtil.Safe(d.question ?? ""));
            if (!string.IsNullOrEmpty(d.context))
            {
                string ctx = d.context;
                // The "Always allow covers:" line is shown on its own below, never cut off.
                if (!string.IsNullOrEmpty(d.covers)) ctx = ctx.Replace(d.covers, "").TrimEnd();
                if (ctx.Length > 1200) ctx = ctx.Substring(0, 1200) + "…";
                if (ctx.Length > 0) text += "\n\n" + UIUtil.Safe(ctx);
            }
            if (!string.IsNullOrEmpty(d.covers)) text += "\n\n" + UIUtil.Safe(d.covers);
            if (d.IsQuestion && (d.multiSelect || d.allowText)) text += "\n\n" + "ColonyCode_LetterQuestionHint".Translate();
            letter.Text = text;
            if (agent?.pawn != null && agent.pawn.Spawned) letter.lookTargets = new LookTargets(agent.pawn);

            letters[d.id] = letter;
            Find.LetterStack.ReceiveLetter(letter, null, 0, true);
        }

        private static void OnDecisionClosed(DecisionInfo d)
        {
            if (d == null || string.IsNullOrEmpty(d.id)) return;
            RemoveLetterFor(d.id);
        }

        /// <summary>Removes the choice letter of a decision, if it is still in the letter stack.</summary>
        public static void RemoveLetterFor(string decisionId)
        {
            if (string.IsNullOrEmpty(decisionId)) return;
            ChoiceLetter_AgentDecision letter;
            if (letters.TryGetValue(decisionId, out letter)) letters.Remove(decisionId);
            if (Current.Game == null || Find.LetterStack == null) return;
            if (letter != null && Find.LetterStack.LettersListForReading.Contains(letter))
            {
                Find.LetterStack.RemoveLetter(letter);
                return;
            }
            // Fallback: a letter we lost track of (e.g. after a reload).
            var list = Find.LetterStack.LettersListForReading;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is ChoiceLetter_AgentDecision l && l.decisionId == decisionId)
                {
                    Find.LetterStack.RemoveLetter(l);
                }
            }
        }

        /// <summary>Remove decision letters that no longer match an open decision (stale after loading a save).</summary>
        public static void RemoveStaleLetters()
        {
            if (Current.Game == null || Find.LetterStack == null) return;
            var list = new List<Letter>(Find.LetterStack.LettersListForReading);
            foreach (var l in list)
            {
                if (l is ChoiceLetter_AgentDecision dl && !ChoiceLetter_AgentDecision.IsOpen(dl.decisionId))
                    Find.LetterStack.RemoveLetter(dl);
            }
        }

        private static void OnMergeResult(MergeRequest m)
        {
            if (!GameReady()) return;
            MergeResultNotifier.Notify(m);
        }

        private static void OnGoalChanged(GoalInfo g)
        {
            if (g == null || string.IsNullOrEmpty(g.id) || !GameReady()) return;
            string prev;
            goalStatus.TryGetValue(g.id, out prev);
            string status = g.status ?? "active";
            goalStatus[g.id] = status;
            if (status == prev) return;
            if (status != "done" && status != "failed") return;

            bool done = status == "done";
            string goalText = UIUtil.OneLine(g.text, 80);
            string label = UIUtil.SessionPrefix(g.sessionId) + (done ? "ColonyCode_GoalDoneLabel" : "ColonyCode_GoalFailedLabel").Translate();
            string text = (done ? "ColonyCode_GoalDoneText" : "ColonyCode_GoalFailedText").Translate(goalText);
            if (!string.IsNullOrEmpty(g.summary))
            {
                string summary = g.summary.Length > 1500 ? g.summary.Substring(0, 1500) + "…" : g.summary;
                text += "\n\n" + UIUtil.Safe(summary);
            }
            var letter = LetterMaker.MakeLetter(label, text, done ? LetterDefOf.PositiveEvent : LetterDefOf.NegativeEvent);
            Find.LetterStack.ReceiveLetter(letter, null, 0, true);
        }
    }

    /// <summary>Messages (and a letter on failure) for merge.result.</summary>
    public static class MergeResultNotifier
    {
        public static void Notify(MergeRequest m)
        {
            if (m == null || Current.Game == null) return;
            string wt = UIUtil.Safe(m.worktree ?? "?");
            string action = ("ColonyCode_ReviewAction_" + (m.action ?? "merge")).CanTranslate()
                ? ("ColonyCode_ReviewAction_" + m.action).Translate().ToString()
                : (m.action ?? "");
            string msg = UIUtil.OneLine(m.message, 300);
            string line = (m.ok ? "ColonyCode_MergeOk" : "ColonyCode_MergeFailed").Translate(action, wt, msg);
            Messages.Message(UIUtil.SessionPrefix(m.sessionId) + line, m.ok ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NegativeEvent, false);
            if (m.ok || Find.LetterStack == null) return;
            string text = "ColonyCode_MergeFailedText".Translate(action, wt) + "\n\n" + UIUtil.Safe(m.message ?? "");
            var letter = LetterMaker.MakeLetter(UIUtil.SessionPrefix(m.sessionId) + "ColonyCode_MergeFailedLabel".Translate(), text, LetterDefOf.NegativeEvent);
            Find.LetterStack.ReceiveLetter(letter, null, 0, true);
        }
    }

    /// <summary>
    /// Choice letter for one open agent decision. Permission: one button per option. Question:
    /// opening the letter shows <see cref="Dialog_Decision"/> (buttons, checkboxes, free text).
    /// </summary>
    public class ChoiceLetter_AgentDecision : ChoiceLetter
    {
        public string decisionId;
        public List<string> options = new List<string>();
        public bool isQuestion;

        public override void OpenLetter()
        {
            if (isQuestion && !ArchivedOnly && IsOpen(decisionId))
            {
                UIEntry.OpenDecision(decisionId);
                return;
            }
            base.OpenLetter();
        }

        public static bool IsOpen(string decisionId)
        {
            var list = UIUtil.Registry?.OpenDecisions;
            if (list == null || string.IsNullOrEmpty(decisionId)) return false;
            foreach (var d in list)
            {
                if (d != null && d.id == decisionId && !d.answered) return true;
            }
            return false;
        }

        public override bool CanDismissWithRightClick => false;

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                if (ArchivedOnly || !IsOpen(decisionId))
                {
                    yield return Option_Close;
                    yield break;
                }
                if (options != null)
                {
                    foreach (var opt in options)
                    {
                        string chosen = opt;
                        yield return new DiaOption(UIUtil.OneLine(chosen, 60))
                        {
                            action = () => Answer(chosen),
                            resolveTree = true
                        };
                    }
                }
                yield return new DiaOption("ColonyCode_LetterAddReason".Translate())
                {
                    action = UIEntry.OpenDecisions,
                    resolveTree = true
                };
                yield return Option_Postpone;
            }
        }

        private void Answer(string option)
        {
            string id = decisionId;
            UIUtil.Registry?.AnswerDecision(id, option, null);
            LetterHub.RemoveLetterFor(id);
            if (Find.LetterStack != null && Find.LetterStack.LettersListForReading.Contains(this))
                Find.LetterStack.RemoveLetter(this);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref decisionId, "decisionId");
            Scribe_Collections.Look(ref options, "options", LookMode.Value);
            Scribe_Values.Look(ref isQuestion, "isQuestion");
            if (options == null) options = new List<string>();
        }
    }
}
