// Pure .NET. No Verse / Unity references: this file is unit-tested under plain mono.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ColonyCode.Http
{
    /// <summary>
    /// Minimal JSON reader/writer.
    /// Parse produces: Dictionary&lt;string,object&gt;, List&lt;object&gt;, string, double, bool, null.
    /// Serialize accepts those plus any IDictionary with string keys, IEnumerable, integer/float
    /// primitives, enums (as their name) and DateTime (as epoch ms).
    /// </summary>
    public static class Json
    {
        public class JsonException : Exception
        {
            public JsonException(string msg) : base(msg) { }
        }

        /// <summary>Max nesting depth accepted by Parse.</summary>
        public const int MaxDepth = 64;

        // ------------------------------------------------------------------ parse

        public static object Parse(string text)
        {
            if (text == null) throw new JsonException("null input");
            var p = new Parser(text);
            p.SkipWs();
            object v = p.ReadValue(0);
            p.SkipWs();
            if (!p.AtEnd) throw new JsonException("trailing characters at " + p.Pos);
            return v;
        }

        public static bool TryParse(string text, out object value)
        {
            try { value = Parse(text); return true; }
            catch (JsonException) { value = null; return false; }
        }

        private sealed class Parser
        {
            private readonly string s;
            public int Pos;
            public Parser(string s) { this.s = s; }
            public bool AtEnd => Pos >= s.Length;

            public void SkipWs()
            {
                while (Pos < s.Length)
                {
                    char c = s[Pos];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == (char)0xFEFF) Pos++;
                    else break;
                }
            }

            private JsonException Err(string m) => new JsonException(m + " at " + Pos);

            public object ReadValue(int depth)
            {
                if (depth > MaxDepth) throw Err("too deep");
                if (AtEnd) throw Err("unexpected end");
                char c = s[Pos];
                switch (c)
                {
                    case '{': return ReadObject(depth);
                    case '[': return ReadArray(depth);
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw Err("unexpected char '" + c + "'");
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(s, Pos, word, 0, word.Length) != 0) throw Err("expected " + word);
                Pos += word.Length;
            }

            private Dictionary<string, object> ReadObject(int depth)
            {
                var d = new Dictionary<string, object>();
                Pos++; // {
                SkipWs();
                if (!AtEnd && s[Pos] == '}') { Pos++; return d; }
                while (true)
                {
                    SkipWs();
                    if (AtEnd || s[Pos] != '"') throw Err("expected key");
                    string key = ReadString();
                    SkipWs();
                    if (AtEnd || s[Pos] != ':') throw Err("expected ':'");
                    Pos++;
                    SkipWs();
                    d[key] = ReadValue(depth + 1);
                    SkipWs();
                    if (AtEnd) throw Err("unexpected end in object");
                    char c = s[Pos++];
                    if (c == ',') continue;
                    if (c == '}') return d;
                    throw Err("expected ',' or '}'");
                }
            }

            private List<object> ReadArray(int depth)
            {
                var l = new List<object>();
                Pos++; // [
                SkipWs();
                if (!AtEnd && s[Pos] == ']') { Pos++; return l; }
                while (true)
                {
                    SkipWs();
                    l.Add(ReadValue(depth + 1));
                    SkipWs();
                    if (AtEnd) throw Err("unexpected end in array");
                    char c = s[Pos++];
                    if (c == ',') continue;
                    if (c == ']') return l;
                    throw Err("expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                Pos++; // opening quote
                var sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw Err("unterminated string");
                    char c = s[Pos++];
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        if (AtEnd) throw Err("bad escape");
                        char e = s[Pos++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                if (Pos + 4 > s.Length) throw Err("bad \\u escape");
                                int code;
                                if (!int.TryParse(s.Substring(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                    throw Err("bad \\u escape");
                                sb.Append((char)code);
                                Pos += 4;
                                break;
                            default: throw Err("bad escape");
                        }
                    }
                    else if (c < 0x20) throw Err("control char in string");
                    else sb.Append(c);
                }
            }

            private double ReadNumber()
            {
                int start = Pos;
                if (s[Pos] == '-') Pos++;
                while (Pos < s.Length)
                {
                    char c = s[Pos];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') Pos++;
                    else break;
                }
                double d;
                if (!double.TryParse(s.Substring(start, Pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    throw Err("bad number");
                return d;
            }
        }

        // -------------------------------------------------------------- serialize

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object v, int depth)
        {
            if (depth > MaxDepth) throw new JsonException("too deep");
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string str: WriteString(sb, str); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case char ch: WriteString(sb, ch.ToString()); return;
                case double d: WriteDouble(sb, d); return;
                case float f: WriteDouble(sb, f); return;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); return;
                case int _: case long _: case short _: case byte _: case sbyte _:
                case uint _: case ulong _: case ushort _:
                    sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
                case Enum e: WriteString(sb, e.ToString()); return;
                case DateTime dt:
                    sb.Append(((long)(dt.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds)
                        .ToString(CultureInfo.InvariantCulture));
                    return;
                case IDictionary dict:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (DictionaryEntry kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                        sb.Append(':');
                        Write(sb, kv.Value, depth + 1);
                    }
                    sb.Append('}');
                    return;
                }
                case IEnumerable list:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (object item in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, item, depth + 1);
                    }
                    sb.Append(']');
                    return;
                }
                default:
                    WriteString(sb, v.ToString());
                    return;
            }
        }

        private static void WriteDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            if (d == Math.Floor(d) && Math.Abs(d) < 9e15)
                sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
            else
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------ typed accessors
        // Convenience helpers for reading parsed objects. All return the fallback when the key is
        // missing or has the wrong type; none of them throw.

        public static string GetString(Dictionary<string, object> d, string key, string fallback = null)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v is string s ? s : fallback;
        }

        public static double GetDouble(Dictionary<string, object> d, string key, double fallback = 0)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v is double x ? x : fallback;
        }

        public static long GetLong(Dictionary<string, object> d, string key, long fallback = 0)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v is double x ? (long)x : fallback;
        }

        public static bool GetBool(Dictionary<string, object> d, string key, bool fallback = false)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v is bool b ? b : fallback;
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        public static List<object> GetList(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as List<object> : null;
        }

        /// <summary>Current time as integer epoch milliseconds (protocol timestamp format).</summary>
        public static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }
    }
}
