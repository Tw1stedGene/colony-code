using System.Collections.Generic;
using ColonyCode.Agents;
using UnityEngine;
using Verse;

namespace ColonyCode.UI
{
    /// <summary>Read-only view of an agent's diff log entries: file list on the left, colored hunks on the right.</summary>
    public class Window_Diff : Window
    {
        private string agentId;
        private readonly LogView view = new LogView(diffOnly: true);
        private Vector2 fileScroll;

        public Window_Diff(string agentId)
        {
            this.agentId = agentId;
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            draggable = true;
            resizeable = true;
            preventCameraMotion = false;
            onlyOneOfTypeAllowed = true;
        }

        public override Vector2 InitialSize => new Vector2(980f, 640f);

        public override void DoWindowContents(Rect inRect)
        {
            var agent = UIUtil.Registry?.Get(agentId);

            Text.Font = GameFont.Medium;
            var titleRect = new Rect(inRect.x, inRect.y, inRect.width - 260f, 34f);
            Widgets.Label(titleRect, "ColonyCode_DiffTitle".Translate(UIUtil.Safe(agent != null ? UIUtil.AgentName(agent) : agentId)));
            Text.Font = GameFont.Small;

            var pickRect = new Rect(inRect.xMax - 250f, inRect.y + 2f, 210f, 28f);
            if (Widgets.ButtonText(pickRect, "ColonyCode_PickAgent".Translate()))
            {
                AgentPicker.Open(id => agentId = id);
            }

            float top = inRect.y + 40f;
            var body = new Rect(inRect.x, top, inRect.width, inRect.height - (top - inRect.y));
            if (agent == null)
            {
                Widgets.Label(body, "ColonyCode_AgentGone".Translate());
                return;
            }

            var listRect = new Rect(body.x, body.y, 240f, body.height);
            var diffRect = new Rect(listRect.xMax + 8f, body.y, body.width - listRect.width - 8f, body.height);

            // The view must build its rows before the file list can be shown; draw it first.
            view.Draw(diffRect, agent);
            DrawFileList(listRect, view.Files);
        }

        private void DrawFileList(Rect rect, List<KeyValuePair<string, int>> files)
        {
            Widgets.DrawMenuSection(rect);
            var inner = rect.ContractedBy(4f);
            Text.Font = GameFont.Tiny;
            var header = new Rect(inner.x, inner.y, inner.width, 20f);
            GUI.color = UIUtil.ColMuted;
            Widgets.Label(header, "ColonyCode_DiffFiles".Translate(files.Count));
            GUI.color = Color.white;

            var outRect = new Rect(inner.x, header.yMax + 2f, inner.width, inner.height - header.height - 2f);
            var viewRect = new Rect(0f, 0f, outRect.width - 16f, files.Count * 22f);
            Widgets.BeginScrollView(outRect, ref fileScroll, viewRect);
            var oldWrap = Text.WordWrap;
            Text.WordWrap = false;
            for (int i = 0; i < files.Count; i++)
            {
                var r = new Rect(0f, i * 22f, viewRect.width, 22f);
                if (i % 2 == 1) Widgets.DrawLightHighlight(r);
                Widgets.DrawHighlightIfMouseover(r);
                GUI.color = UIUtil.ColDiffFile;
                string name = files[i].Key;
                Widgets.Label(r.ContractedBy(2f, 0f), UIUtil.Safe(ShortPath(name)));
                GUI.color = Color.white;
                TooltipHandler.TipRegion(r, UIUtil.Safe(name));
                if (Widgets.ButtonInvisible(r)) view.ScrollToRow(files[i].Value);
            }
            Text.WordWrap = oldWrap;
            Widgets.EndScrollView();
            Text.Font = GameFont.Small;
        }

        private static string ShortPath(string path)
        {
            if (path.Length <= 34) return path;
            int slash = path.LastIndexOf('/');
            string file = slash >= 0 ? path.Substring(slash + 1) : path;
            return "…/" + file;
        }
    }

    /// <summary>Float menu listing all agents.</summary>
    public static class AgentPicker
    {
        public static void Open(System.Action<string> onPick)
        {
            var opts = new List<FloatMenuOption>();
            var agents = UIUtil.Registry?.Agents;
            if (agents != null)
            {
                foreach (var a in agents)
                {
                    if (a == null) continue;
                    string id = a.id;
                    opts.Add(new FloatMenuOption(UIUtil.Safe(UIUtil.AgentName(a)) + " (" + UIUtil.RoleLabel(a.role) + ")", () => onPick(id)));
                }
            }
            if (opts.Count == 0) opts.Add(new FloatMenuOption("ColonyCode_NoAgents".Translate(), null));
            Find.WindowStack.Add(new FloatMenu(opts));
        }
    }
}
