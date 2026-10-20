using System;
using System.Collections.Generic;
using System.Text;
using ColonyCode.Agents;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyCode.UI
{
    /// <summary>Entry points other code (buildings, gizmos, settings) uses to open the Colony Code windows.</summary>
    public static class UIEntry
    {
        /// <summary>Toggles the console window (goal / @agent message input).</summary>
        public static void OpenConsole()
        {
            if (Find.WindowStack == null) return;
            var open = Find.WindowStack.WindowOfType<Window_Console>();
            if (open != null)
            {
                open.FocusInput();
                return;
            }
            Find.WindowStack.Add(new Window_Console());
        }

        public static void ToggleConsole()
        {
            if (Find.WindowStack == null) return;
            if (!Find.WindowStack.TryRemove(typeof(Window_Console))) Find.WindowStack.Add(new Window_Console());
        }

        /// <summary>Opens (or brings forward) the list of open decisions.</summary>
        public static void OpenDecisions()
        {
            if (Find.WindowStack == null) return;
            if (Find.WindowStack.IsOpen(typeof(Window_Decisions))) return;
            Find.WindowStack.Add(new Window_Decisions());
        }

        /// <summary>Opens the task board window (To do / In progress / Done), or leaves an open one as is.</summary>
        public static void OpenTaskBoard()
        {
            if (Find.WindowStack == null) return;
            if (Find.WindowStack.IsOpen(typeof(Window_TaskBoard))) return;
            Find.WindowStack.Add(new Window_TaskBoard());
        }

        /// <summary>Opens the console with the input set to <paramref name="prefill"/> (e.g. "@marlow ").</summary>
        public static void OpenConsole(string prefill)
        {
            if (Find.WindowStack == null) return;
            var open = Find.WindowStack.WindowOfType<Window_Console>();
            if (open == null)
            {
                open = new Window_Console();
                Find.WindowStack.Add(open);
            }
            if (prefill != null) open.SetInput(prefill);
            open.FocusInput();
        }

        /// <summary>Opens the card of one agent (replaces an open card).</summary>
        public static void OpenAgentCard(string agentId)
        {
            if (Find.WindowStack == null || string.IsNullOrEmpty(agentId)) return;
            var open = Find.WindowStack.WindowOfType<Window_AgentCard>();
            if (open != null)
            {
                open.agentId = agentId;
                Find.WindowStack.Notify_ClickedInsideWindow(open);
                return;
            }
            Find.WindowStack.Add(new Window_AgentCard(agentId));
        }

        /// <summary>
        /// Opens the review window (worktrees + diff + Merge / Request changes / Reject) for a session.
        /// Null or unknown: the first connected session.
        /// </summary>
        public static void OpenReview(string sessionId)
        {
            if (Find.WindowStack == null) return;
            var open = Find.WindowStack.WindowOfType<Window_Review>();
            if (open != null)
            {
                if (!string.IsNullOrEmpty(sessionId)) open.SelectSession(sessionId);
                Find.WindowStack.Notify_ClickedInsideWindow(open);
                return;
            }
            Find.WindowStack.Add(new Window_Review(sessionId));
        }

        /// <summary>Opens the journal of finished goals (saved with the game).</summary>
        public static void OpenJournal()
        {
            if (Find.WindowStack == null) return;
            if (Find.WindowStack.IsOpen(typeof(Window_Journal))) return;
            Find.WindowStack.Add(new Window_Journal());
        }

        /// <summary>Opens the decision dialog for one decision (used by its letter).</summary>
        public static void OpenDecision(string decisionId)
        {
            if (Find.WindowStack == null || string.IsNullOrEmpty(decisionId)) return;
            foreach (var w in Find.WindowStack.Windows)
                if (w is Dialog_Decision dd && dd.decisionId == decisionId) return;
            Find.WindowStack.Add(new Dialog_Decision(decisionId));
        }

        /// <summary>Opens the diff viewer for one agent. Replaces an open diff window for another agent.</summary>
        public static void OpenDiff(string agentId)
        {
            if (Find.WindowStack == null || string.IsNullOrEmpty(agentId)) return;
            Find.WindowStack.TryRemove(typeof(Window_Diff), false);
            Find.WindowStack.Add(new Window_Diff(agentId));
        }
    }

    /// <summary>Shared helpers for the Colony Code UI: names, colors, safe text.</summary>
    [StaticConstructorOnStartup]
    public static class UIUtil
    {
        public static readonly Texture2D DotTex = ContentFinder<Texture2D>.Get("UI/ColonyCode_Dot", false) ?? BaseContent.WhiteTex;
        public static readonly Texture2D BarFillTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.25f, 0.6f, 0.3f));
        public static readonly Texture2D BarFailTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.65f, 0.2f, 0.2f));
        public static readonly Texture2D BarBgTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.12f, 0.12f, 0.12f));

        public static readonly Color ColText = new Color(0.9f, 0.9f, 0.9f);
        public static readonly Color ColTool = new Color(0.55f, 0.75f, 1f);
        public static readonly Color ColResult = new Color(0.65f, 0.65f, 0.65f);
        public static readonly Color ColError = new Color(1f, 0.45f, 0.4f);
        public static readonly Color ColDiffAdd = new Color(0.45f, 0.9f, 0.45f);
        public static readonly Color ColDiffDel = new Color(1f, 0.45f, 0.45f);
        public static readonly Color ColDiffHunk = new Color(0.5f, 0.85f, 0.95f);
        public static readonly Color ColDiffFile = new Color(1f, 0.85f, 0.4f);
        public static readonly Color ColMuted = new Color(0.6f, 0.6f, 0.6f);
        public static readonly Color ColGood = new Color(0.4f, 0.85f, 0.4f);
        public static readonly Color ColBad = new Color(1f, 0.4f, 0.35f);

        public static AgentRegistry Registry => AgentRegistry.Instance;

        public static string AgentName(AgentInfo a)
        {
            if (a == null) return "?";
            if (!string.IsNullOrEmpty(a.displayName)) return a.displayName;
            if (!string.IsNullOrEmpty(a.label)) return a.label;
            if (a.pawn != null) return a.pawn.LabelShort;
            return a.id ?? "?";
        }

        /// <summary>"[session] " when several sessions are known, else "". Safe for display.</summary>
        public static string SessionPrefix(string sessionId)
        {
            var reg = Registry;
            return reg == null ? "" : Safe(reg.SessionPrefix(sessionId));
        }

        /// <summary>Session label for display (escaped), "?" when unknown.</summary>
        public static string SessionLabel(string sessionId)
        {
            var reg = Registry;
            return Safe(reg != null ? reg.SessionLabel(sessionId) : (sessionId ?? "?"));
        }

        /// <summary>The agent's name with the [session] prefix when several sessions are known. Safe for display.</summary>
        public static string AgentLabel(AgentInfo a)
        {
            if (a == null) return "?";
            return SessionPrefix(a.sessionId) + Safe(AgentName(a));
        }

        /// <summary>12345 -> "12.3k", 2500000 -> "2.5M".</summary>
        public static string FormatTokens(long n)
        {
            if (n < 1000) return n.ToString();
            if (n < 1000000) return (n / 1000f).ToString(n < 10000 ? "0.0" : "0") + "k";
            return (n / 1000000f).ToString(n < 10000000 ? "0.00" : "0.0") + "M";
        }

        /// <summary>In-game date of an absolute tick, at the current map's longitude; "" when unknown.</summary>
        public static string GameDate(long ticksAbs)
        {
            if (ticksAbs < 0) return "";
            try
            {
                Vector2 loc = Vector2.zero;
                var map = Find.CurrentMap ?? Find.AnyPlayerHomeMap;
                if (map != null && Find.WorldGrid != null) loc = Find.WorldGrid.LongLatOf(map.Tile);
                return GenDate.DateFullStringWithHourAt(ticksAbs, loc);
            }
            catch
            {
                return "";
            }
        }

        /// <summary>A small "sent" toast.</summary>
        public static void Toast(string text)
        {
            Messages.Message(text, MessageTypeDefOf.SilentInput, false);
        }

        public static string AgentName(string agentId)
        {
            var reg = Registry;
            var a = reg?.Get(agentId);
            return a != null ? AgentName(a) : (agentId ?? "?");
        }

        public static Color StateColor(AgentState s)
        {
            switch (s)
            {
                case AgentState.Idle: return new Color(0.55f, 0.55f, 0.55f);
                case AgentState.Thinking: return new Color(0.75f, 0.6f, 1f);
                case AgentState.Reading: return new Color(0.4f, 0.7f, 1f);
                case AgentState.Editing: return new Color(1f, 0.8f, 0.3f);
                case AgentState.Running: return new Color(1f, 0.55f, 0.2f);
                case AgentState.Testing: return new Color(0.3f, 0.85f, 0.85f);
                case AgentState.WaitingUser: return new Color(1f, 0.3f, 0.85f);
                case AgentState.Blocked: return new Color(0.85f, 0.25f, 0.25f);
                case AgentState.Done: return new Color(0.35f, 0.9f, 0.35f);
                case AgentState.Error: return new Color(1f, 0.2f, 0.2f);
                default: return Color.white;
            }
        }

        public static string StateLabel(AgentState s)
        {
            return ("ColonyCode_State_" + s).Translate();
        }

        public static string RoleLabel(string role)
        {
            if (role == "lead") return "ColonyCode_Role_lead".Translate();
            if (role == "worker") return "ColonyCode_Role_worker".Translate();
            return role ?? "";
        }

        public static Color KindColor(string kind)
        {
            switch (kind)
            {
                case "tool": return ColTool;
                case "result": return ColResult;
                case "error": return ColError;
                case "diff": return ColText;
                default: return ColText;
            }
        }

        /// <summary>Color for one line of a unified diff.</summary>
        public static Color DiffLineColor(string line)
        {
            if (string.IsNullOrEmpty(line)) return ColResult;
            if (IsDiffFileHeader(line)) return ColDiffFile;
            if (line.StartsWith("@@")) return ColDiffHunk;
            if (line[0] == '+') return ColDiffAdd;
            if (line[0] == '-') return ColDiffDel;
            return ColResult;
        }

        public static bool IsDiffFileHeader(string line)
        {
            return line.StartsWith("+++ ") || line.StartsWith("--- ") || line.StartsWith("diff --git") ||
                   line.StartsWith("Index: ") || line == "+++" || line == "---";
        }

        public static void DrawDot(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, DotTex);
            GUI.color = old;
        }

        /// <summary>
        /// Text coming from Claude Code is shown as-is. Unity rich text would interpret tags such as
        /// &lt;b&gt; or &lt;color=..&gt;; break those up so agent output cannot restyle the UI.
        /// </summary>
        public static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (s.IndexOf('<') < 0) return s.Replace("\t", "    ");
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\t') { sb.Append("    "); continue; }
                sb.Append(c);
                if (c == '<' && i + 1 < s.Length && LooksLikeTag(s, i + 1)) sb.Append(' ');
            }
            return sb.ToString();
        }

        private static readonly string[] TagNames = { "b>", "i>", "color", "size", "material", "quad", "/b>", "/i>", "/color", "/size", "/material" };

        private static bool LooksLikeTag(string s, int start)
        {
            foreach (var t in TagNames)
            {
                if (string.Compare(s, start, t, 0, t.Length, StringComparison.OrdinalIgnoreCase) == 0) return true;
            }
            return false;
        }

        public static string OneLine(string s, int max = 200)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", "").Replace('\n', ' ');
            if (s.Length > max) s = s.Substring(0, max - 1) + "…";
            return Safe(s);
        }

        /// <summary>Lowercase name without spaces, used to match "@name".</summary>
        public static string NameKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (!char.IsWhiteSpace(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>The handle shown/inserted after '@' for an agent.</summary>
        public static string Handle(AgentInfo a)
        {
            var n = NameKey(AgentName(a));
            return string.IsNullOrEmpty(n) ? a.id : n;
        }

        /// <summary>Agents whose name, label, pawn name or id starts with the typed prefix (case-insensitive).</summary>
        public static List<AgentInfo> MatchAgents(string prefix)
        {
            var result = new List<AgentInfo>();
            var reg = Registry;
            if (reg?.Agents == null) return result;
            string p = NameKey(prefix);
            foreach (var a in reg.Agents)
            {
                if (a == null) continue;
                if (p.Length == 0 || Keys(a).Exists(k => k.StartsWith(p))) result.Add(a);
            }
            return result;
        }

        /// <summary>Resolves a typed "@name" to an agent id: exact key match first, then a unique prefix. Null if none/ambiguous.</summary>
        public static string ResolveAgentId(string name)
        {
            var reg = Registry;
            if (reg?.Agents == null) return null;
            string p = NameKey(name);
            if (p.Length == 0) return null;
            foreach (var a in reg.Agents)
            {
                if (a != null && Keys(a).Contains(p)) return a.id;
            }
            var m = MatchAgents(name);
            return m.Count == 1 ? m[0].id : null;
        }

        private static List<string> Keys(AgentInfo a)
        {
            var keys = new List<string>(4);
            void Add(string s) { var k = NameKey(s); if (k.Length > 0) keys.Add(k); }
            Add(a.displayName);
            Add(a.label);
            Add(a.id);
            if (a.pawn != null) Add(a.pawn.LabelShort);
            return keys;
        }

        public static void JumpTo(AgentInfo a)
        {
            var p = a?.pawn;
            if (p == null || p.Destroyed) return;
            CameraJumper.TryJumpAndSelect(p);
        }

        public static bool CanJump(AgentInfo a)
        {
            return a?.pawn != null && !a.pawn.Destroyed;
        }

        public static int OpenDecisionCount()
        {
            var list = Registry?.OpenDecisions;
            if (list == null) return 0;
            int n = 0;
            foreach (var d in list) if (d != null && !d.answered) n++;
            return n;
        }

        public static string FormatTime(long epochMs)
        {
            if (epochMs <= 0) return "";
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(epochMs).ToLocalTime().ToString("HH:mm:ss");
            }
            catch
            {
                return "";
            }
        }
    }
}
