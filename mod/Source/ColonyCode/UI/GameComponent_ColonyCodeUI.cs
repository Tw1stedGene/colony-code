using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyCode.UI
{
    [DefOf]
    public static class ColonyCodeUIDefOf
    {
        public static KeyBindingDef ColonyCode_OpenConsole;
        public static MainButtonDef ColonyCode_Agents;

        static ColonyCodeUIDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(ColonyCodeUIDefOf));
        }
    }

    /// <summary>
    /// UI glue that needs a game: the console hotkey, and cleanup of decision letters restored from a
    /// save (decisions are not saved, so those letters are stale). RimWorld adds it to every game.
    /// </summary>
    public class GameComponent_ColonyCodeUI : GameComponent
    {
        private bool cleanedUp;

        public GameComponent_ColonyCodeUI() { }

        public GameComponent_ColonyCodeUI(Game game) { }

        public override void StartedNewGame()
        {
            LetterHub.ResetForGame(Current.Game);
            cleanedUp = true;
        }

        public override void LoadedGame()
        {
            LetterHub.ResetForGame(Current.Game);
            LetterHub.RemoveStaleLetters();
            cleanedUp = true;
        }

        public override void GameComponentUpdate()
        {
            if (!cleanedUp)
            {
                LetterHub.RemoveStaleLetters();
                cleanedUp = true;
            }
        }

        public override void GameComponentOnGUI()
        {
            var key = ColonyCodeUIDefOf.ColonyCode_OpenConsole;
            if (key == null || !key.KeyDownEvent) return;
            var consoleOpen = Find.WindowStack.WindowOfType<Window_Console>() != null;
            // Do not steal the key while the player types into some other text field.
            if (!consoleOpen && GUIUtility.keyboardControl != 0) return;
            UIEntry.ToggleConsole();
            Event.current.Use();
        }
    }
}
