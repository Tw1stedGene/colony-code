using System.Collections.Generic;
using ColonyCode.Agents;
using UnityEngine;
using Verse;

namespace ColonyCode.UI
{
    /// <summary>Goal history: every goal that reached done or failed, newest first. Saved with the game.</summary>
    public class Window_Journal : Window
    {
        private Vector2 scroll;
        private class Item
        {
            public JournalEntry entry;
            public string body, meta;
            public float bodyH, metaH, height;
        }

        private readonly List<Item> items = new List<Item>();
        private float total;
        private int cachedCount = -1;
        private JournalEntry cachedLast, cachedFirst;
        private float cachedW = -1f;

        public Window_Journal()
        {
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            draggable = true;
            resizeable = true;
            preventCameraMotion = false;
            onlyOneOfTypeAllowed = true;
            forcePause = false;
        }

        public override Vector2 InitialSize => new Vector2(760f, 620f);

        private static string Body(JournalEntry e)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(UIUtil.Safe(e.text ?? ""));
            if (!string.IsNullOrEmpty(e.summary))
            {
                string s = e.summary.Length > 1200 ? e.summary.Substring(0, 1200) + "…" : e.summary;
                sb.Append("\n<color=#aaaaaa>").Append(UIUtil.Safe(s)).Append("</color>");
            }
            return sb.ToString();
        }

        private static string Meta(JournalEntry e)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(e.sessionLabel)) parts.Add("[" + UIUtil.Safe(e.sessionLabel) + "]");
            string start = UIUtil.GameDate(e.startTicksAbs), end = UIUtil.GameDate(e.endTicksAbs);
            if (start.Length > 0 && end.Length > 0) parts.Add("ColonyCode_JournalDates".Translate(start, end));
            else if (end.Length > 0) parts.Add(end);
            if (e.agents != null && e.agents.Count > 0) parts.Add("ColonyCode_JournalAgents".Translate(UIUtil.Safe(string.Join(", ", e.agents))));
            return string.Join("  ·  ", parts);
        }

        private const float StatusW = 70f;

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 40f, 34f), "ColonyCode_JournalTitle".Translate());
            Text.Font = GameFont.Small;
            var outRect = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f);
            var journal = UIUtil.Registry?.Journal;
            if (journal == null || journal.Count == 0)
            {
                GUI.color = UIUtil.ColMuted;
                Widgets.Label(outRect, "ColonyCode_JournalEmpty".Translate());
                GUI.color = Color.white;
                return;
            }

            float w = outRect.width - 16f;
            float textW = w - StatusW - 12f;
            Rebuild(journal, textW);

            var view = new Rect(0f, 0f, w, total);
            Widgets.BeginScrollView(outRect, ref scroll, view);
            float y = 0f;
            foreach (var it in items)
            {
                var r = new Rect(0f, y, w, it.height);
                if (r.yMax >= scroll.y && r.y <= scroll.y + outRect.height) DrawEntry(r, it, textW);
                y += it.height + 6f;
            }
            Widgets.EndScrollView();
        }

        /// <summary>Per-entry strings and heights, newest first; rebuilt only when the journal or the width changes.</summary>
        private void Rebuild(IReadOnlyList<JournalEntry> journal, float textW)
        {
            var last = journal[journal.Count - 1];
            if (journal.Count == cachedCount && ReferenceEquals(last, cachedLast) && ReferenceEquals(journal[0], cachedFirst)
                && Mathf.Abs(textW - cachedW) < 0.5f)
                return;
            cachedCount = journal.Count;
            cachedLast = last;
            cachedFirst = journal[0];
            cachedW = textW;
            items.Clear();
            total = 0f;
            var oldFont = Text.Font;
            for (int i = journal.Count - 1; i >= 0; i--)
            {
                var e = journal[i];
                var it = new Item { entry = e, body = Body(e), meta = Meta(e) };
                Text.Font = GameFont.Small;
                it.bodyH = Text.CalcHeight(it.body, textW);
                Text.Font = GameFont.Tiny;
                it.metaH = Text.CalcHeight(it.meta, textW);
                it.height = it.bodyH + it.metaH + 14f;
                items.Add(it);
                total += it.height + 6f;
            }
            Text.Font = oldFont;
        }

        private static void DrawEntry(Rect r, Item it, float textW)
        {
            Widgets.DrawMenuSection(r);
            bool done = it.entry.status == "done";
            GUI.color = done ? UIUtil.ColGood : UIUtil.ColBad;
            Widgets.Label(new Rect(r.x + 6f, r.y + 6f, StatusW, 22f), done ? DoneLabel : FailedLabel);
            GUI.color = Color.white;
            float x = r.x + StatusW + 6f;
            float y = r.y + 6f;
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(x, y, textW, it.bodyH), it.body);
            y += it.bodyH + 2f;
            Text.Font = GameFont.Tiny;
            GUI.color = UIUtil.ColMuted;
            Widgets.Label(new Rect(x, y, textW, it.metaH), it.meta);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private static string DoneLabel => doneLabel ?? (doneLabel = "ColonyCode_GoalStatus_done".Translate());
        private static string FailedLabel => failedLabel ?? (failedLabel = "ColonyCode_GoalStatus_failed".Translate());
        private static string doneLabel, failedLabel;
    }
}
