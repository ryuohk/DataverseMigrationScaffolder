using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DataverseMigrationScaffolder.HarnessGen
{
    /// <summary>Raised for any input problem the user must fix (bad path, metadata or template).
    /// Messages name the offending file, table or column.</summary>
    public sealed class GeneratorException : Exception
    {
        public GeneratorException(string message) : base(message) { }
        public GeneratorException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Small helpers that keep the generator's text handling identical to the reference
    /// implementation: anchored regex matching, repr-style quoting in messages, and
    /// case-insensitive ordering.
    /// </summary>
    internal static class Py
    {
        public static readonly StringComparer Ordinal = StringComparer.Ordinal;

        public static string Lower(this string s) { return s == null ? null : s.ToLowerInvariant(); }

        /// <summary>A regex that only matches at the start of the input (Python re.match).</summary>
        public static Regex Start(string pattern, RegexOptions options = RegexOptions.None)
        {
            return new Regex(@"\A(?:" + pattern + ")", options | RegexOptions.CultureInvariant);
        }

        /// <summary>A regex that must match the whole input (Python re.fullmatch).</summary>
        public static Regex Full(string pattern, RegexOptions options = RegexOptions.None)
        {
            return new Regex(@"\A(?:" + pattern + @")\z", options | RegexOptions.CultureInvariant);
        }

        public static Regex Re(string pattern, RegexOptions options = RegexOptions.None)
        {
            return new Regex(pattern, options | RegexOptions.CultureInvariant);
        }

        /// <summary>Python repr() of a string.</summary>
        public static string Repr(string s)
        {
            if (s == null) return "None";
            var quote = s.Contains("'") && !s.Contains("\"") ? '"' : '\'';
            var sb = new StringBuilder();
            sb.Append(quote);
            foreach (var ch in s)
            {
                if (ch == quote || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch == '\n') sb.Append("\\n");
                else if (ch == '\r') sb.Append("\\r");
                else if (ch == '\t') sb.Append("\\t");
                else if (ch < 0x20 || ch == 0x7f) sb.Append("\\x").Append(((int)ch).ToString("x2"));
                else sb.Append(ch);
            }
            sb.Append(quote);
            return sb.ToString();
        }

        /// <summary>Python repr() of a list of strings.</summary>
        public static string Repr(IEnumerable<string> items)
        {
            return "[" + string.Join(", ", items.Select(Repr)) + "]";
        }

        public static string Join(this IEnumerable<string> items, string separator)
        {
            return string.Join(separator, items);
        }

        /// <summary>sorted(items, key=str.lower): stable, by lower-cased ordinal value.</summary>
        public static List<string> SortedLower(IEnumerable<string> items)
        {
            return items.OrderBy(i => i.ToLowerInvariant(), StringComparer.Ordinal).ToList();
        }

        /// <summary>sorted(items): ordinal.</summary>
        public static List<string> Sorted(IEnumerable<string> items)
        {
            return items.OrderBy(i => i, StringComparer.Ordinal).ToList();
        }

        public static string D2(int n) { return n.ToString("00", CultureInfo.InvariantCulture); }

        public static string Str(int n) { return n.ToString(CultureInfo.InvariantCulture); }

        /// <summary>Python str.strip() with no arguments.</summary>
        public static string Strip(this string s) { return s.Trim(); }

        public static HashSet<string> Set(IEnumerable<string> items) { return new HashSet<string>(items, StringComparer.Ordinal); }

        public static bool SetEquals(IEnumerable<string> a, IEnumerable<string> b)
        {
            return new HashSet<string>(a, StringComparer.Ordinal).SetEquals(b);
        }

        /// <summary>Regex.Replace with a callback (never a replacement pattern, so '$' is literal).</summary>
        public static string Sub(Regex re, string input, Func<Match, string> replace)
        {
            return re.Replace(input, m => replace(m));
        }

        public static string Sub(Regex re, string input, string literal)
        {
            return re.Replace(input, m => literal);
        }
    }

    /// <summary>Reference-equality set of nodes (Python's set of id(node)).</summary>
    internal sealed class NodeSet : HashSet<object>
    {
        public NodeSet() : base(ReferenceComparer.Instance) { }
        public NodeSet(IEnumerable<object> items) : base(items, ReferenceComparer.Instance) { }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object obj) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj); }
        }
    }
}
