using System.Collections.Generic;
using ColonyCode.Agents;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ColonyCode.UI
{
    /// <summary>
    /// Console: type a goal and press Enter, or "@name message" to message one agent ("@all" for
    /// everyone). Shows an autocomplete list for @names (Tab completes) and the recent goal feed.
    /// </summary>
    public class Window_Console : Window
    {
        private const string InputControl = "ColonyCode_ConsoleInput";
        private const int MaxInput = 4000;

        private static readonly List<string> history = new List<string>();
        private static readonly List<string> feedback = new List<string>();

        private string input = "";
        private int historyIndex = -1;
        private bool focusPending = true;
        private int lastSubmitFrame = -1;
        private Vector2 feedScroll;
        private int openFrame = -10;
        /// <summary>Session picked in the dropdown; null = any session. Kept while the game runs.</summary>
        private static string pickedSession;

        public Window_Console()
        {
            doCloseX = true;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            draggable = true;
            resizeable = true;
            preventCameraMotion = false;
            onlyOneOfTypeAllowed = true;
            forcePause = false;
            optionalTitle = "ColonyCode_ConsoleTitle".Translate();
        }

        public override Vector2 InitialSize => new Vector2(760f, 520f);

        public void FocusInput()
        {
            focusPending = true;
        }

        /// <summary>Replaces the input text (e.g. "@marlow " from the agent card) and focuses it.</summary>
        public void SetInput(string text)
        {
            input = text ?? "";
            historyIndex = -1;
            focusPending = true;
        }

        /// <summary>The session goals, @all messages and tasks go to; null = any session.</summary>
        public static string PickedSession => SessionPicker.Validate(pickedSession);

        public override void OnAcceptKeyPressed()
        {
            Submit();
        }

        public override void DoWindowContents(Rect inRect)
        {
            HandleKeys();

            Text.Font = GameFont.Small;
            float y = inRect.y;

            // Connection line + session picker.
            pickedSession = SessionPicker.Validate(pickedSession);
            bool picker = SessionPicker.Worthwhile() || pickedSession != null;
            float pickW = picker ? 230f : 0f;
            GUI.color = Core.Bridge.IsRunning ? UIUtil.ColMuted : UIUtil.ColBad;
            Widgets.Label(new Rect(inRect.x, y, inRect.width - pickW - 8f, 22f), UIUtil.Safe(Core.BridgeHost.StatusText));
            GUI.color = Color.white;
            if (picker) SessionPicker.Button(new Rect(inRect.xMax - pickW, y - 2f, pickW, 26f), pickedSession, id => pickedSession = id);
            y += 28f;

            // Input.
            var inputRect = new Rect(inRect.x, y, inRect.width - 110f, 32f);
            GUI.SetNextControlName(InputControl);
            string newInput = Widgets.TextField(inputRect, input, MaxInput);
            if (newInput != input)
            {
                input = newInput;
                historyIndex = -1;
            }
            if (focusPending)
            {
                GUI.FocusControl(InputControl);
                focusPending = false;
            }
            if (Widgets.ButtonText(new Rect(inputRect.xMax + 8f, y, 102f, 32f), "ColonyCode_Send".Translate()))
                Submit();
            y += 36f;

            // Hint / autocomplete.
            y = DrawHints(new Rect(inRect.x, y, inRect.width, inRect.height), y);

            // Feedback of the last submissions.
            if (feedback.Count > 0)
            {
                GUI.color = UIUtil.ColMuted;
                Text.Font = GameFont.Tiny;
                for (int i = Mathf.Max(0, feedback.Count - 2); i < feedback.Count; i++)
                {
                    Widgets.Label(new Rect(inRect.x, y, inRect.width, 18f), feedback[i]);
                    y += 18f;
                }
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                y += 4f;
            }

            // Goal feed.
            Widgets.DrawLineHorizontal(inRect.x, y, inRect.width);
            y += 6f;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "ColonyCode_GoalFeed".Translate());
            y += 26f;
            var feedRect = new Rect(inRect.x, y, inRect.width, inRect.yMax - y - 32f);
            DrawGoalFeed(feedRect);

            // Footer buttons.
            float by = inRect.yMax - 28f;
            if (Widgets.ButtonText(new Rect(inRect.x, by, 160f, 28f), "ColonyCode_OpenDecisions".Translate(UIUtil.OpenDecisionCount())))
                UIEntry.OpenDecisions();
        }

        public override void PostOpen()
        {
            base.PostOpen();
            openFrame = Time.frameCount;
        }

        /// <summary>True for the character the console hotkey types (default '\\'; follows a rebinding to '`').</summary>
        private static bool IsHotkeyCharacter(char c)
        {
            if (c == '\0') return false;
            var key = ColonyCodeUIDefOf.ColonyCode_OpenConsole;
            KeyCode code = key != null ? key.MainKey : KeyCode.Backslash;
            switch (code)
            {
                case KeyCode.Backslash: return c == '\\';
                case KeyCode.BackQuote: return c == '`';
                case KeyCode.Slash: return c == '/';
                default: return false;
            }
        }

        private void HandleKeys()
        {
            var ev = Event.current;
            if (ev.type != EventType.KeyDown) return;
            // The hotkey that opened the window also sends its character event; do not type it.
            if (IsHotkeyCharacter(ev.character) && Time.frameCount - openFrame <= 2)
            {
                ev.Use();
                return;
            }
            bool focused = GUI.GetNameOfFocusedControl() == InputControl;
            if (!focused) return;
            // Enter and Tab also arrive as separate character events; keep them out of the field.
            if (ev.keyCode == KeyCode.None && (ev.character == '\n' || ev.character == '\r' || ev.character == '\t'))
            {
                ev.Use();
                return;
            }
            switch (ev.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    Submit();
                    ev.Use();
                    break;
                case KeyCode.Tab:
                    TryComplete();
                    ev.Use();
                    break;
                case KeyCode.UpArrow:
                    if (history.Count > 0)
                    {
                        historyIndex = historyIndex < 0 ? history.Count - 1 : Mathf.Max(0, historyIndex - 1);
                        input = history[historyIndex];
                        ev.Use();
                    }
                    break;
                case KeyCode.DownArrow:
                    if (historyIndex >= 0)
                    {
                        historyIndex++;
                        if (historyIndex >= history.Count) { historyIndex = -1; input = ""; }
                        else input = history[historyIndex];
                        ev.Use();
                    }
                    break;
            }
        }

        /// <summary>The partial "@name" being typed (no space yet), or null.</summary>
        private string TypedHandle()
        {
            if (string.IsNullOrEmpty(input) || input[0] != '@') return null;
            int sp = input.IndexOf(' ');
            return sp < 0 ? input.Substring(1) : null;
        }

        private void TryComplete()
        {
            string partial = TypedHandle();
            if (partial == null) return;
            if ("all".StartsWith(partial.ToLowerInvariant()) && partial.Length > 0 && UIUtil.MatchAgents(partial).Count == 0)
            {
                input = "@all ";
                return;
            }
            var matches = UIUtil.MatchAgents(partial);
            if (matches.Count > 0) input = "@" + UIUtil.Handle(matches[0]) + " ";
        }

        private float DrawHints(Rect area, float y)
        {
            Text.Font = GameFont.Tiny;
            string partial = TypedHandle();
            if (partial != null)
            {
                var matches = UIUtil.MatchAgents(partial);
                if (matches.Count == 0 && !"all".StartsWith(partial.ToLowerInvariant()))
                {
                    GUI.color = UIUtil.ColBad;
                    Widgets.Label(new Rect(area.x, y, area.width, 18f), "ColonyCode_NoAgentMatch".Translate(UIUtil.Safe(partial)));
                    y += 18f;
                }
                else
                {
                    int shown = 0;
                    foreach (var a in matches)
                    {
                        if (shown++ >= 6) break;
                        var r = new Rect(area.x, y, area.width, 18f);
                        Widgets.DrawHighlightIfMouseover(r);
                        UIUtil.DrawDot(new Rect(r.x + 2f, r.y + 4f, 10f, 10f), UIUtil.StateColor(a.state));
                        GUI.color = Color.white;
                        Widgets.Label(new Rect(r.x + 16f, r.y, r.width - 16f, r.height),
                            "@" + UIUtil.Safe(UIUtil.Handle(a)) + "   " + UIUtil.AgentLabel(a) + " - " + UIUtil.RoleLabel(a.role) + " - " + UIUtil.StateLabel(a.state));
                        if (Widgets.ButtonInvisible(r))
                        {
                            input = "@" + UIUtil.Handle(a) + " ";
                            focusPending = true;
                        }
                        y += 18f;
                    }
                    if ("all".StartsWith(partial.ToLowerInvariant()))
                    {
                        GUI.color = UIUtil.ColMuted;
                        Widgets.Label(new Rect(area.x + 16f, y, area.width - 16f, 18f), "ColonyCode_HintAll".Translate());
                        y += 18f;
                    }
                }
            }
            else
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(new Rect(area.x, y, area.width, 18f), "ColonyCode_ConsoleHint".Translate());
                y += 18f;
            }
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            return y + 4f;
        }

        private void DrawGoalFeed(Rect rect)
        {
            var goals = UIUtil.Registry?.Goals;
            Widgets.DrawMenuSection(rect);
            var outRect = rect.ContractedBy(6f);
            if (goals == null || goals.Count == 0)
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(outRect, "ColonyCode_NoGoals".Translate());
                GUI.color = Color.white;
                return;
            }
            const float rowH = 44f;
            var viewRect = new Rect(0f, 0f, outRect.width - 16f, goals.Count * rowH);
            Widgets.BeginScrollView(outRect, ref feedScroll, viewRect);
            // Newest first.
            int row = 0;
            for (int i = goals.Count - 1; i >= 0; i--)
            {
                var g = goals[i];
                if (g == null) continue;
                GoalRow.Draw(new Rect(0f, row * rowH, viewRect.width, rowH - 4f), g);
                row++;
            }
            Widgets.EndScrollView();
        }

        private void Submit()
        {
            if (lastSubmitFrame == Time.frameCount) return;
            lastSubmitFrame = Time.frameCount;

            string text = (input ?? "").Trim();
            if (text.Length == 0) return;
            var reg = UIUtil.Registry;
            if (reg == null)
            {
                Messages.Message("ColonyCode_NoRegistry".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (!Core.Bridge.IsRunning)
            {
                Messages.Message("ColonyCode_BridgeDown".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }

            if (text[0] == '@')
            {
                int sp = text.IndexOf(' ');
                string name = sp < 0 ? text.Substring(1) : text.Substring(1, sp - 1);
                string msg = sp < 0 ? "" : text.Substring(sp + 1).Trim();
                if (msg.Length == 0)
                {
                    Messages.Message("ColonyCode_EmptyMessage".Translate(), MessageTypeDefOf.RejectInput, false);
                    return;
                }
                string to;
                if (name.ToLowerInvariant() == "all") to = "all";
                else
                {
                    to = UIUtil.ResolveAgentId(name);
                    if (to == null)
                    {
                        Messages.Message("ColonyCode_NoAgentMatch".Translate(UIUtil.Safe(name)), MessageTypeDefOf.RejectInput, false);
                        return;
                    }
                }
                string session = PickedSession;
                reg.SendMessage(to, msg, to == "all" ? session : null);
                string target = to == "all" ? (session != null ? "all [" + UIUtil.SessionLabel(session) + "]" : "all") : UIUtil.AgentLabel(reg.Get(to));
                AddFeedback("ColonyCode_SentMessage".Translate(target, UIUtil.OneLine(msg, 80)));
            }
            else
            {
                string session = PickedSession;
                reg.SubmitGoal(text, session);
                AddFeedback((session != null ? "[" + UIUtil.SessionLabel(session) + "] " : "") + "ColonyCode_SentGoal".Translate(UIUtil.OneLine(text, 90)));
            }
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
            history.Add(text);
            if (history.Count > 50) history.RemoveAt(0);
            historyIndex = -1;
            input = "";
            focusPending = true;
        }

        private static void AddFeedback(string s)
        {
            feedback.Add(UIUtil.FormatTime(ColonyCode.Http.Json.NowMs()) + "  " + s);
            if (feedback.Count > 10) feedback.RemoveAt(0);
        }
    }

    /// <summary>One goal: status, text, progress bar.</summary>
    public static class GoalRow
    {
        public static void Draw(Rect r, GoalInfo g)
        {
            Widgets.DrawHighlightIfMouseover(r);
            string status = g.status ?? "active";
            Color c = status == "done" ? UIUtil.ColGood : status == "failed" ? UIUtil.ColBad : UIUtil.ColTool;
            Text.Font = GameFont.Small;
            var oldWrap = Text.WordWrap;
            Text.WordWrap = false;
            var statusRect = new Rect(r.x + 4f, r.y, 80f, 22f);
            GUI.color = c;
            Widgets.Label(statusRect, ("ColonyCode_GoalStatus_" + status).CanTranslate() ? ("ColonyCode_GoalStatus_" + status).Translate().ToString() : status);
            GUI.color = Color.white;
            Widgets.Label(new Rect(statusRect.xMax, r.y, r.width - statusRect.width - 8f, 22f), UIUtil.SessionPrefix(g.sessionId) + UIUtil.OneLine(g.text, 160));
            Text.WordWrap = oldWrap;
            TooltipHandler.TipRegion(r, UIUtil.Safe((g.text ?? "") + (string.IsNullOrEmpty(g.summary) ? "" : "\n\n" + g.summary)));

            // progress < 0 means "unknown" (the plugin did not report one).
            bool known = status == "done" || g.progress >= 0f;
            float p = status == "done" ? 1f : Mathf.Clamp01(g.progress);
            var bar = new Rect(r.x + 4f, r.y + 24f, r.width - 8f, 12f);
            Widgets.FillableBar(bar, known ? p : 0f, status == "failed" ? UIUtil.BarFailTex : UIUtil.BarFillTex, UIUtil.BarBgTex, true);
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(bar, known ? Mathf.RoundToInt(p * 100f) + "%" : "ColonyCode_ProgressUnknown".Translate().ToString());
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
        }
    }
}
