using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DataverseMigrationScaffolder.HarnessGen
{
    /// <summary>A JSON object that keeps its keys in insertion order.</summary>
    internal sealed class JObj : List<KeyValuePair<string, object>>
    {
        public JObj() { }

        public object this[string key]
        {
            get { return Get(key); }
            set
            {
                var i = FindIndex(p => p.Key == key);
                if (i >= 0) this[i] = new KeyValuePair<string, object>(key, value);
                else Add(new KeyValuePair<string, object>(key, value));
            }
        }

        public JObj Add(string key, object value)
        {
            this[key] = value;
            return this;
        }

        public bool Has(string key) { return FindIndex(p => p.Key == key) >= 0; }

        public object Get(string key)
        {
            var i = FindIndex(p => p.Key == key);
            return i >= 0 ? this[i].Value : null;
        }
    }

    /// <summary>
    /// JSON for the manifest (read) and the report (write). The writer reproduces Python's
    /// json.dumps(value, indent=2): ASCII-only output with \uXXXX escapes, two-space indent,
    /// "[]" and "{}" for empty containers.
    /// </summary>
    internal static class Json
    {
        // ---- reader ---------------------------------------------------------------------

        public static object Parse(string text)
        {
            var pos = 0;
            var value = ReadValue(text, ref pos);
            SkipSpace(text, ref pos);
            if (pos != text.Length) throw Error(text, pos, "Extra data");
            return value;
        }

        private static FormatException Error(string text, int pos, string what)
        {
            var line = 1;
            var col = 1;
            for (var i = 0; i < pos && i < text.Length; i++)
            {
                if (text[i] == '\n') { line++; col = 1; }
                else col++;
            }
            return new FormatException(string.Format(CultureInfo.InvariantCulture, "{0}: line {1} column {2} (char {3})", what, line, col, pos));
        }

        private static void SkipSpace(string t, ref int p)
        {
            while (p < t.Length && (t[p] == ' ' || t[p] == '\t' || t[p] == '\n' || t[p] == '\r')) p++;
        }

        private static object ReadValue(string t, ref int p)
        {
            SkipSpace(t, ref p);
            if (p >= t.Length) throw Error(t, p, "Expecting value");
            var c = t[p];
            if (c == '{')
            {
                p++;
                var obj = new JObj();
                SkipSpace(t, ref p);
                if (p < t.Length && t[p] == '}') { p++; return obj; }
                while (true)
                {
                    SkipSpace(t, ref p);
                    if (p >= t.Length || t[p] != '"') throw Error(t, p, "Expecting property name enclosed in double quotes");
                    var key = ReadString(t, ref p);
                    SkipSpace(t, ref p);
                    if (p >= t.Length || t[p] != ':') throw Error(t, p, "Expecting ':' delimiter");
                    p++;
                    obj[key] = ReadValue(t, ref p);
                    SkipSpace(t, ref p);
                    if (p < t.Length && t[p] == ',') { p++; continue; }
                    if (p < t.Length && t[p] == '}') { p++; return obj; }
                    throw Error(t, p, "Expecting ',' delimiter");
                }
            }
            if (c == '[')
            {
                p++;
                var list = new List<object>();
                SkipSpace(t, ref p);
                if (p < t.Length && t[p] == ']') { p++; return list; }
                while (true)
                {
                    list.Add(ReadValue(t, ref p));
                    SkipSpace(t, ref p);
                    if (p < t.Length && t[p] == ',') { p++; continue; }
                    if (p < t.Length && t[p] == ']') { p++; return list; }
                    throw Error(t, p, "Expecting ',' delimiter");
                }
            }
            if (c == '"') return ReadString(t, ref p);
            if (string.CompareOrdinal(t, p, "true", 0, 4) == 0) { p += 4; return true; }
            if (string.CompareOrdinal(t, p, "false", 0, 5) == 0) { p += 5; return false; }
            if (string.CompareOrdinal(t, p, "null", 0, 4) == 0) { p += 4; return null; }
            var start = p;
            if (p < t.Length && t[p] == '-') p++;
            while (p < t.Length && char.IsDigit(t[p])) p++;
            var isInt = true;
            if (p < t.Length && t[p] == '.') { isInt = false; p++; while (p < t.Length && char.IsDigit(t[p])) p++; }
            if (p < t.Length && (t[p] == 'e' || t[p] == 'E'))
            {
                isInt = false;
                p++;
                if (p < t.Length && (t[p] == '+' || t[p] == '-')) p++;
                while (p < t.Length && char.IsDigit(t[p])) p++;
            }
            var number = t.Substring(start, p - start);
            if (number.Length == 0 || number == "-") throw Error(t, start, "Expecting value");
            if (isInt) return long.Parse(number, CultureInfo.InvariantCulture);
            return double.Parse(number, CultureInfo.InvariantCulture);
        }

        private static string ReadString(string t, ref int p)
        {
            p++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (p >= t.Length) throw Error(t, p, "Unterminated string starting at");
                var c = t[p++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (p >= t.Length) throw Error(t, p, "Unterminated string starting at");
                var e = t[p++];
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
                        if (p + 4 > t.Length) throw Error(t, p, "Invalid \\uXXXX escape");
                        sb.Append((char)int.Parse(t.Substring(p, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        p += 4;
                        break;
                    default: throw Error(t, p - 1, "Invalid \\escape");
                }
            }
        }

        // ---- writer (json.dumps(indent=2)) ------------------------------------------------

        public static string Dumps(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value, int level)
        {
            if (value == null) { sb.Append("null"); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is string) { WriteString(sb, (string)value); return; }
            if (value is int || value is long) { sb.Append(Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture)); return; }
            var obj = value as JObj;
            if (obj != null)
            {
                if (obj.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                for (var i = 0; i < obj.Count; i++)
                {
                    sb.Append(i == 0 ? "\n" : ",\n").Append(' ', 2 * (level + 1));
                    WriteString(sb, obj[i].Key);
                    sb.Append(": ");
                    Write(sb, obj[i].Value, level + 1);
                }
                sb.Append('\n').Append(' ', 2 * level).Append('}');
                return;
            }
            var list = value as System.Collections.IEnumerable;
            if (list != null)
            {
                var items = list.Cast<object>().ToList();
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (var i = 0; i < items.Count; i++)
                {
                    sb.Append(i == 0 ? "\n" : ",\n").Append(' ', 2 * (level + 1));
                    Write(sb, items[i], level + 1);
                }
                sb.Append('\n').Append(' ', 2 * level).Append(']');
                return;
            }
            throw new InvalidOperationException("Cannot serialize " + value.GetType().Name);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
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
                        if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
