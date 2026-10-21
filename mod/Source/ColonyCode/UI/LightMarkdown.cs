using System.Collections.Generic;
using System.Text;

namespace ColonyCode.UI
{
    /// <summary>
    /// Light markdown for Claude's answers, as Unity rich text that is safe to draw: everything the agent
    /// wrote goes through <see cref="UIUtil.Safe"/> (its own tags are broken up), and only the tags made
    /// here are real. RimWorld's fonts have no bold face (vanilla never uses &lt;b&gt;), so bold is a
    /// highlight colour instead:
    /// <list type="bullet">
    /// <item>**bold** and __bold__ lose their markers and get the highlight colour; *italic* / _italic_ become &lt;i&gt;;</item>
    /// <item>`inline code` loses its backticks and is tinted; ``` fences are dropped and the lines between are tinted;</item>
    /// <item># headings lose their hashes and are highlighted; "- " bullets stay, "* " / "+ " bullets become "- ".</item>
    /// </list>
    /// Called when log rows or a result are (re)built, never per frame.
    /// </summary>
    public static class LightMarkdown
    {
        private const string BoldOpen = "<color=#FFE6A6>";
        private const string CodeOpen = "<color=#A8D8FF>";
        private const string Close = "</color>";
        /// <summary>A single line longer than this is cut (Unity draws at most ~16k characters per label).</summary>
        public const int LineMax = 3000;

        /// <summary>The text split into display lines (rich text), fenced code blocks tinted.</summary>
        public static List<string> Lines(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;
            bool inFence = false;
            foreach (var raw0 in text.Replace("\r", "").Split('\n'))
            {
                string raw = raw0.Length > LineMax ? raw0.Substring(0, LineMax) + "…" : raw0;
                string trimmed = raw.TrimStart();
                if (trimmed.StartsWith("```"))
                {
                    inFence = !inFence;
                    continue;
                }
                if (inFence)
                {
                    result.Add(raw.Length == 0 ? "" : CodeOpen + UIUtil.Safe(raw) + Close);
                    continue;
                }
                result.Add(Line(raw));
            }
            // drop trailing blank lines
            while (result.Count > 0 && result[result.Count - 1].Length == 0) result.RemoveAt(result.Count - 1);
            return result;
        }

        /// <summary>The whole text as one rich-text string (lines joined with '\n').</summary>
        public static string Block(string text)
        {
            return string.Join("\n", Lines(text).ToArray());
        }

        /// <summary>One line of markdown as safe rich text.</summary>
        public static string Line(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            int indent = 0;
            while (indent < raw.Length && raw[indent] == ' ') indent++;
            string body = raw.Substring(indent);
            var sb = new StringBuilder(raw.Length + 32);
            sb.Append(raw, 0, indent);

            // # Heading
            int hashes = 0;
            while (hashes < body.Length && hashes < 6 && body[hashes] == '#') hashes++;
            if (hashes > 0 && hashes < body.Length && body[hashes] == ' ')
            {
                sb.Append(BoldOpen);
                Inline(sb, body.Substring(hashes + 1).Trim(), true);
                sb.Append(Close);
                return sb.ToString();
            }
            // "* item" / "+ item" bullets read as "- item"
            if (body.Length >= 2 && (body[0] == '*' || body[0] == '+') && body[1] == ' ')
            {
                sb.Append("- ");
                body = body.Substring(2);
            }
            Inline(sb, body, false);
            return sb.ToString();
        }

        private static void Inline(StringBuilder sb, string s, bool inBold)
        {
            int literalStart = 0;
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '`')
                {
                    int end = s.IndexOf('`', i + 1);
                    if (end > i + 1)
                    {
                        Literal(sb, s, literalStart, i);
                        sb.Append(CodeOpen).Append(UIUtil.Safe(s.Substring(i + 1, end - i - 1))).Append(Close);
                        i = end + 1;
                        literalStart = i;
                        continue;
                    }
                }
                else if ((c == '*' || c == '_') && i + 1 < s.Length && s[i + 1] == c)
                {
                    int end = FindClosing(s, i + 2, c, true);
                    if (end > i + 2 && OpensAt(s, i, 2) && ClosesAt(s, end, 2))
                    {
                        Literal(sb, s, literalStart, i);
                        string inner = s.Substring(i + 2, end - i - 2);
                        if (inBold) Inline(sb, inner, true);
                        else
                        {
                            sb.Append(BoldOpen);
                            Inline(sb, inner, true);
                            sb.Append(Close);
                        }
                        i = end + 2;
                        literalStart = i;
                        continue;
                    }
                }
                else if (c == '*' || c == '_')
                {
                    int end = FindClosing(s, i + 1, c, false);
                    if (end > i + 1 && OpensAt(s, i, 1) && ClosesAt(s, end, 1))
                    {
                        Literal(sb, s, literalStart, i);
                        sb.Append("<i>");
                        Inline(sb, s.Substring(i + 1, end - i - 1), inBold);
                        sb.Append("</i>");
                        i = end + 1;
                        literalStart = i;
                        continue;
                    }
                }
                i++;
            }
            Literal(sb, s, literalStart, s.Length);
        }

        private static void Literal(StringBuilder sb, string s, int from, int to)
        {
            if (to > from) sb.Append(UIUtil.Safe(s.Substring(from, to - from)));
        }

        /// <summary>Index of the closing marker (doubled or single), or -1.</summary>
        private static int FindClosing(string s, int from, char c, bool doubled)
        {
            for (int j = from; j < s.Length; j++)
            {
                if (s[j] != c) continue;
                if (doubled)
                {
                    if (j + 1 < s.Length && s[j + 1] == c) return j;
                }
                else
                {
                    // a single marker: not part of a doubled one
                    bool prevSame = j > 0 && s[j - 1] == c;
                    bool nextSame = j + 1 < s.Length && s[j + 1] == c;
                    if (!prevSame && !nextSame) return j;
                }
            }
            return -1;
        }

        /// <summary>A marker of <paramref name="len"/> chars at i opens: start or non-word before it, non-space after it.</summary>
        private static bool OpensAt(string s, int i, int len)
        {
            if (i > 0 && IsWord(s[i - 1])) return false;
            int after = i + len;
            return after < s.Length && !char.IsWhiteSpace(s[after]);
        }

        /// <summary>A marker at i closes: non-space before it, end or non-word after it.</summary>
        private static bool ClosesAt(string s, int i, int len)
        {
            if (i == 0 || char.IsWhiteSpace(s[i - 1])) return false;
            int after = i + len;
            return after >= s.Length || !IsWord(s[after]);
        }

        private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
