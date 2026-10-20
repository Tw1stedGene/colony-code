using System.Collections.Generic;
using ColonyCode.Agents;
using UnityEngine;
using Verse;
using LogEntry = ColonyCode.Agents.LogEntry;

namespace ColonyCode.UI
{
    /// <summary>
    /// Scrollable, virtualized view of an agent's log. Every log entry is split into single-line rows
    /// (no wrapping, clipped at the right edge) so only the visible rows are drawn. Colored by kind;
    /// diff lines are red/green. Sticks to the bottom while the user has not scrolled up.
    /// </summary>
    public class LogView
    {
        private struct Row
        {
            public string text;
            public Color color;
            public string tip;
        }

        public const float RowHeight = 18f;

        private readonly bool diffOnly;
        private readonly List<Row> rows = new List<Row>();
        private Vector2 scroll;
        private bool stickToBottom = true;

        // Cache key: rebuild rows when the log list, its count or its last entry changes.
        private List<LogEntry> cachedLog;
        private int cachedCount = -1;
        private LogEntry cachedLast;
        private string cachedAgentId;

        /// <summary>File header rows (diff mode) with the row index they start at, for the file list.</summary>
        public readonly List<KeyValuePair<string, int>> Files = new List<KeyValuePair<string, int>>();

        private readonly int maxRows;

        /// <param name="maxRows">When &gt; 0, only the last that many rows (lines) are kept.</param>
        public LogView(bool diffOnly = false, int maxRows = 0)
        {
            this.diffOnly = diffOnly;
            this.maxRows = maxRows;
        }

        public int RowCount => rows.Count;

        public void ScrollToRow(int index)
        {
            scroll.y = Mathf.Max(0f, index * RowHeight);
            stickToBottom = false;
        }

        public void ScrollToBottom()
        {
            stickToBottom = true;
        }

        public void Draw(Rect rect, AgentInfo agent)
        {
            Rebuild(agent);
            Widgets.DrawBoxSolid(rect, new Color(0.06f, 0.06f, 0.07f, 0.9f));
            var outRect = rect.ContractedBy(4f);
            if (rows.Count == 0)
            {
                GUI.color = UIUtil.ColMuted;
                Text.Font = GameFont.Small;
                Widgets.Label(outRect, (diffOnly ? "ColonyCode_NoDiffs" : "ColonyCode_NoLog").Translate());
                GUI.color = Color.white;
                return;
            }

            float contentHeight = rows.Count * RowHeight;
            var viewRect = new Rect(0f, 0f, outRect.width - 16f, contentHeight);
            float maxScroll = Mathf.Max(0f, contentHeight - outRect.height);
            if (stickToBottom) scroll.y = maxScroll;

            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            var oldFont = Text.Font;
            var oldWrap = Text.WordWrap;
            var oldAnchor = Text.Anchor;
            Text.Font = GameFont.Tiny;
            Text.WordWrap = false;
            Text.Anchor = TextAnchor.MiddleLeft;

            int first = Mathf.Max(0, Mathf.FloorToInt(scroll.y / RowHeight) - 1);
            int last = Mathf.Min(rows.Count - 1, Mathf.CeilToInt((scroll.y + outRect.height) / RowHeight) + 1);
            for (int i = first; i <= last; i++)
            {
                var row = rows[i];
                var r = new Rect(2f, i * RowHeight, viewRect.width - 2f, RowHeight);
                if (row.tip != null && Mouse.IsOver(r))
                {
                    Widgets.DrawHighlight(r);
                    TooltipHandler.TipRegion(r, row.tip);
                }
                GUI.color = row.color;
                Widgets.Label(r, row.text);
            }
            GUI.color = Color.white;
            Text.Font = oldFont;
            Text.WordWrap = oldWrap;
            Text.Anchor = oldAnchor;
            Widgets.EndScrollView();

            // The user scrolled up: stop following; back at the bottom: follow again.
            stickToBottom = scroll.y >= maxScroll - 2f;
        }

        private void Rebuild(AgentInfo agent)
        {
            var log = agent?.log;
            string agentId = agent?.id;
            int count = log?.Count ?? 0;
            LogEntry last = count > 0 ? log[count - 1] : null;
            if (ReferenceEquals(log, cachedLog) && count == cachedCount && ReferenceEquals(last, cachedLast) && agentId == cachedAgentId)
                return;
            if (agentId != cachedAgentId) stickToBottom = true;
            cachedLog = log;
            cachedCount = count;
            cachedLast = last;
            cachedAgentId = agentId;

            rows.Clear();
            Files.Clear();
            if (log == null) return;
            for (int i = 0; i < count; i++)
            {
                var e = log[i];
                if (e == null) continue;
                bool isDiff = e.kind == "diff";
                if (diffOnly && !isDiff) continue;
                AddEntry(e, isDiff);
            }
            if (maxRows > 0 && rows.Count > maxRows)
            {
                int drop = rows.Count - maxRows;
                rows.RemoveRange(0, drop);
                for (int i = Files.Count - 1; i >= 0; i--)
                {
                    if (Files[i].Value < drop) Files.RemoveAt(i);
                    else Files[i] = new KeyValuePair<string, int>(Files[i].Key, Files[i].Value - drop);
                }
            }
        }

        private void AddEntry(LogEntry e, bool isDiff)
        {
            string text = e.text ?? "";
            string[] lines = text.Replace("\r", "").Split('\n');
            string time = UIUtil.FormatTime(e.ts);
            if (isDiff)
            {
                if (!diffOnly)
                {
                    rows.Add(new Row { text = (time.Length > 0 ? time + "  " : "") + "ColonyCode_LogDiffHeader".Translate(), color = UIUtil.ColMuted });
                }
                foreach (var raw in lines)
                {
                    string line = raw;
                    if (line.Length == 0) continue;
                    if (diffOnly && (line.StartsWith("+++ ") || line.StartsWith("diff --git") || line.StartsWith("Index: ")))
                    {
                        string file = FileNameOf(line);
                        if (file != null && (Files.Count == 0 || Files[Files.Count - 1].Key != file || Files[Files.Count - 1].Value < rows.Count - 3))
                            Files.Add(new KeyValuePair<string, int>(file, rows.Count));
                    }
                    rows.Add(new Row { text = UIUtil.Safe(Clip(line)), color = UIUtil.DiffLineColor(line), tip = line.Length > 160 ? UIUtil.Safe(line) : null });
                }
                return;
            }

            var color = UIUtil.KindColor(e.kind);
            string prefix = (time.Length > 0 ? time + "  " : "") + KindTag(e.kind);
            string indent = new string(' ', 4);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (i > 0 && line.Length == 0 && i == lines.Length - 1) continue;
                string shown = i == 0 ? prefix + line : indent + line;
                rows.Add(new Row { text = UIUtil.Safe(Clip(shown)), color = color, tip = shown.Length > 160 ? UIUtil.Safe(text) : null });
            }
        }

        private static string Clip(string s)
        {
            return s.Length > 400 ? s.Substring(0, 400) + "…" : s;
        }

        private static string KindTag(string kind)
        {
            switch (kind)
            {
                case "tool": return "> ";
                case "result": return "  ";
                case "error": return "! ";
                default: return "";
            }
        }

        /// <summary>"+++ b/src/a.ts" or "diff --git a/x b/x" -> "src/a.ts".</summary>
        public static string FileNameOf(string header)
        {
            string s = header;
            if (s.StartsWith("+++ ") || s.StartsWith("--- ")) s = s.Substring(4);
            else if (s.StartsWith("Index: ")) s = s.Substring(7);
            else if (s.StartsWith("diff --git "))
            {
                s = s.Substring(11);
                int sp = s.LastIndexOf(" b/");
                if (sp >= 0) s = s.Substring(sp + 1);
            }
            s = s.Trim();
            int tab = s.IndexOf('\t');
            if (tab > 0) s = s.Substring(0, tab);
            if (s.StartsWith("a/") || s.StartsWith("b/")) s = s.Substring(2);
            if (s == "/dev/null" || s.Length == 0) return null;
            return s;
        }
    }
}
