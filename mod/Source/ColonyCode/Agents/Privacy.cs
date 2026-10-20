using System;
using System.Text.RegularExpressions;

namespace ColonyCode.Agents
{
    /// <summary>
    /// Keeps the player's account name out of the game: home-folder paths become "~", and labels
    /// that are (or carry) the account name lose it. The plugin already does this; this is the
    /// game-side guard for older plugins. The game runs on the same machine as Claude Code, so the
    /// local account name and home folder are the ones to hide.
    /// </summary>
    public static class Privacy
    {
        private static readonly string User = SafeUser();
        private static readonly string Home = SafeHome();
        private static readonly Regex HomePath =
            new Regex(@"(/Users/[^/\\\s""]+|/home/[^/\\\s""]+|[A-Za-z]:[\\/]Users[\\/][^\\/\s""]+)", RegexOptions.IgnoreCase);

        /// <summary>The text with every home-folder path shown as "~". Null stays null.</summary>
        public static string Path(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string t = text;
            if (!string.IsNullOrEmpty(Home) && Home.Length > 1) t = t.Replace(Home, "~");
            return HomePath.Replace(t, "~");
        }

        /// <summary>
        /// A label with paths made private and the account name removed: a label that is only the
        /// account name (or "~") becomes null, "Claude (name)" becomes "Claude".
        /// </summary>
        public static string Label(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string t = Path(text).Trim();
            if (!string.IsNullOrEmpty(User))
            {
                if (string.Equals(t, User, StringComparison.OrdinalIgnoreCase)) return null;
                t = Regex.Replace(t, @"\s*\(\s*" + Regex.Escape(User) + @"\s*\)", "", RegexOptions.IgnoreCase).Trim();
            }
            return t == "" || t == "~" ? null : t;
        }

        private static string SafeUser()
        {
            try { return Environment.UserName ?? ""; } catch { return ""; }
        }

        private static string SafeHome()
        {
            try { return (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) ?? "").TrimEnd('/', '\\'); }
            catch { return ""; }
        }
    }
}
