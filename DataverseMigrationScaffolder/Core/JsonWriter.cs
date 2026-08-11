using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DataverseMigrationScaffolder.Core
{
    /// <summary>
    /// Minimal indented JSON writer (objects, arrays, strings, numbers, booleans, null),
    /// so the tool stays a single DLL with no serializer dependency to deploy.
    ///
    /// Usage: StartObject() ... Prop("name", value) ... EndObject(), then ToString().
    /// </summary>
    public class JsonWriter
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private readonly List<bool> _scopeIsEmpty = new List<bool> { true };
        private int _depth;

        public JsonWriter StartObject(string name = null)
        {
            Prefix(name);
            _sb.Append("{");
            OpenScope();
            return this;
        }

        public JsonWriter EndObject()
        {
            CloseScope("}");
            return this;
        }

        public JsonWriter StartArray(string name = null)
        {
            Prefix(name);
            _sb.Append("[");
            OpenScope();
            return this;
        }

        public JsonWriter EndArray()
        {
            CloseScope("]");
            return this;
        }

        public JsonWriter Prop(string name, string value)
        {
            Prefix(name);
            _sb.Append(value == null ? "null" : "\"" + Escape(value) + "\"");
            return this;
        }

        public JsonWriter Prop(string name, int value)
        {
            Prefix(name);
            _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public JsonWriter Prop(string name, bool value)
        {
            Prefix(name);
            _sb.Append(value ? "true" : "false");
            return this;
        }

        /// <summary>Writes a bare string element (for use inside an array).</summary>
        public JsonWriter Value(string value)
        {
            return Prop(null, value);
        }

        public JsonWriter StringArray(string name, IEnumerable<string> values)
        {
            StartArray(name);
            if (values != null)
            {
                foreach (var v in values) Value(v);
            }
            EndArray();
            return this;
        }

        public override string ToString()
        {
            return _sb.ToString();
        }

        // ---------------------------------------------------------------- internals

        private void Prefix(string name)
        {
            var last = _scopeIsEmpty.Count - 1;
            if (!_scopeIsEmpty[last]) _sb.Append(",");
            _scopeIsEmpty[last] = false;

            if (_depth > 0 || _sb.Length > 0)
            {
                _sb.AppendLine();
                _sb.Append(new string(' ', _depth * 2));
            }

            if (name != null) _sb.Append("\"" + Escape(name) + "\": ");
        }

        private void OpenScope()
        {
            _depth++;
            _scopeIsEmpty.Add(true);
        }

        private void CloseScope(string closer)
        {
            var last = _scopeIsEmpty.Count - 1;
            var hadContent = !_scopeIsEmpty[last];
            _scopeIsEmpty.RemoveAt(last);
            _depth--;

            if (hadContent)
            {
                _sb.AppendLine();
                _sb.Append(new string(' ', _depth * 2));
            }
            _sb.Append(closer);
        }

        private static string Escape(string value)
        {
            var sb = new StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
