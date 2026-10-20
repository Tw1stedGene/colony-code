using System;
using System.Collections.Generic;
using ColonyCode.Agents;
using UnityEngine;
using Verse;

namespace ColonyCode.UI
{
    /// <summary>Dropdown button to pick a Claude Code session ("any session" = null).</summary>
    public static class SessionPicker
    {
        private static List<SessionInfo> cached = new List<SessionInfo>();
        private static int cachedFrame = -1;
        private static AgentRegistry cachedFor;
        private static readonly Comparison<SessionInfo> Order =
            (a, b) => a.connected != b.connected ? (a.connected ? -1 : 1) : a.seq.CompareTo(b.seq);

        /// <summary>
        /// Sessions offered by the picker: connected first, then recently ended ones. Computed once per
        /// frame (asked several times per OnGUI event); callers must not modify the returned list.
        /// </summary>
        public static List<SessionInfo> Choices()
        {
            var reg = UIUtil.Registry;
            int frame = Time.frameCount;
            if (frame == cachedFrame && ReferenceEquals(reg, cachedFor)) return cached;
            cachedFrame = frame;
            cachedFor = reg;
            cached = reg != null ? reg.KnownSessions() : new List<SessionInfo>();
            cached.Sort(Order);
            return cached;
        }

        /// <summary>True when the picker is worth showing (more than one session known).</summary>
        public static bool Worthwhile()
        {
            return Choices().Count > 1;
        }

        /// <summary>Label for a picked session id (null = "any session").</summary>
        public static string Label(string sessionId, bool allowAny = true)
        {
            if (string.IsNullOrEmpty(sessionId))
                return allowAny ? "ColonyCode_SessionAny".Translate().ToString() : "ColonyCode_SessionNone".Translate().ToString();
            return UIUtil.SessionLabel(sessionId);
        }

        /// <summary>Draws the button; clicking opens a float menu. A picked session that disappeared falls back to "any".</summary>
        public static void Button(Rect r, string current, Action<string> onPick, bool allowAny = true)
        {
            string shown = "ColonyCode_SessionPick".Translate(Label(current, allowAny));
            if (Widgets.ButtonText(r, shown))
            {
                var opts = new List<FloatMenuOption>();
                if (allowAny) opts.Add(new FloatMenuOption("ColonyCode_SessionAny".Translate(), () => onPick(null)));
                foreach (var s in Choices())
                {
                    string id = s.id;
                    string label = UIUtil.SessionLabel(id) + (s.connected ? "" : " " + "ColonyCode_SessionEndedTag".Translate());
                    opts.Add(new FloatMenuOption(label, () => onPick(id)));
                }
                if (opts.Count == 0) opts.Add(new FloatMenuOption("ColonyCode_NoSessions".Translate(), null));
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            TooltipHandler.TipRegion(r, "ColonyCode_SessionPickTip".Translate());
        }

        /// <summary>Drops a picked session that is no longer known.</summary>
        public static string Validate(string current)
        {
            if (string.IsNullOrEmpty(current)) return null;
            foreach (var s in Choices()) if (s.id == current) return current;
            return null;
        }
    }
}
