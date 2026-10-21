using System.Collections.Generic;
using ColonyCode.Agents;
using UnityEngine;
using Verse;

namespace ColonyCode.UI
{
    /// <summary>
    /// Draws parsed diffs (<see cref="DiffFile"/>) as rows: a file header, hunk headers, and lines with
    /// old/new line numbers on red/green backgrounds; long runs of unchanged context collapse (click to
    /// expand). Used by the review window and the task details window. Rows are rebuilt only when the
    /// files (or their version) or the expanded set change; a frame draws only the rows in view and
    /// allocates nothing.
    /// </summary>
    public class DiffRowsView
    {
        public const float RowH = 18f;
        private const float NumW = 42f;
        private const int KeepContext = 3;

        private enum RowKind { File, Hunk, Line, Collapsed }

        private struct Row
        {
            public RowKind kind;
            public string text;
            public string kindCode; // add / del / ctx for lines
            public string oldStr, newStr; // cached line-number labels (null when absent)
            public string expandKey; // for collapsed rows
            public string tip;
        }

        private readonly bool fileRowsShown;
        private readonly HashSet<string> expanded = new HashSet<string>();
        private readonly List<Row> rows = new List<Row>();
        private readonly List<KeyValuePair<string, int>> fileRows = new List<KeyValuePair<string, int>>();
        private Vector2 scroll;

        // Row cache key.
        private object cachedKey;
        private int cachedVersion = -1;
        private int cachedExpandCount = -1;

        private static readonly Color AddBg = new Color(0.1f, 0.3f, 0.1f, 0.55f);
        private static readonly Color DelBg = new Color(0.35f, 0.1f, 0.1f, 0.55f);
        private static readonly Color FileBg = new Color(0.2f, 0.2f, 0.22f, 0.95f);

        /// <param name="fileRows">Show a header row per file (off when the view shows one file under its own title).</param>
        public DiffRowsView(bool fileRows = true)
        {
            fileRowsShown = fileRows;
        }

        public int RowCount => rows.Count;

        public void ResetScroll()
        {
            scroll = Vector2.zero;
        }

        /// <summary>Scrolls to the file's header row (when the view holds it).</summary>
        public void ScrollToFile(string path)
        {
            foreach (var kv in fileRows)
                if (kv.Key == path) { scroll.y = kv.Value * RowH; break; }
        }

        /// <summary>
        /// Sets what the view shows. <paramref name="key"/> + <paramref name="version"/> identify the
        /// content: the rows are rebuilt (and the expanded runs forgotten) only when they change.
        /// </summary>
        public void SetFiles(object key, int version, List<DiffFile> files)
        {
            if (ReferenceEquals(key, cachedKey) && version == cachedVersion && expanded.Count == cachedExpandCount) return;
            if (!ReferenceEquals(key, cachedKey))
            {
                expanded.Clear();
                scroll = Vector2.zero;
            }
            cachedKey = key;
            cachedVersion = version;
            rows.Clear();
            fileRows.Clear();
            if (files != null)
                for (int fi = 0; fi < files.Count; fi++)
                {
                    var f = files[fi];
                    fileRows.Add(new KeyValuePair<string, int>(f.path, rows.Count));
                    if (fileRowsShown)
                        rows.Add(new Row { kind = RowKind.File, text = StatusLabel(f.status) + "  " + UIUtil.Safe(f.path) + "   +" + f.additions + " -" + f.deletions, tip = UIUtil.Safe(f.path) });
                    for (int hi = 0; hi < f.hunks.Count; hi++)
                    {
                        var h = f.hunks[hi];
                        if (!string.IsNullOrEmpty(h.header)) rows.Add(new Row { kind = RowKind.Hunk, text = UIUtil.Safe(Clip(h.header)) });
                        AddLines(h, fi + ":" + hi);
                    }
                }
            cachedExpandCount = expanded.Count;
        }

        public static string StatusLabel(string status)
        {
            string k = "ColonyCode_FileStatus_" + (status ?? "modified");
            return k.CanTranslate() ? k.Translate().ToString() : (status ?? "");
        }

        /// <summary>Adds a hunk's lines; long runs of unchanged context are collapsed (click to expand).</summary>
        private void AddLines(DiffHunk h, string hunkKey)
        {
            var lines = h.lines;
            int i = 0;
            while (i < lines.Count)
            {
                if (lines[i].kind != "ctx") { AddLine(lines[i]); i++; continue; }
                int start = i;
                while (i < lines.Count && lines[i].kind == "ctx") i++;
                int end = i; // exclusive
                bool leading = start == 0, trailing = end == lines.Count;
                int keepHead = leading ? 0 : KeepContext;
                int keepTail = trailing ? 0 : KeepContext;
                int len = end - start;
                string key = hunkKey + ":" + start;
                if (len <= keepHead + keepTail + 2 || expanded.Contains(key))
                {
                    for (int k = start; k < end; k++) AddLine(lines[k]);
                    continue;
                }
                for (int k = start; k < start + keepHead; k++) AddLine(lines[k]);
                int hidden = len - keepHead - keepTail;
                rows.Add(new Row { kind = RowKind.Collapsed, text = "ColonyCode_ReviewCollapsed".Translate(hidden), expandKey = key });
                for (int k = end - keepTail; k < end; k++) AddLine(lines[k]);
            }
        }

        private void AddLine(DiffLine l)
        {
            string text = l.text ?? "";
            char sign = l.kind == "add" ? '+' : l.kind == "del" ? '-' : ' ';
            rows.Add(new Row
            {
                kind = RowKind.Line,
                kindCode = l.kind,
                // display text cached with its +/-/space prefix: Draw allocates nothing per event
                text = sign + " " + UIUtil.Safe(Clip(text)),
                oldStr = l.oldNo > 0 ? l.oldNo.ToString() : null,
                newStr = l.newNo > 0 ? l.newNo.ToString() : null,
                tip = text.Length > 160 ? UIUtil.Safe(text.Length > 2000 ? text.Substring(0, 2000) : text) : null,
            });
        }

        private static string Clip(string s) => s.Length > 400 ? s.Substring(0, 400) + "…" : s;

        public void Draw(Rect outRect)
        {
            float contentH = rows.Count * RowH;
            var view = new Rect(0f, 0f, outRect.width - 16f, contentH);
            Widgets.BeginScrollView(outRect, ref scroll, view);
            var oldFont = Text.Font;
            var oldWrap = Text.WordWrap;
            var oldAnchor = Text.Anchor;
            Text.Font = GameFont.Tiny;
            Text.WordWrap = false;
            Text.Anchor = TextAnchor.MiddleLeft;
            int first = Mathf.Max(0, Mathf.FloorToInt(scroll.y / RowH) - 1);
            int last = Mathf.Min(rows.Count - 1, Mathf.CeilToInt((scroll.y + outRect.height) / RowH) + 1);
            string expand = null;
            for (int i = first; i <= last; i++)
            {
                var row = rows[i];
                var r = new Rect(0f, i * RowH, view.width, RowH);
                switch (row.kind)
                {
                    case RowKind.File:
                        Widgets.DrawBoxSolid(r, FileBg);
                        GUI.color = UIUtil.ColDiffFile;
                        Widgets.Label(new Rect(r.x + 4f, r.y, r.width - 8f, r.height), row.text);
                        break;
                    case RowKind.Hunk:
                        GUI.color = UIUtil.ColDiffHunk;
                        Widgets.Label(new Rect(r.x + 2f * NumW + 8f, r.y, r.width - 2f * NumW - 8f, r.height), row.text);
                        break;
                    case RowKind.Collapsed:
                        Widgets.DrawHighlightIfMouseover(r);
                        GUI.color = UIUtil.ColMuted;
                        Widgets.Label(new Rect(r.x + 2f * NumW + 8f, r.y, r.width - 2f * NumW - 8f, r.height), row.text);
                        if (Widgets.ButtonInvisible(r)) expand = row.expandKey;
                        break;
                    default:
                        if (row.kindCode == "add") Widgets.DrawBoxSolid(r, AddBg);
                        else if (row.kindCode == "del") Widgets.DrawBoxSolid(r, DelBg);
                        GUI.color = UIUtil.ColMuted;
                        Text.Anchor = TextAnchor.MiddleRight;
                        if (row.oldStr != null) Widgets.Label(new Rect(r.x, r.y, NumW - 4f, r.height), row.oldStr);
                        if (row.newStr != null) Widgets.Label(new Rect(r.x + NumW, r.y, NumW - 4f, r.height), row.newStr);
                        Text.Anchor = TextAnchor.MiddleLeft;
                        GUI.color = row.kindCode == "add" ? UIUtil.ColDiffAdd : row.kindCode == "del" ? UIUtil.ColDiffDel : UIUtil.ColResult;
                        Widgets.Label(new Rect(r.x + 2f * NumW, r.y, r.width - 2f * NumW, r.height), row.text);
                        break;
                }
                if (row.tip != null && Mouse.IsOver(r)) TooltipHandler.TipRegion(r, row.tip);
            }
            GUI.color = Color.white;
            Text.Font = oldFont;
            Text.WordWrap = oldWrap;
            Text.Anchor = oldAnchor;
            Widgets.EndScrollView();
            if (expand != null) expanded.Add(expand);
        }
    }
}
