using System.Collections.Generic;
using ColonyCode.Agents;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ColonyCode.World
{
    /// <summary>
    /// Draws "● Name" and the activity line above residents on the current map. The dot shows the
    /// agent state, the name is in the resident's own colour (<see cref="AgentLook"/>). A resident
    /// without an agent gets a grey dot and its name only. Composed strings and their sizes are cached
    /// per pawn and rebuilt only when the inputs change, so a repaint allocates nothing.
    /// </summary>
    public static class AgentOverlay
    {
        public const float WorldOffsetZ = 0.95f;
        private const float Dot = 8f;
        private const float Pad = 3f;
        private const int MaxCacheEntries = 64;
        private static readonly Color Bg = new Color(0f, 0f, 0f, 0.55f);
        private static readonly Color ActColor = new Color(0.85f, 0.85f, 0.85f);

        private class Entry
        {
            // inputs
            public string pawnName;
            public string label;
            public string activity;
            public bool remote;
            public bool hasAgent;
            // outputs
            public string nameText;
            public string actText;
            public Vector2 nameSize;
            public Vector2 actSize;
            public Color nameColor;
        }

        private static readonly Dictionary<Pawn, Entry> cache = new Dictionary<Pawn, Entry>();
        private static string remoteSuffix;

        public static void Draw(AgentRegistry reg)
        {
            if (reg == null || (reg.Agents.Count == 0 && reg.Residents.Count == 0)) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            Map map = Find.CurrentMap;
            if (map == null || WorldRendererUtility.WorldRendered) return;
            if (Find.CameraDriver != null && Find.CameraDriver.CurrentZoom == CameraZoomRange.Furthest) return;
            if (cache.Count > MaxCacheEntries) cache.Clear();

            var oldFont = Text.Font;
            var oldAnchor = Text.Anchor;
            var oldColor = GUI.color;
            try
            {
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleLeft;
                var agents = reg.Agents;
                for (int i = 0; i < agents.Count; i++)
                {
                    var a = agents[i];
                    var p = a.pawn;
                    if (p == null || !p.Spawned || p.Map != map) continue;
                    DrawFor(a, p);
                }
                var residents = reg.Residents;
                for (int i = 0; i < residents.Count; i++)
                {
                    var r = residents[i];
                    var p = r.pawn;
                    if (!r.Valid || !p.Spawned || p.Map != map || reg.GetForPawn(p) != null) continue;
                    DrawFor(null, p);
                }
            }
            finally
            {
                Text.Font = oldFont;
                Text.Anchor = oldAnchor;
                GUI.color = oldColor;
            }
        }

        private static Entry EntryFor(AgentInfo a, Pawn p)
        {
            string pawnName = p.LabelShort;
            string label = a?.label;
            string activity = a?.activity ?? "";
            bool remote = a != null && AgentStateUtility.IsWorkingRemotely(a);
            bool hasAgent = a != null;
            Entry e;
            if (cache.TryGetValue(p, out e) && ReferenceEquals(e.pawnName, pawnName) && ReferenceEquals(e.label, label) &&
                ReferenceEquals(e.activity, activity) && e.remote == remote && e.hasAgent == hasAgent)
                return e;
            if (e == null)
            {
                e = new Entry();
                cache[p] = e;
            }
            e.pawnName = pawnName;
            e.label = label;
            e.activity = activity;
            e.remote = remote;
            e.hasAgent = hasAgent;

            string name = pawnName;
            if (!string.IsNullOrEmpty(label) && label != name) name += " (" + label + ")";
            string act = activity;
            if (remote)
            {
                if (remoteSuffix == null) remoteSuffix = "ColonyCodeW_WorkingRemotely".Translate();
                act = act.Length > 0 ? act + " " + remoteSuffix : remoteSuffix;
            }
            if (act.Length > 48) act = act.Substring(0, 45) + "...";
            e.nameText = name;
            e.actText = act;
            e.nameSize = Text.CalcSize(name);
            e.actSize = act.Length > 0 ? Text.CalcSize(act) : Vector2.zero;
            e.nameColor = AgentLook.NameColor(p);
            return e;
        }

        private static void DrawFor(AgentInfo a, Pawn p)
        {
            var e = EntryFor(a, p);
            Vector2 c = GenMapUI.LabelDrawPosFor(p, WorldOffsetZ);
            bool hasAct = e.actText.Length > 0;
            float lineH = Mathf.Max(e.nameSize.y, Dot);
            float w = Mathf.Max(Dot + Pad + e.nameSize.x, e.actSize.x) + Pad * 2f;
            float h = lineH + (hasAct ? e.actSize.y : 0f) + Pad;
            var box = new Rect(c.x - w / 2f, c.y - h, w, h);
            Widgets.DrawBoxSolid(box, Bg);

            var dot = new Rect(box.x + Pad, box.y + Pad / 2f + (lineH - Dot) / 2f, Dot, Dot);
            Widgets.DrawBoxSolid(dot, AgentStateUtility.StateColor(a != null ? a.state : AgentState.Idle));

            GUI.color = e.nameColor;
            Widgets.Label(new Rect(dot.xMax + Pad, box.y + Pad / 2f, e.nameSize.x + 2f, lineH), e.nameText);
            if (hasAct)
            {
                GUI.color = ActColor;
                Widgets.Label(new Rect(box.x + Pad, box.y + Pad / 2f + lineH, e.actSize.x + 2f, e.actSize.y), e.actText);
            }
            GUI.color = Color.white;
        }
    }
}
