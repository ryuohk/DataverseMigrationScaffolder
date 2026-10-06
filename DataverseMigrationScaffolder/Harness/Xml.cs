using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace DataverseMigrationScaffolder.HarnessGen
{
    /// <summary>
    /// DOM helpers over System.Xml with the semantics the generator relies on: whitespace is
    /// kept exactly, node lists are snapshots, and documents serialize the way SSIS packages
    /// were always written by this generator (attributes in insertion order, "&lt;x/&gt;" for
    /// empty elements, line breaks inside attribute values as character references).
    /// </summary>
    internal static class Xml
    {
        public const string DtsNs = "www.microsoft.com/SqlServer/Dts";
        public const string SsisNs = "www.microsoft.com/SqlServer/SSIS";
        public const string SqlTaskNs = "www.microsoft.com/sqlserver/dts/tasks/sqltask";
        public const string GraphNs = "clr-namespace:Microsoft.SqlServer.IntegrationServices.Designer.Model.Serialization;"
                                      + "assembly=Microsoft.SqlServer.IntegrationServices.Graph";

        // ---- parsing --------------------------------------------------------------------

        public static XmlDocument Parse(byte[] xml)
        {
            using (var stream = new MemoryStream(xml))
            using (var reader = XmlReader.Create(stream, Settings())) return Load(reader);
        }

        public static XmlDocument Parse(string xml)
        {
            using (var text = new StringReader(xml))
            using (var reader = XmlReader.Create(text, Settings())) return Load(reader);
        }

        public static XmlDocument ParseFile(string path)
        {
            return Parse(File.ReadAllBytes(path));
        }

        private static XmlReaderSettings Settings()
        {
            return new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = false };
        }

        private static XmlDocument Load(XmlReader reader)
        {
            var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            doc.Load(reader);
            return doc;
        }

        // ---- serialization --------------------------------------------------------------

        /// <summary>The package bytes: declaration plus document, attribute line breaks and tabs
        /// written as character references so SSIS reads multi-line SQL back unchanged.</summary>
        public static byte[] ToXml(XmlDocument doc)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            foreach (XmlNode child in doc.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.XmlDeclaration || IsTextNode(child)) continue;
                Write(sb, child, true);
            }
            return new UTF8Encoding(false).GetBytes(sb.ToString());
        }

        /// <summary>An element's markup, without a declaration and with raw attribute values.</summary>
        public static string ElementXml(XmlNode element)
        {
            var sb = new StringBuilder();
            Write(sb, element, false);
            return sb.ToString();
        }

        /// <summary>A document's markup as text (used for token searches).</summary>
        public static string DocumentText(XmlDocument doc)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" ?>");
            foreach (XmlNode child in doc.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.XmlDeclaration || IsTextNode(child)) continue;
                Write(sb, child, false);
            }
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, XmlNode node, bool escapeAttributeWhitespace)
        {
            switch (node.NodeType)
            {
                case XmlNodeType.Element:
                    sb.Append('<').Append(node.Name);
                    // Namespace declarations first, then the other attributes in insertion order.
                    var attrs = node.Attributes.Cast<XmlAttribute>().ToList();
                    foreach (var attr in attrs.Where(IsNamespaceDeclaration).Concat(attrs.Where(a => !IsNamespaceDeclaration(a))))
                    {
                        sb.Append(' ').Append(attr.Name).Append("=\"");
                        Escape(sb, attr.Value, escapeAttributeWhitespace);
                        sb.Append('"');
                    }
                    if (node.HasChildNodes)
                    {
                        sb.Append('>');
                        foreach (XmlNode child in node.ChildNodes) Write(sb, child, escapeAttributeWhitespace);
                        sb.Append("</").Append(node.Name).Append('>');
                    }
                    else sb.Append("/>");
                    break;
                case XmlNodeType.Text:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    Escape(sb, ((XmlCharacterData)node).Data, false);
                    break;
                case XmlNodeType.CDATA:
                    sb.Append("<![CDATA[").Append(((XmlCharacterData)node).Data).Append("]]>");
                    break;
                case XmlNodeType.Comment:
                    sb.Append("<!--").Append(((XmlCharacterData)node).Data).Append("-->");
                    break;
                case XmlNodeType.ProcessingInstruction:
                    var pi = (XmlProcessingInstruction)node;
                    sb.Append("<?").Append(pi.Target).Append(' ').Append(pi.Data).Append("?>");
                    break;
                case XmlNodeType.EntityReference:
                    foreach (XmlNode child in node.ChildNodes) Write(sb, child, escapeAttributeWhitespace);
                    break;
            }
        }

        private static bool IsNamespaceDeclaration(XmlAttribute a)
        {
            return a.Name == "xmlns" || a.Name.StartsWith("xmlns:", StringComparison.Ordinal);
        }

        private static void Escape(StringBuilder sb, string data, bool whitespaceRefs)
        {
            foreach (var c in data)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '\r': if (whitespaceRefs) sb.Append("&#xD;"); else sb.Append(c); break;
                    case '\n': if (whitespaceRefs) sb.Append("&#xA;"); else sb.Append(c); break;
                    case '\t': if (whitespaceRefs) sb.Append("&#x9;"); else sb.Append(c); break;
                    default: sb.Append(c); break;
                }
            }
        }

        // ---- navigation -----------------------------------------------------------------

        public static bool IsTextNode(XmlNode n)
        {
            return n.NodeType == XmlNodeType.Text || n.NodeType == XmlNodeType.Whitespace
                   || n.NodeType == XmlNodeType.SignificantWhitespace;
        }

        public static bool IsTextOrCData(XmlNode n) { return IsTextNode(n) || n.NodeType == XmlNodeType.CDATA; }

        public static string Data(XmlNode n) { return ((XmlCharacterData)n).Data; }

        /// <summary>Set a text-like node's data, replacing a whitespace node that would stop being whitespace.</summary>
        public static void SetData(XmlNode n, string value)
        {
            if ((n.NodeType == XmlNodeType.Whitespace || n.NodeType == XmlNodeType.SignificantWhitespace)
                && value.Any(c => !char.IsWhiteSpace(c)))
            {
                n.ParentNode.ReplaceChild(n.OwnerDocument.CreateTextNode(value), n);
                return;
            }
            ((XmlCharacterData)n).Data = value;
        }

        /// <summary>Text and CDATA content of an element, stripped.</summary>
        public static string Text(XmlNode node)
        {
            var sb = new StringBuilder();
            foreach (XmlNode c in node.ChildNodes)
                if (IsTextOrCData(c)) sb.Append(Data(c));
            return sb.ToString().Trim();
        }

        /// <summary>The node and every descendant element, in document order.</summary>
        public static List<XmlElement> Elements(XmlNode node)
        {
            var result = new List<XmlElement>();
            Collect(node, result);
            return result;
        }

        private static void Collect(XmlNode node, List<XmlElement> into)
        {
            var el = node as XmlElement;
            if (el != null) into.Add(el);
            foreach (XmlNode child in node.ChildNodes)
                if (child.NodeType == XmlNodeType.Element) Collect(child, into);
        }

        /// <summary>Descendant elements with this qualified name (snapshot, document order).</summary>
        public static List<XmlElement> ByTag(XmlNode node, string name)
        {
            var root = node as XmlDocument;
            var list = root != null ? root.GetElementsByTagName(name) : ((XmlElement)node).GetElementsByTagName(name);
            return list.Cast<XmlElement>().ToList();
        }

        public static List<XmlElement> ByTagNs(XmlNode node, string ns, string local)
        {
            var root = node as XmlDocument;
            var list = root != null ? root.GetElementsByTagName(local, ns) : ((XmlElement)node).GetElementsByTagName(local, ns);
            return list.Cast<XmlElement>().ToList();
        }

        /// <summary>Element children, optionally with this local name.</summary>
        public static List<XmlElement> Children(XmlNode node, string local = null)
        {
            return node.ChildNodes.OfType<XmlElement>().Where(e => local == null || e.LocalName == local).ToList();
        }

        public static IEnumerable<XmlElement> Ancestors(XmlNode node)
        {
            var n = node.ParentNode;
            while (n != null && n.NodeType == XmlNodeType.Element)
            {
                yield return (XmlElement)n;
                n = n.ParentNode;
            }
        }

        public static bool Inside(XmlNode node, NodeSet identities)
        {
            while (node != null)
            {
                if (identities.Contains(node)) return true;
                node = node.ParentNode;
            }
            return false;
        }

        public static List<KeyValuePair<string, string>> Attributes(XmlElement el)
        {
            return el.Attributes.Cast<XmlAttribute>().Select(a => new KeyValuePair<string, string>(a.Name, a.Value)).ToList();
        }

        /// <summary>A component's own properties by name (later duplicates win, first position kept).</summary>
        public static Props Props(XmlElement component)
        {
            var props = new Props();
            foreach (var p in ByTag(component, "property"))
                if (p.ParentNode != null && p.ParentNode.ParentNode == component) props.Set(p.GetAttribute("name"), p);
            return props;
        }

        /// <summary>Every property element below the node (not only its own), later duplicates winning.</summary>
        public static Props AllProps(XmlElement component)
        {
            var props = new Props();
            foreach (var p in ByTag(component, "property")) props.Set(p.GetAttribute("name"), p);
            return props;
        }

        public static void ClearChildren(XmlNode node)
        {
            while (node.FirstChild != null) node.RemoveChild(node.FirstChild);
        }

        public static void SetText(XmlNode node, string value)
        {
            ClearChildren(node);
            node.AppendChild(node.OwnerDocument.CreateTextNode(value));
        }

        /// <summary>A DTS-namespace element with DTS-namespace attributes, in the given order.</summary>
        public static XmlElement Dts(XmlDocument doc, string name, params string[] attrs)
        {
            var node = doc.CreateElement("DTS", name, DtsNs);
            for (var i = 0; i + 1 < attrs.Length; i += 2)
            {
                var a = doc.CreateAttribute("DTS", attrs[i], DtsNs);
                a.Value = attrs[i + 1];
                node.Attributes.Append(a);
            }
            return node;
        }

        /// <summary>Set an attribute by namespace and local name, keeping its position if present.</summary>
        public static void SetNs(XmlElement el, string prefix, string local, string ns, string value)
        {
            var existing = el.GetAttributeNode(local, ns);
            if (existing != null) { existing.Value = value; return; }
            var a = el.OwnerDocument.CreateAttribute(prefix, local, ns);
            a.Value = value;
            el.Attributes.Append(a);
        }

        public static string GetNs(XmlElement el, string ns, string local)
        {
            var a = el.GetAttributeNode(local, ns);
            return a == null ? "" : a.Value;
        }
    }

    /// <summary>Property elements by name; keys keep first-insertion order, values the last element.</summary>
    internal sealed class Props
    {
        private readonly List<string> _keys = new List<string>();
        private readonly Dictionary<string, XmlElement> _values = new Dictionary<string, XmlElement>(StringComparer.Ordinal);

        public void Set(string name, XmlElement prop)
        {
            if (!_values.ContainsKey(name)) _keys.Add(name);
            _values[name] = prop;
        }

        public bool Has(string name) { return _values.ContainsKey(name); }
        public XmlElement this[string name] { get { XmlElement e; return _values.TryGetValue(name, out e) ? e : null; } }
        public string Text(string name) { var e = this[name]; return e == null ? null : Xml.Text(e); }
        public string TextOr(string name, string fallback) { var e = this[name]; return e == null ? fallback : Xml.Text(e); }
        public int Count { get { return _keys.Count; } }
        public IEnumerable<KeyValuePair<string, string>> Items()
        {
            return _keys.Select(k => new KeyValuePair<string, string>(k, Xml.Text(_values[k])));
        }
    }
}
