using System.Collections.Generic;
using ColonyCode.Agents;
using UnityEngine;
using Verse;
using LogEntry = ColonyCode.Agents.LogEntry;

namespace ColonyCode.UI
{
    /// <summary>
    /// Scrollable, virtualized view of an agent's log. Each log entry becomes rows (one per line of its
    /// text); a row word-wraps to the panel width. The time sits in its own column and wrapped lines
    /// continue under the text column. Claude's answers (kind "text") are shown as light markdown;
    /// diff lines keep their red/green on every wrapped line.
    ///
    /// Cost: an entry's rows (display strings) are built once, when the entry first shows; a row's
    /// height is measured once per (quantized) width. Rebuild runs only when the log changes, layout
    /// only when rows or the width change; drawing a frame allocates nothing and draws only the rows in
    /// view. Sticks to the bottom while the user has not scrolled up.
    /// </summary>
    public class LogView
    {
        private sealed class Row
        {
            public string time;   // null on every row but an entry's first
            public string text;   // display string (safe rich text)
            public Color color;
            public string file;   // diff-only mode: the file a "+++ b/x" header row starts
            public float height;
            public float measuredFor = -1f;
        }

        /// <summary>Height of one unwrapped row.</summary>
        public const float RowHeight = 18f;
        private const float TimeGap = 6f;
        private const float WidthStep = 4f;
        private const float ScrollbarWidth = 16f;

        private readonly bool diffOnly;
        private readonly int maxRows;
        private readonly List<Row> rows = new List<Row>();
        /// <summary>tops[i] = y of row i; tops[rows.Count] = total height.</summary>
        private float[] tops = new float[1];
        private Vector2 scroll;
        private bool stickToBottom = true;

        // Rows per entry, reused while the entry stays in the log.
        private Dictionary<LogEntry, List<Row>> entryRows = new Dictionary<LogEntry, List<Row>>();
        private Dictionary<LogEntry, List<Row>> spareRows = new Dictionary<LogEntry, List<Row>>();

        // Cache key: rebuild rows when the log list, its count or its last entry changes.
        private List<LogEntry> cachedLog;
        private int cachedCount = -1;
        private LogEntry cachedLast;
        private string cachedAgentId;
        private bool layoutDirty = true;
        private float layoutWidth = -1f;
        private int pendingScrollRow = -1;

        private static float timeColumn = -1f;
        private static string diffHeader;
        private static LoadedLanguage diffHeaderFor;

        /// <summary>File header rows (diff mode) with the row index they start at, for the file list.</summary>
        public readonly List<KeyValuePair<string, int>> Files = new List<KeyValuePair<string, int>>();

        /// <param name="maxRows">When &gt; 0, only the last that many rows (lines) are kept.</param>
        public LogView(bool diffOnly = false, int maxRows = 0)
        {
            this.diffOnly = diffOnly;
            this.maxRows = maxRows;
        }

        public int RowCount => rows.Count;

        public void ScrollToRow(int index)
        {
            pendingScrollRow = index;
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

            var oldFont = Text.Font;
            var oldWrap = Text.WordWrap;
            var oldAnchor = Text.Anchor;
            Text.Font = GameFont.Tiny;
            Text.WordWrap = true;
            Text.Anchor = TextAnchor.UpperLeft;

            float viewWidth = outRect.width - ScrollbarWidth;
            float textX = 2f + TimeColumn();
            // Quantized, so a window being resized re-measures every few pixels, not every frame.
            float textW = Mathf.Max(40f, Mathf.Floor((viewWidth - textX - 2f) / WidthStep) * WidthStep);
            if (layoutDirty || textW != layoutWidth) Layout(textW);

            float contentHeight = tops[rows.Count];
            var viewRect = new Rect(0f, 0f, viewWidth, contentHeight);
            float maxScroll = Mathf.Max(0f, contentHeight - outRect.height);
            if (pendingScrollRow >= 0)
            {
                scroll.y = Mathf.Min(maxScroll, tops[Mathf.Clamp(pendingScrollRow, 0, rows.Count)]);
                pendingScrollRow = -1;
            }
            else if (stickToBottom) scroll.y = maxScroll;

            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            int first = FirstVisible(scroll.y);
            float bottom = scroll.y + outRect.height;
            for (int i = first; i < rows.Count && tops[i] <= bottom; i++)
            {
                var row = rows[i];
                float y = tops[i];
                if (row.time != null)
                {
                    GUI.color = UIUtil.ColMuted;
                    Widgets.Label(new Rect(2f, y, textX - 2f, RowHeight), row.time);
                }
                GUI.color = row.color;
                Widgets.Label(new Rect(textX, y, textW, row.height), row.text);
            }
            GUI.color = Color.white;
            Widgets.EndScrollView();
            Text.Font = oldFont;
            Text.WordWrap = oldWrap;
            Text.Anchor = oldAnchor;

            // The user scrolled up: stop following; back at the bottom: follow again.
            stickToBottom = scroll.y >= maxScroll - 2f;
        }

        /// <summary>Width of the time column (Tiny font), measured once; 0 in diff-only mode.</summary>
        private float TimeColumn()
        {
            if (diffOnly) return 0f;
            if (timeColumn < 0f) timeColumn = Text.CalcSize("00:00:00").x + TimeGap;
            return timeColumn;
        }

        /// <summary>Index of the first row whose bottom is below <paramref name="y"/> (binary search).</summary>
        private int FirstVisible(float y)
        {
            int lo = 0, hi = rows.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (tops[mid + 1] <= y) lo = mid + 1;
                else hi = mid;
            }
            return Mathf.Max(0, lo);
        }

        /// <summary>Measures rows not yet measured at this width (Tiny font, word wrap on), then the offsets.</summary>
        private void Layout(float width)
        {
            layoutWidth = width;
            layoutDirty = false;
            if (tops.Length < rows.Count + 1) tops = new float[Mathf.Max(rows.Count + 1, tops.Length * 2)];
            float y = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.measuredFor != width)
                {
                    row.measuredFor = width;
                    row.height = Mathf.Max(RowHeight, Mathf.Ceil(UIUtil.TextHeight(row.text, width)) + 2f);
                }
                tops[i] = y;
                y += row.height;
            }
            tops[rows.Count] = y;
        }

        private void Rebuild(AgentInfo agent)
        {
            var log = agent?.log;
            string agentId = agent?.id;
            int count = log?.Count ?? 0;
            LogEntry last = count > 0 ? log[count - 1] : null;
            if (ReferenceEquals(log, cachedLog) && count == cachedCount && ReferenceEquals(last, cachedLast) && agentId == cachedAgentId)
                return;
            if (agentId != cachedAgentId)
            {
                stickToBottom = true;
                entryRows.Clear();
            }
            cachedLog = log;
            cachedCount = count;
            cachedLast = last;
            cachedAgentId = agentId;
            layoutDirty = true;

            rows.Clear();
            Files.Clear();
            spareRows.Clear();
            if (log != null)
            {
                for (int i = 0; i < count; i++)
                {
                    var e = log[i];
                    if (e == null) continue;
                    bool isDiff = e.kind == "diff";
                    if (diffOnly && !isDiff) continue;
                    List<Row> mine;
                    if (!entryRows.TryGetValue(e, out mine)) mine = BuildEntry(e, isDiff);
                    spareRows[e] = mine;
                    foreach (var r in mine)
                    {
                        if (r.file != null && (Files.Count == 0 || Files[Files.Count - 1].Key != r.file || Files[Files.Count - 1].Value < rows.Count - 3))
                            Files.Add(new KeyValuePair<string, int>(r.file, rows.Count));
                        rows.Add(r);
                    }
                }
            }
            // keep only the rows of entries still in the log
            var t = entryRows;
            entryRows = spareRows;
            spareRows = t;
            spareRows.Clear();

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

        private List<Row> BuildEntry(LogEntry e, bool isDiff)
        {
            var list = new List<Row>();
            string text = e.text ?? "";
            string time = diffOnly ? null : UIUtil.FormatTime(e.ts);
            if (time != null && time.Length == 0) time = null;
            if (isDiff)
            {
                if (!diffOnly)
                {
                    if (diffHeader == null || diffHeaderFor != LanguageDatabase.activeLanguage)
                    {
                        diffHeaderFor = LanguageDatabase.activeLanguage;
                        diffHeader = "ColonyCode_LogDiffHeader".Translate();
                    }
                    list.Add(new Row { time = time, text = diffHeader, color = UIUtil.ColMuted });
                }
                foreach (var raw in text.Replace("\r", "").Split('\n'))
                {
                    if (raw.Length == 0) continue;
                    string line = raw.Length > LightMarkdown.LineMax ? raw.Substring(0, LightMarkdown.LineMax) + "…" : raw;
                    string file = null;
                    if (diffOnly && (line.StartsWith("+++ ") || line.StartsWith("diff --git") || line.StartsWith("Index: ")))
                        file = FileNameOf(line);
                    // one label per diff line: its colour holds on every wrapped line
                    list.Add(new Row { text = UIUtil.Safe(line), color = UIUtil.DiffLineColor(line), file = file });
                }
                return list;
            }

            var color = UIUtil.KindColor(e.kind);
            string tag = KindTag(e.kind);
            List<string> lines;
            if (e.kind == "text") lines = LightMarkdown.Lines(text);
            else
            {
                lines = new List<string>();
                var parts = text.Replace("\r", "").Split('\n');
                int n = parts.Length;
                while (n > 1 && parts[n - 1].Length == 0) n--;
                for (int i = 0; i < n; i++)
                {
                    string p = parts[i];
                    lines.Add(UIUtil.Safe(p.Length > LightMarkdown.LineMax ? p.Substring(0, LightMarkdown.LineMax) + "…" : p));
                }
            }
            if (lines.Count == 0) lines.Add("");
            for (int i = 0; i < lines.Count; i++)
            {
                // an empty line still takes a row, so paragraphs keep their gap
                list.Add(new Row { time = i == 0 ? time : null, text = i == 0 ? tag + lines[i] : lines[i], color = color });
            }
            return list;
        }

        private static string KindTag(string kind)
        {
            switch (kind)
            {
                case "tool": return "> ";
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
