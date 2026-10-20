using System;
using UnityEngine;
using Verse;

namespace ColonyCode.UI
{
    /// <summary>Small dialog with a multi-line text box, an optional session picker and Send / Cancel.</summary>
    public class Dialog_TextInput : Window
    {
        private const string FieldName = "ColonyCode_DialogTextInput";

        private readonly string title;
        private readonly string prompt;
        private readonly string sendLabel;
        private readonly bool pickSession;
        private readonly Action<string, string> onSubmit;
        private string text = "";
        private string session;
        private bool focused;

        /// <param name="onSubmit">(text, sessionId or null)</param>
        public Dialog_TextInput(string title, string prompt, string sendLabel, bool pickSession, string session, Action<string, string> onSubmit)
        {
            this.title = title;
            this.prompt = prompt;
            this.sendLabel = sendLabel;
            this.pickSession = pickSession;
            this.session = session;
            this.onSubmit = onSubmit;
            doCloseX = true;
            closeOnClickedOutside = false;
            closeOnAccept = false;
            absorbInputAroundWindow = true;
            forcePause = false;
            preventCameraMotion = false;
        }

        public override Vector2 InitialSize => new Vector2(520f, 300f);

        public override void OnAcceptKeyPressed()
        {
            // Enter inserts a newline in the text area; Ctrl/Cmd+Enter sends.
            if (Event.current != null && (Event.current.control || Event.current.command)) Send();
        }

        private void Send()
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            onSubmit?.Invoke(text.Trim(), session);
            Close();
        }

        public override void DoWindowContents(Rect inRect)
        {
            float y = inRect.y;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, y, inRect.width - 30f, 32f), title);
            Text.Font = GameFont.Small;
            y += 34f;
            if (!string.IsNullOrEmpty(prompt))
            {
                GUI.color = UIUtil.ColMuted;
                float ph = Text.CalcHeight(prompt, inRect.width);
                Widgets.Label(new Rect(inRect.x, y, inRect.width, ph), prompt);
                GUI.color = Color.white;
                y += ph + 4f;
            }
            if (pickSession)
            {
                SessionPicker.Button(new Rect(inRect.x, y, 260f, 26f), session, id => session = id);
                y += 30f;
            }
            var area = new Rect(inRect.x, y, inRect.width, inRect.yMax - y - 40f);
            GUI.SetNextControlName(FieldName);
            text = Widgets.TextArea(area, text);
            if (text.Length > 2000) text = text.Substring(0, 2000);
            if (!focused)
            {
                GUI.FocusControl(FieldName);
                focused = true;
            }
            bool can = !string.IsNullOrWhiteSpace(text);
            if (Widgets.ButtonText(new Rect(inRect.xMax - 130f, inRect.yMax - 32f, 130f, 32f), sendLabel, true, true, can) && can) Send();
            if (Widgets.ButtonText(new Rect(inRect.xMax - 270f, inRect.yMax - 32f, 130f, 32f), "CancelButton".Translate())) Close();
        }
    }
}
