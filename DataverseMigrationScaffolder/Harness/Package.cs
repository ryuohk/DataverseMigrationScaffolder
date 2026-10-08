using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace DataverseMigrationScaffolder.HarnessGen
{
    internal sealed class RenderedPackage
    {
        public RenderedPackage(string packageName, string dtsid, string versionGuid, byte[] xml, List<string> warnings = null)
        {
            PackageName = packageName; Dtsid = dtsid; VersionGuid = versionGuid; Xml = xml;
            Warnings = warnings ?? new List<string>();
        }

        public string PackageName { get; }
        public string Dtsid { get; }
        public string VersionGuid { get; }
        public byte[] Xml { get; }
        public List<string> Warnings { get; }
    }

    /// <summary>
    /// Single-pass, identifier-aware replacement of reference-table tokens. A token only
    /// matches when it is not glued to letters/digits on the left or to letters, digits or
    /// underscores on the right, so 'new_language' does not match inside 'new_languageid'. A
    /// leading underscore is allowed so names such as 'Load_new_Language' are retargeted.
    /// </summary>
    internal sealed class Substituter
    {
        private readonly Dictionary<string, string> _mapping;
        private readonly Regex _pattern;

        public Substituter(IEnumerable<KeyValuePair<string, string>> mapping)
        {
            _mapping = new Dictionary<string, string>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var kv in mapping)
            {
                if (string.IsNullOrEmpty(kv.Key) || kv.Key == kv.Value) continue;
                if (!_mapping.ContainsKey(kv.Key)) order.Add(kv.Key);
                _mapping[kv.Key] = kv.Value;
            }
            _pattern = order.Count > 0 ? Package.TokenPattern(order) : null;
        }

        public string Apply(string text)
        {
            if (_pattern == null || string.IsNullOrEmpty(text)) return text;
            return _pattern.Replace(text, m => _mapping[m.Value]);
        }
    }

    /// <summary>An insertion-ordered string map with Python dict assignment semantics.</summary>
    internal sealed class OrderedMap : List<KeyValuePair<string, string>>
    {
        public void Set(string key, string value)
        {
            var i = FindIndex(p => p.Key == key);
            if (i >= 0) this[i] = new KeyValuePair<string, string>(key, value);
            else Add(new KeyValuePair<string, string>(key, value));
        }

        public void SetDefault(string key, string value)
        {
            if (FindIndex(p => p.Key == key) < 0) Add(new KeyValuePair<string, string>(key, value));
        }

        public string Get(string key)
        {
            var i = FindIndex(p => p.Key == key);
            return i >= 0 ? this[i].Value : null;
        }
    }

    /// <summary>
    /// Clone the reference .dtsx for one target table. Templates must identify the selected
    /// entity and staging/GUID objects explicitly; only supported staging projections and
    /// declared single-field match keys are accepted. Complete row collections expand to the
    /// target columns; partial collections retain semantic roles. Every DTSID is remapped to a
    /// GUID derived from the package name, so the same inputs give byte-identical output.
    /// </summary>
    internal static class Package
    {
        public static readonly string[] ColumnCollections = { "outputColumns", "externalMetadataColumns", "inputColumns" };
        public static readonly string[][] TypeAttrs =
        {
            new[] { "dataType", "length", "precision", "scale", "codePage" },
            new[] { "cachedDataType", "cachedLength", "cachedPrecision", "cachedScale", "cachedCodepage" },
        };
        private static readonly byte[] GuidNamespace = HexBytes("5d3f6c1e8a2b4f0e9c472b1d7e6a9f10");
        private const string GuidBody = "[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}";
        private static readonly Regex GuidRe = Py.Re(@"\{?(" + GuidBody + @")\}?");
        private static readonly Regex GuidFull = Py.Full(@"\{?(" + GuidBody + @")\}?");
        public static readonly Regex Select = Py.Start(@"^(\s*)SELECT\s+.*?\s+FROM\s+(\S+)(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex UpdateRe = Py.Start(@"^\s*UPDATE\s+(\S+)\s+SET\s+(.*?)\s+WHERE\s+\[?(\w+)\]?\s*=\s*\?\s*;?\s*$",
                                                           RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex AssignRe = Py.Start(@"^\s*\[?(\w+)\]?\s*=\s*\?\s*$");

        private static byte[] HexBytes(string hex)
        {
            return Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
        }

        /// <summary>(table, SET columns in parameter order, WHERE column) of a GUID table sync statement.</summary>
        public static Tuple<string, List<string>, string> ParseGuidUpdate(string sql)
        {
            var m = UpdateRe.Match(sql);
            if (!m.Success) return null;
            var columns = new List<string>();
            foreach (var part in m.Groups[2].Value.Split(','))
            {
                var a = AssignRe.Match(part);
                if (!a.Success) return null;
                columns.Add(a.Groups[1].Value);
            }
            return Tuple.Create(m.Groups[1].Value, columns, m.Groups[3].Value);
        }

        public static Regex TokenPattern(IEnumerable<string> tokens)
        {
            var alternatives = string.Join("|", tokens.OrderByDescending(t => t.Length).Select(Regex.Escape));
            return Py.Re("(?<![A-Za-z0-9])(?:" + alternatives + ")(?![A-Za-z0-9_])");
        }

        /// <summary>True if token occurs in text under the same boundary rules the Substituter uses.</summary>
        public static bool ContainsToken(string text, string token)
        {
            return TokenPattern(new[] { token }).IsMatch(text);
        }

        public static OrderedMap TableTokens(Table reference, Table target)
        {
            var tokens = new OrderedMap();
            tokens.Set(reference.StagingTable, target.StagingTable);
            tokens.Set(reference.GuidTable, target.GuidTable);
            tokens.Set(Metadata.ObjectName(reference.StagingTable), Metadata.ObjectName(target.StagingTable));
            tokens.Set(Metadata.ObjectName(reference.GuidTable), Metadata.ObjectName(target.GuidTable));
            tokens.Set(reference.PrimaryId, target.PrimaryId);
            tokens.Set(reference.SchemaName, target.SchemaName);
            tokens.Set(reference.LogicalName, target.LogicalName);
            // Match-key compatibility is checked before any substitutions are made.
            if (reference.MatchKeys.Count > 0 && target.MatchKeys.Count > 0) tokens.SetDefault(reference.MatchKeys[0], target.MatchKeys[0]);
            if (!string.IsNullOrEmpty(reference.PrimaryName) && !string.IsNullOrEmpty(target.PrimaryName))
                tokens.SetDefault(reference.PrimaryName, target.PrimaryName);
            if (reference.DisplayName != reference.LogicalName) tokens.SetDefault(reference.DisplayName, target.DisplayName);
            return tokens;
        }

        /// <summary>UUIDv5 of "seed|package|ORIGINAL" in the generator's namespace, as {UPPERCASE}.</summary>
        public static string DeterministicGuid(string seed, string packageName, string original)
        {
            var name = Encoding.UTF8.GetBytes(seed + "|" + packageName + "|" + original.ToUpperInvariant());
            byte[] hash;
            using (var sha1 = SHA1.Create()) hash = sha1.ComputeHash(GuidNamespace.Concat(name).ToArray());
            var b = hash.Take(16).ToArray();
            b[6] = (byte)((b[6] & 0x0F) | 0x50);
            b[8] = (byte)((b[8] & 0x3F) | 0x80);
            var hex = string.Concat(b.Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
            var text = hex.Substring(0, 8) + "-" + hex.Substring(8, 4) + "-" + hex.Substring(12, 4) + "-" + hex.Substring(16, 4) + "-" + hex.Substring(20);
            return "{" + text.ToUpperInvariant() + "}";
        }

        public static XmlDocument ParsePackage(byte[] xml, string label)
        {
            try { return Xml.Parse(xml); }
            catch (XmlException ex) { throw new GeneratorException("Reference package " + label + " is not well-formed XML: " + ex.Message, ex); }
        }

        /// <param name="updateOnly">the template is a deferred update pass whose destination action
        /// the caller has already validated; it has no create/upsert match fields.</param>
        public static RenderedPackage Render(byte[] templateXml, string templateLabel, Table reference, Table target,
                                             string packageName, string seed, bool updateOnly = false)
        {
            var doc = ParsePackage(templateXml, templateLabel);
            var root = doc.DocumentElement;
            if (root.NamespaceURI != Xml.DtsNs || root.LocalName != "Executable")
                throw new GeneratorException("Reference package " + templateLabel + " is not an SSIS package (root element is " + Py.Repr(root.Name) + ")");

            ClearUnusedSql(doc);
            var isProtected = ValidateTemplate(doc, templateLabel, reference, target, updateOnly);
            var subst = new Substituter(TableTokens(reference, target));
            var targetNames = new HashSet<string>(target.Columns.Select(c => c.Name), StringComparer.Ordinal);
            var columnNames = new OrderedMap();
            foreach (var c in reference.Columns) if (targetNames.Contains(c.Name)) columnNames.Set(c.Name, c.Name);
            columnNames.Set(reference.PrimaryId, target.PrimaryId);
            if (!string.IsNullOrEmpty(reference.PrimaryName) && !string.IsNullOrEmpty(target.PrimaryName))
                columnNames.Set(reference.PrimaryName, target.PrimaryName);
            if (reference.MatchKeys.Count > 0 && target.MatchKeys.Count > 0) columnNames.Set(reference.MatchKeys[0], target.MatchKeys[0]);
            var refColumns = new HashSet<string>(reference.Columns.Select(c => c.Name.ToLowerInvariant()), StringComparer.Ordinal);
            var columns = target.PackageColumns;
            var generated = new NodeSet(isProtected);
            // Staging columns the reference reads but sends to no KingswaySoft destination (for
            // example statecode, used only for the GUID table) stay out of every target's
            // destinations too.
            var sent = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in Xml.ByTag(doc, "component"))
                if (Xml.Props(c).Has("DestinationEntity") && !Xml.Inside(c, isProtected))
                    foreach (var i in Xml.ByTag(c, "inputColumn")) sent.Add(ColumnName(i).ToLowerInvariant());
            var unmapped = sent.Overlaps(refColumns)
                ? new HashSet<string>(refColumns.Where(n => !sent.Contains(n)), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            // Columns a destination may leave out for every table (the update destination leaves out
            // overriddencreatedon, which can only be set on create): shared by the reference and the
            // target under the same name, and not a key.
            var keys = new HashSet<string>(StringComparer.Ordinal) { reference.PrimaryId.ToLowerInvariant() };
            foreach (var k in reference.MatchKeys.Take(1)) keys.Add(k.ToLowerInvariant());
            if (!string.IsNullOrEmpty(reference.PrimaryName)) keys.Add(reference.PrimaryName.ToLowerInvariant());
            var targetLower = new HashSet<string>(columns.Select(c => c.Name.ToLowerInvariant()), StringComparer.Ordinal);
            var optional = new HashSet<string>(refColumns.Where(n => targetLower.Contains(n) && !keys.Contains(n)), StringComparer.Ordinal);

            var rebuilt = 0;
            foreach (var tag in ColumnCollections)
                foreach (var collection in Xml.ByTag(doc, tag))
                {
                    if (Xml.Inside(collection, isProtected)) continue;
                    if (RebuildCollection(doc, collection, refColumns, columns, subst, generated, columnNames, unmapped, optional)) rebuilt++;
                }
            if (rebuilt == 0)
                throw new GeneratorException("Reference package " + templateLabel + " has no data flow columns named after reference table "
                                             + Py.Repr(reference.LogicalName) + " columns; check --reference-table matches the package");
            foreach (var prop in Xml.ByTag(doc, "property"))
                if (prop.GetAttribute("name") == "SqlCommand" && !Xml.Inside(prop, isProtected))
                {
                    RewriteSqlCommand(doc, prop, reference, target, columns, subst, generated);
                    RewriteGuidUpdate(doc, prop, columnNames, subst, generated);
                }
            RetargetCommandParameters(doc, generated);

            foreach (var prop in Xml.ByTag(doc, "property"))
            {
                var name = prop.GetAttribute("name");
                if ((name == "Expression" || name == "FriendlyExpression") && !Xml.Inside(prop, isProtected))
                {
                    // A primary-name field can have the same spelling as its entity, e.g.
                    // new_language. In expressions it maps to the target's primary name.
                    var columnSubst = new Substituter(columnNames);
                    foreach (XmlNode child in prop.ChildNodes.Cast<XmlNode>().ToList())
                        if (Xml.IsTextOrCData(child)) Xml.SetData(child, subst.Apply(columnSubst.Apply(Xml.Data(child))));
                    generated.Add(prop);
                }
            }
            Apply(root, subst.Apply, generated);
            var warnings = RetargetDestinationMetadata(doc, target);

            ValidateReferences(root, templateLabel);
            var removed = reference.Columns.Select(c => c.Name).Where(n => !targetNames.Contains(n)).Distinct().ToList();
            foreach (var el in Xml.Elements(root))
            {
                if (Xml.Inside(el, isProtected)) continue;
                if (el.Name == "property" && el.GetAttribute("name") == "LookupTypes") continue;   // entity names, not columns
                var values = Xml.Attributes(el).Select(a => a.Value).Concat(new[] { Xml.Text(el) }).ToList();
                foreach (var old in removed)
                    if (values.Any(v => ContainsToken(v, old)))
                        throw new GeneratorException("Reference package " + templateLabel + ": unresolved reference column " + Py.Repr(old)
                                                     + " for " + Py.Repr(target.LogicalName) + "; provide explicit SQL/expression mappings");
            }

            var guidMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var el in Xml.Elements(root))
                foreach (var attr in new[] { "DTS:DTSID", "DTS:VersionGUID" })
                {
                    var m = GuidFull.Match(el.GetAttribute(attr) ?? "");
                    if (!m.Success) continue;
                    var key = m.Groups[1].Value.ToUpperInvariant();
                    if (!guidMap.ContainsKey(key)) guidMap[key] = DeterministicGuid(seed, packageName, key);
                }
            Apply(root, s => GuidRe.Replace(s, m =>
            {
                string mapped;
                return guidMap.TryGetValue(m.Groups[1].Value.ToUpperInvariant(), out mapped) ? mapped : m.Value;
            }), new NodeSet());

            root.SetAttribute("DTS:ObjectName", packageName);
            var bytes = Xml.ToXml(doc);
            return new RenderedPackage(packageName, root.GetAttribute("DTS:DTSID"), root.GetAttribute("DTS:VersionGUID"), bytes, warnings);
        }

        // OLE DB AccessMode values that read/write a named table or view; SqlCommand is then unused.
        private static readonly Dictionary<string, string[]> TableModes = new Dictionary<string, string[]>
        {
            { "source", new[] { "0", "1" } }, { "destination", new[] { "0", "1", "3", "4" } },
        };

        /// <summary>Blank a SqlCommand the component does not use (table access mode), so leftover draft
        /// queries in the reference are neither validated nor copied into every table.</summary>
        private static void ClearUnusedSql(XmlDocument doc)
        {
            foreach (var component in Xml.ByTag(doc, "component"))
            {
                var cls = component.GetAttribute("componentClassID").ToLowerInvariant();
                var kind = cls.Contains("oledbsource") ? "source" : cls.Contains("oledbdestination") ? "destination" : null;
                var props = Xml.Props(component);
                var mode = props.Has("AccessMode") ? props.Text("AccessMode") : null;
                if (kind != null && props.Has("SqlCommand") && mode != null && TableModes[kind].Contains(mode))
                    Xml.ClearChildren(props["SqlCommand"]);
            }
        }

        // Dataverse attribute type (manifest dataverseType) -> KingswaySoft destination field
        // metadata: (FieldType, SSIS dataType). Observed in hand-built KingswaySoft packages.
        private static readonly Dictionary<string, string[]> CrmFields = new Dictionary<string, string[]>
        {
            { "String", new[] { "String", "wstr" } }, { "Memo", new[] { "Memo", "nText" } }, { "Lookup", new[] { "Lookup", "guid" } },
            { "Customer", new[] { "Customer", "guid" } }, { "Owner", new[] { "Owner", "guid" } },
            { "Uniqueidentifier", new[] { "Uniqueidentifier", "guid" } }, { "DateTime", new[] { "DateTime", "dbTimeStamp" } },
            { "Picklist", new[] { "Picklist", "i4" } }, { "State", new[] { "State", "i4" } }, { "Status", new[] { "Status", "i4" } },
            { "Boolean", new[] { "Boolean", "bool" } }, { "Integer", new[] { "Integer", "i4" } }, { "BigInt", new[] { "BigInt", "i8" } },
            { "EntityName", new[] { "EntityName", "wstr" } },
        };
        // Not seen in a reference package yet; the designer's metadata refresh corrects them if needed.
        private static readonly Dictionary<string, string[]> CrmFieldsUnverified = new Dictionary<string, string[]>
        {
            { "Money", new[] { "Money", "cy" } }, { "Decimal", new[] { "Decimal", "numeric" } }, { "Double", new[] { "Double", "r8" } },
            { "MultiSelectPicklist", new[] { "MultiSelectPicklist", "wstr" } },
        };
        private static readonly Dictionary<string, string> LookupTypes = new Dictionary<string, string>
        {
            { "Customer", "account;contact" }, { "Owner", "systemuser;team" },
        };

        /// <summary>(FieldType, type attributes, LookupTypes, verified) for a destination field.</summary>
        public static Tuple<string, OrderedMap, string, bool> CrmField(Column column)
        {
            var known = CrmFields.ContainsKey(column.DataverseType);
            string[] pair;
            if (!CrmFields.TryGetValue(column.DataverseType, out pair) && !CrmFieldsUnverified.TryGetValue(column.DataverseType, out pair))
                pair = new[] { "String", "wstr" };
            var fieldType = pair[0];
            var attrs = new OrderedMap();
            attrs.Set("dataType", pair[1]);
            if (pair[1] == "wstr")
                attrs.Set("length", Py.Str(fieldType == "EntityName" ? 64 : (column.SsisType.Length ?? 0) != 0 ? column.SsisType.Length.Value : 4000));
            else if (pair[1] == "numeric") { attrs.Set("precision", "23"); attrs.Set("scale", "10"); }
            else if (fieldType == "DateTime" && column.SqlType.Trim().ToUpperInvariant() == "DATE") attrs.Set("dataType", "dbDate");
            string lookups;
            if (!LookupTypes.TryGetValue(fieldType, out lookups))
                lookups = fieldType == "Lookup" ? string.Join(";", column.AllLookupTargets) : "";
            return Tuple.Create(fieldType, attrs, lookups, known);
        }

        /// <summary>Rewrite the cached field metadata of rebuilt KingswaySoft destination columns and of
        /// the GUID table columns, which copy their types from an unrelated reference column.</summary>
        private static List<string> RetargetDestinationMetadata(XmlDocument doc, Table target)
        {
            var byName = new Dictionary<string, Column>(StringComparer.Ordinal);
            foreach (var c in target.Columns) byName[c.Name.ToLowerInvariant()] = c;
            var inferred = new List<string>();
            foreach (var component in Xml.ByTag(doc, "component"))
            {
                var props = Xml.Props(component);
                if (props.Has("DestinationEntity"))
                {
                    foreach (var ext in Xml.ByTag(component, "externalMetadataColumn"))
                    {
                        Column column;
                        if (!byName.TryGetValue(ext.GetAttribute("name").ToLowerInvariant(), out column) || string.IsNullOrEmpty(column.DataverseType))
                            continue;   // entity field the reference cached but the table does not map
                        var field = CrmField(column);
                        foreach (var attr in new[] { "dataType", "length", "precision", "scale", "codePage" })
                            if (ext.HasAttribute(attr)) ext.RemoveAttribute(attr);
                        foreach (var kv in field.Item2) ext.SetAttribute(kv.Key, kv.Value);
                        foreach (var prop in Xml.ByTag(ext, "property"))
                        {
                            var pname = prop.GetAttribute("name");
                            var value = pname == "FieldType" ? field.Item1 : pname == "LookupTypes" ? field.Item3 : null;
                            if (value == null) continue;
                            Xml.ClearChildren(prop);
                            if (value.Length > 0) prop.AppendChild(doc.CreateTextNode(value));
                        }
                        if (!field.Item4 && !inferred.Contains(column.Name)) inferred.Add(column.Name);
                    }
                }
                else if (props.Has("OpenRowset") && SameSqlObject(props.Text("OpenRowset"), target.GuidTable))
                {
                    // The scaffolder creates GUID tables with the record id as VARCHAR(100).
                    foreach (var ext in Xml.ByTag(component, "externalMetadataColumn"))
                    {
                        if (ext.GetAttribute("name").ToLowerInvariant() != target.PrimaryId.ToLowerInvariant()) continue;
                        foreach (var attr in new[] { "dataType", "length", "codePage" })
                            if (ext.HasAttribute(attr)) ext.RemoveAttribute(attr);
                        ext.SetAttribute("codePage", "1252");
                        ext.SetAttribute("dataType", "str");
                        ext.SetAttribute("length", "100");
                    }
                }
            }
            return inferred.Count > 0
                ? new List<string> { target.LogicalName + ": KingswaySoft field types inferred for " + string.Join(", ", inferred)
                                     + "; refresh the destination's metadata in Visual Studio if it reports a mismatch" }
                : new List<string>();
        }

        public static string ColumnName(XmlElement el)
        {
            var cached = el.GetAttribute("cachedName");
            return cached.Length > 0 ? cached : el.GetAttribute("name");
        }

        private static string MapName(OrderedMap columnNames, string name)
        {
            return columnNames.Get(name) ?? name;
        }

        /// <summary>Reference column names (lower case) as the target's: the reference's record id, primary
        /// name and match key stand for the target's own (a reference that leaves out its primary name
        /// leaves out every table's), other columns keep their name.</summary>
        private static HashSet<string> AsTarget(IEnumerable<string> names, OrderedMap columnNames)
        {
            var toTarget = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in columnNames) toTarget[kv.Key.ToLowerInvariant()] = kv.Value.ToLowerInvariant();
            return new HashSet<string>(names.Select(n => toTarget.TryGetValue(n, out var t) ? t : n), StringComparer.Ordinal);
        }

        private static bool LeavesOutShared(Dictionary<string, XmlElement> byName, HashSet<string> refColumns, HashSet<string> unmapped,
                                            HashSet<string> optional)
        {
            var leftOut = refColumns.Where(n => !byName.ContainsKey(n)).ToList();
            return leftOut.Count > 0 && leftOut.All(n => optional.Contains(n) || unmapped.Contains(n)) && leftOut.Count < byName.Count;
        }

        private static bool RebuildCollection(XmlDocument doc, XmlElement collection, HashSet<string> refColumns, List<Column> columns,
                                              Substituter subst, NodeSet generated, OrderedMap columnNames, HashSet<string> unmapped,
                                              HashSet<string> optional)
        {
            var children = Xml.Children(collection);
            var fromReference = children.Where(n => refColumns.Contains(ColumnName(n).ToLowerInvariant())).ToList();
            if (fromReference.Count == 0) return false;
            var byName = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
            foreach (var n in fromReference) byName[ColumnName(n).ToLowerInvariant()] = n;
            var owner = Xml.Ancestors(collection).FirstOrDefault(a => a.Name == "component");
            var entityFields = collection.Name == "externalMetadataColumns" && owner != null && Xml.Props(owner).Has("DestinationEntity");
            // Only a complete row collection expands to all target fields (a destination's row
            // leaves out the columns the reference sends to no destination). Key-only and
            // GUID-cache collections retain their roles and cardinality.
            List<Column> selected;
            if (entityFields)
            {
                // A KingswaySoft destination's cached entity fields: the target's Dataverse columns,
                // plus the staging-only columns (statecode, ownerid, ...) this cache lists.
                selected = columns.Where(c => !string.IsNullOrEmpty(c.DataverseType) || byName.ContainsKey(c.Name.ToLowerInvariant())).ToList();
            }
            else if (new HashSet<string>(byName.Keys).SetEquals(refColumns))
            {
                selected = columns.ToList();
            }
            else if (unmapped.Count > 0 && new HashSet<string>(byName.Keys).SetEquals(refColumns.Where(n => !unmapped.Contains(n))))
            {
                var dropped = AsTarget(unmapped, columnNames);
                selected = columns.Where(c => !dropped.Contains(c.Name.ToLowerInvariant())).ToList();
            }
            else if (LeavesOutShared(byName, refColumns, unmapped, optional))
            {
                // Every column but a few shared ones (a key-only collection keeps far fewer than it
                // leaves out, and keeps the keys).
                var leftOut = new HashSet<string>(refColumns.Where(n => !byName.ContainsKey(n)), StringComparer.Ordinal);
                var dropped = AsTarget(leftOut, columnNames);
                selected = columns.Where(c => !dropped.Contains(c.Name.ToLowerInvariant())).ToList();
            }
            else
            {
                var wanted = new HashSet<string>(fromReference.Select(n => MapName(columnNames, ColumnName(n)).ToLowerInvariant()), StringComparer.Ordinal);
                selected = columns.Where(c => wanted.Contains(c.Name.ToLowerInvariant())).ToList();
                if (selected.Count != wanted.Count)
                {
                    var have = new HashSet<string>(selected.Select(c => c.Name.ToLowerInvariant()));
                    var missing = Py.Sorted(wanted.Where(w => !have.Contains(w)));
                    var name = owner != null ? owner.GetAttribute("name") : "?";
                    throw new GeneratorException("Template component " + Py.Repr(name) + " uses " + string.Join(", ", missing)
                                                 + ", which the target table does not have (not in its manifest columns or staging table script)");
                }
            }
            var selectedNames = new HashSet<string>(selected.Select(c => c.Name.ToLowerInvariant()), StringComparer.Ordinal);
            var refSet = new NodeSet(fromReference);
            var bound = children.Where(n => refSet.Contains(n) || selectedNames.Contains(ColumnName(n).ToLowerInvariant())).ToList();
            var clones = new List<XmlElement>();
            foreach (var col in selected)
            {
                var prototype = fromReference.FirstOrDefault(n => MapName(columnNames, ColumnName(n)).ToLowerInvariant() == col.Name.ToLowerInvariant());
                if (prototype == null)
                {
                    // Never copy lookup, expression, or vendor-specific column settings from an
                    // arbitrary field to a new one.
                    prototype = fromReference.FirstOrDefault(n => Xml.ByTag(n, "property").Count == 0);
                    if (prototype == null && collection.Name == "externalMetadataColumns")
                        prototype = fromReference[0];   // field metadata is rewritten from the manifest afterwards
                    if (prototype == null)
                        throw new GeneratorException("No plain column prototype for " + Py.Repr(col.Name) + "; supply explicit column mappings in the template");
                }
                // A system field (no manifest type) keeps the reference entity's cached type.
                var keepType = entityFields && string.IsNullOrEmpty(col.DataverseType)
                               && ColumnName(prototype).ToLowerInvariant() == col.Name.ToLowerInvariant();
                clones.Add(CloneColumn(prototype, col, subst, !keepType));
            }
            foreach (var clone in clones) generated.Add(clone);
            ReplaceNodes(doc, collection, bound, clones);
            return true;
        }

        private static XmlElement CloneColumn(XmlElement prototype, Column column, Substituter subst, bool setType)
        {
            var old = ColumnName(prototype);
            var clone = (XmlElement)prototype.CloneNode(true);
            var suffix = "[" + old + "]";
            Func<string, string> retarget = value =>
            {
                if (value == old) return column.Name;
                if (value.EndsWith(suffix, StringComparison.Ordinal))
                    return subst.Apply(value.Substring(0, value.Length - suffix.Length)) + "[" + column.Name + "]";   // refId / lineageId paths
                return subst.Apply(value);
            };
            var rename = new Substituter(new[] { new KeyValuePair<string, string>(old, column.Name) });
            foreach (var el in Xml.Elements(clone))
            {
                foreach (XmlAttribute attr in el.Attributes.Cast<XmlAttribute>().ToList()) attr.Value = retarget(attr.Value);
                foreach (XmlNode child in el.ChildNodes.Cast<XmlNode>().ToList())
                {
                    if (!Xml.IsTextOrCData(child)) continue;
                    var data = Xml.Data(child);
                    Xml.SetData(child, data.Trim() == old ? data.Replace(old, column.Name) : rename.Apply(subst.Apply(data)));
                }
            }
            if (setType)
                foreach (var family in TypeAttrs)
                    if (clone.HasAttribute(family[0])) SetType(clone, family, column.SsisType);
            return clone;
        }

        public static void SetType(XmlElement el, string[] family, SsisType type)
        {
            foreach (var attr in family) if (el.HasAttribute(attr)) el.RemoveAttribute(attr);
            el.SetAttribute(family[0], type.DataType);
            var values = new[] { type.Length, type.Precision, type.Scale, type.CodePage };
            for (var i = 0; i < 4; i++)
                if (values[i].HasValue) el.SetAttribute(family[i + 1], Py.Str(values[i].Value));
        }

        private static void ReplaceNodes(XmlDocument doc, XmlElement parent, List<XmlElement> oldNodes, List<XmlElement> newNodes)
        {
            var first = oldNodes[0];
            var prev = first.PreviousSibling;
            var indent = prev != null && Xml.IsTextNode(prev) && Xml.Data(prev).Trim().Length == 0 ? Xml.Data(prev) : null;
            for (var i = 0; i < newNodes.Count; i++)
            {
                if (i > 0 && indent != null) parent.InsertBefore(doc.CreateTextNode(indent), first);
                parent.InsertBefore(newNodes[i], first);
            }
            foreach (var node in oldNodes)
            {
                var before = node.PreviousSibling;
                parent.RemoveChild(node);
                if (node != first && before != null && Xml.IsTextNode(before) && Xml.Data(before).Trim().Length == 0)
                    parent.RemoveChild(before);
            }
        }

        private static string RawText(XmlNode node)
        {
            return string.Concat(node.ChildNodes.Cast<XmlNode>().Where(Xml.IsTextOrCData).Select(Xml.Data));
        }

        private static void RewriteSqlCommand(XmlDocument doc, XmlElement prop, Table reference, Table target, List<Column> columns,
                                              Substituter subst, NodeSet generated)
        {
            var m = Select.Match(RawText(prop));
            if (!m.Success) return;
            var source = m.Groups[2].Value.TrimEnd(';');
            var refNames = new HashSet<string> { reference.StagingTable.ToLowerInvariant(), Metadata.ObjectName(reference.StagingTable).ToLowerInvariant() };
            if (!refNames.Contains(source.ToLowerInvariant()) && !refNames.Contains(Metadata.ObjectName(source).ToLowerInvariant())) return;
            var selectList = string.Join(",\n", columns.Select(c => "    [" + c.Name + "]"));
            Xml.SetText(prop, m.Groups[1].Value + "SELECT\n" + selectList + "\nFROM " + target.StagingTable + subst.Apply(m.Groups[3].Value));
            generated.Add(prop);
        }

        /// <summary>Rename the columns of a GUID table sync statement (already validated) for the target.
        /// Parameter order is unchanged, so the input columns keep their Param_N mappings.</summary>
        private static void RewriteGuidUpdate(XmlDocument doc, XmlElement prop, OrderedMap columnNames, Substituter subst, NodeSet generated)
        {
            if (generated.Contains(prop)) return;
            var parsed = ParseGuidUpdate(Xml.Text(prop));
            if (parsed == null) return;
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in columnNames) names[kv.Key.ToLowerInvariant()] = kv.Value;
            Func<string, string> rename = c => { string v; return names.TryGetValue(c.ToLowerInvariant(), out v) ? v : c; };
            var sets = string.Join(",\n", parsed.Item2.Select(c => "    [" + rename(c) + "] = ?"));
            Xml.SetText(prop, "UPDATE " + subst.Apply(parsed.Item1) + "\nSET\n" + sets + "\nWHERE [" + rename(parsed.Item3) + "] = ?;");
            generated.Add(prop);
        }

        /// <summary>Give each OLE DB Command parameter the type of the rebuilt column bound to it.</summary>
        private static void RetargetCommandParameters(XmlDocument doc, NodeSet generated)
        {
            foreach (var component in Xml.ByTag(doc, "component"))
            {
                if (!component.GetAttribute("componentClassID").ToLowerInvariant().Contains("oledbcommand")) continue;
                // By parameter name: rebuilt columns already carry the target's task path, the
                // parameters are renamed later with the rest of the package.
                var parameters = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
                foreach (var e in Xml.ByTag(component, "externalMetadataColumn")) parameters[e.GetAttribute("name")] = e;
                foreach (var inp in Xml.ByTag(component, "inputColumn"))
                {
                    var id = inp.GetAttribute("externalMetadataColumnId");
                    var marker = ".ExternalColumns[";
                    var at = id.LastIndexOf(marker, StringComparison.Ordinal);
                    var tail = at < 0 ? id : id.Substring(at + marker.Length);
                    tail = tail.Length > 0 ? tail.Substring(0, tail.Length - 1) : tail;
                    XmlElement param;
                    if (!parameters.TryGetValue(tail, out param) || !generated.Contains(inp)) continue;
                    Func<string, int?> num = a => inp.GetAttribute(a).Length > 0 ? int.Parse(inp.GetAttribute(a), CultureInfo.InvariantCulture) : (int?)null;
                    SetType(param, TypeAttrs[0], new SsisType(inp.GetAttribute("cachedDataType"), num("cachedLength"), num("cachedPrecision"),
                                                              num("cachedScale"), num("cachedCodepage")));
                }
            }
        }

        /// <summary>Apply fn to every attribute value, text, CDATA and comment below node, except skipped subtrees.</summary>
        public static void Apply(XmlNode node, Func<string, string> fn, NodeSet skip)
        {
            if (skip.Contains(node)) return;
            if (node.NodeType == XmlNodeType.Element)
                foreach (XmlAttribute attr in node.Attributes.Cast<XmlAttribute>().ToList())
                {
                    var updated = fn(attr.Value);
                    if (updated != attr.Value) attr.Value = updated;
                }
            foreach (XmlNode child in node.ChildNodes.Cast<XmlNode>().ToList())
            {
                if (child.NodeType == XmlNodeType.Element) Apply(child, fn, skip);
                else if (Xml.IsTextOrCData(child) || child.NodeType == XmlNodeType.Comment)
                {
                    var data = Xml.Data(child);
                    var updated = fn(data);
                    if (updated != data) Xml.SetData(child, updated);
                }
            }
        }

        private static readonly Regex Placeholder = Py.Re(@"Flattened_StagingTemplateEntity|\bTODO\b|\bTO DO\b|\{\{.*?\}\}", RegexOptions.IgnoreCase);
        private static readonly Regex QualifiedRef = Py.Re(@"(?<![A-Za-z0-9_])(?:\[?\w+\]?\.){1,2}\[?(?:stage|guid)_[A-Za-z0-9_]+\]?", RegexOptions.IgnoreCase);
        private static readonly Regex TokenRef = Py.Re(@"(?<![A-Za-z0-9])(?:stage|guid)_[A-Za-z0-9_]+", RegexOptions.IgnoreCase);
        private static readonly Regex FromWord = Py.Re(@"\bFROM\b", RegexOptions.IgnoreCase);
        private static readonly Regex SelectPrefix = Py.Re(@"^\s*SELECT\s+", RegexOptions.IgnoreCase);
        private static readonly Regex PlainColumn = Py.Full(@"\[?[A-Za-z_][A-Za-z0-9_]*\]?");

        /// <summary>Fail closed where metadata cannot prove a table-specific substitution. Lookup
        /// components are independent readers: their declared target is validated, then their
        /// subtree is kept out of column rebuilding and substitution.</summary>
        private static NodeSet ValidateTemplate(XmlDocument doc, string label, Table reference, Table target, bool updateOnly)
        {
            Action<string> fail = m => { throw new GeneratorException("Reference package " + label + ", table " + Py.Repr(target.LogicalName) + ": " + m); };
            var isProtected = new NodeSet();
            var destinations = new List<XmlElement>();
            var sourceFound = false;
            var allowedLookups = new HashSet<string>(reference.Columns.SelectMany(c => c.LookupTargets).Select(t => t.ToLowerInvariant()));
            var targetLookups = new HashSet<string>(target.Columns.SelectMany(c => c.LookupTargets).Select(t => t.ToLowerInvariant()));
            var referenceNames = new HashSet<string>(reference.Columns.Select(c => c.Name.ToLowerInvariant()), StringComparer.Ordinal);
            foreach (var component in Xml.ByTag(doc, "component"))
            {
                var own = Xml.ByTag(component, "property").Where(p => p.ParentNode.ParentNode == component).ToList();
                var props = Xml.Props(component);
                var name = component.GetAttribute("name");
                if (props.Count != own.Count) fail("duplicate properties in component " + Py.Repr(name));
                var identity = (component.GetAttribute("componentClassID") + " "
                                + string.Join(" ", props.Items().Where(kv => kv.Key.ToLowerInvariant() == "usercomponenttypename").Select(kv => kv.Value))).ToLowerInvariant();
                if (identity.Contains("lookup"))
                {
                    var entities = props.Items().Where(kv => (kv.Key == "SourceEntity" || kv.Key == "LookupEntity") && kv.Value.Length > 0)
                                        .Select(kv => kv.Value.ToLowerInvariant()).ToList();
                    if (entities.Count == 0 || entities.Any(v => !allowedLookups.Contains(v) || !targetLookups.Contains(v)))
                        fail("lookup component " + Py.Repr(name) + " has an unverified lookup target; "
                             + "provide a template whose lookup targets are declared by both tables");
                    isProtected.Add(component);
                    continue;
                }
                foreach (var key in new[] { "SourceEntity", "DestinationEntity" })
                {
                    if (!props.Has(key)) continue;
                    if (props.Text(key) != reference.LogicalName)
                        fail(key + " " + Py.Repr(props.Text(key)) + " does not match selected reference table " + Py.Repr(reference.LogicalName)
                             + "; select the matching reference package/table");
                    if (key == "DestinationEntity") destinations.Add(component);
                    else sourceFound = true;
                }
                var sql = props.TextOr("SqlCommand", "");
                if (sql.Length > 0 && identity.Contains("oledbcommand"))
                {
                    var parsed = ParseGuidUpdate(sql);
                    if (parsed == null || !SameSqlObject(parsed.Item1, reference.GuidTable)
                        || parsed.Item3.ToLowerInvariant() != reference.PrimaryId.ToLowerInvariant()
                        || parsed.Item2.Any(c => !referenceNames.Contains(c.ToLowerInvariant())))
                        fail("SqlCommand in " + Py.Repr(name) + " is not a supported GUID table sync; use UPDATE " + reference.GuidTable
                             + " SET [column] = ?, ... WHERE [" + reference.PrimaryId + "] = ? with staging columns only");
                }
                else if (sql.Length > 0)
                {
                    var match = Select.Match(sql);
                    if (!match.Success || !SameSqlObject(match.Groups[2].Value.TrimEnd(';'), reference.StagingTable))
                        fail("SqlCommand in " + Py.Repr(name) + " is not a supported SELECT from " + reference.StagingTable
                             + "; supply explicit staging SQL, not a legacy-source placeholder");
                    // Replacing arbitrary projections would discard legacy mappings or transformations.
                    var projection = FromWord.Split(sql, 2)[0];
                    projection = SelectPrefix.Replace(projection, "", 1);
                    var parts = projection.Split(',');
                    if (parts.Any(p => !PlainColumn.IsMatch(p.Trim()) || !referenceNames.Contains(p.Trim().Trim('[', ']').ToLowerInvariant())))
                        fail("SqlCommand contains an unsupported projection; use explicit staging column names "
                             + "and put legacy mappings in the scaffolder's staging population SQL");
                    var projected = parts.Select(p => p.Trim().Trim('[', ']').ToLowerInvariant()).ToList();
                    if (projected.Distinct().Count() != projected.Count || !new HashSet<string>(projected).SetEquals(referenceNames))
                        fail("SqlCommand has an ambiguous partial/duplicate projection; provide the complete "
                             + "reference staging column list so generated row metadata matches the query");
                    var tail = match.Groups[3].Value.Trim().TrimEnd(';');
                    var keyText = reference.MatchKeys.Count == 1 ? reference.MatchKeys[0] : "\0";
                    if (tail.Length > 0 && !Py.Full(@"WHERE\s+\[?" + Regex.Escape(keyText) + @"\]?\s+IS\s+NOT\s+NULL", RegexOptions.IgnoreCase).IsMatch(tail))
                        fail("SqlCommand has an unsupported filter/join; provide explicit table-specific SQL");
                    sourceFound = true;
                }
                foreach (var dynamic in new[] { "SqlCommandVariable", "OpenRowsetVariable" })
                    if (!string.IsNullOrEmpty(props.Text(dynamic))) fail("dynamic " + dynamic + " is unsupported; supply an explicit staging source");
                var rowset = props.TextOr("OpenRowset", "");
                if (identity.Contains("oledbsource") && sql.Length == 0 && !SameSqlObject(rowset, reference.StagingTable))
                    fail("OLE DB source does not identify the reference staging table; supply explicit staging SQL");
                if (rowset.Length > 0 && Metadata.ObjectName(rowset).ToLowerInvariant() == Metadata.ObjectName(reference.StagingTable).ToLowerInvariant())
                    sourceFound = true;
            }

            // Check SQL tasks, properties, expressions and identifiers too. Merely finding a valid
            // entity elsewhere does not excuse a stale staging/GUID reference.
            var expectedLower = new HashSet<string> { Metadata.ObjectName(reference.StagingTable).ToLowerInvariant(), Metadata.ObjectName(reference.GuidTable).ToLowerInvariant() };
            var expectedExact = new HashSet<string>(StringComparer.Ordinal) { Metadata.ObjectName(reference.StagingTable), Metadata.ObjectName(reference.GuidTable) };
            foreach (var el in Xml.Elements(doc.DocumentElement))
            {
                var values = Xml.Attributes(el).Select(a => a.Value).Concat(new[] { Xml.Text(el) });
                foreach (var value in values)
                {
                    if (Placeholder.IsMatch(value))
                        fail("unresolved placeholder SQL or expression; supply the real table-specific staging query");
                    if (Xml.Inside(el, isProtected)) continue;
                    foreach (Match q in QualifiedRef.Matches(value))
                        if (!SameSqlObject(q.Value, reference.StagingTable) && !SameSqlObject(q.Value, reference.GuidTable))
                            fail("staging/GUID reference " + Py.Repr(q.Value) + " does not match the selected schema/table");
                    foreach (Match t in TokenRef.Matches(value))
                        if (!expectedLower.Contains(t.Value.ToLowerInvariant()) || !expectedExact.Contains(t.Value))
                            fail("staging/GUID reference " + Py.Repr(t.Value) + " does not match selected reference table "
                                 + Py.Repr(reference.LogicalName) + "; isolate legitimate lookups in validated lookup components");
                }
            }
            if (destinations.Count == 0 || !sourceFound)
                fail("cannot identify both reference source and destination; select an unambiguous table flow template");

            foreach (var component in destinations)
            {
                var props = Xml.AllProps(component);
                var mapped = new HashSet<string>(Xml.ByTag(component, "inputColumn").Select(c => c.GetAttribute("cachedName").ToLowerInvariant()));
                var action = props.TextOr("ActionType", "").ToLowerInvariant();
                // An update (KingswaySoft ActionType 3) matches on the record id it is given.
                var byPrimaryKey = (action == "3" || action == "update") && mapped.Contains(reference.PrimaryId.ToLowerInvariant());
                if (!updateOnly && !byPrimaryKey && string.IsNullOrEmpty(props.Text("UpsertMatchingFields"))
                    && string.IsNullOrEmpty(props.Text("RecordMatchingKeyFields")) && action != "1" && action != "create" && action != "insert")
                    fail("unsupported destination action without explicit match fields; provide a compatible "
                         + "create-only template or declared match-key configuration");
                // RecordMatchingCriteria 0 (none) and 1 (primary key) need no key fields.
                var criteria = props.TextOr("RecordMatchingCriteria", "0");
                if (criteria != "0" && criteria != "1" && string.IsNullOrEmpty(props.Text("RecordMatchingKeyFields")))
                    fail("unsupported RecordMatchingCriteria without explicit RecordMatchingKeyFields; provide a template with declared match fields");
            }
            var keyProps = destinations.SelectMany(c => Xml.ByTag(c, "property"))
                                       .Where(p => (p.GetAttribute("name") == "UpsertMatchingFields" || p.GetAttribute("name") == "RecordMatchingKeyFields")
                                                   && Xml.Text(p).Length > 0).ToList();
            var docText = keyProps.Count > 0 ? null : Xml.DocumentText(doc);
            var usesKeys = keyProps.Count > 0 || reference.MatchKeys.Any(k => ContainsToken(docText, k));
            if (usesKeys)
            {
                foreach (var table in new[] { reference, target })
                {
                    if (table.MatchKeys.Count != 1)
                        fail("unsupported match-key configuration for " + Py.Repr(table.LogicalName) + ": expected one declared "
                             + "match key for this template; supply a compatible template or explicit metadata (no key is inferred)");
                    if (!table.PackageColumns.Any(c => c.Name.ToLowerInvariant() == table.MatchKeys[0].ToLowerInvariant()))
                        fail("match key " + Py.Repr(table.MatchKeys[0]) + " is not a writable staging column");
                }
                foreach (var prop in keyProps)
                    if (Xml.Text(prop).ToLowerInvariant() != reference.MatchKeys[0].ToLowerInvariant())
                        fail(prop.GetAttribute("name") + " does not equal the reference match key; composite/custom matching is unsupported");
            }
            else if (reference.MatchKeys.Count > 1 || target.MatchKeys.Count > 1)
                fail("composite match keys are unsupported; provide an explicit table-specific template");
            ValidateReferences(doc.DocumentElement, label);
            return isProtected;
        }

        private static readonly Regex ExpressionLineage = Py.Re(@"#\{([^}]+)\}");

        public static void ValidateReferences(XmlElement root, string label)
        {
            var elements = Xml.Elements(root);
            var ids = elements.Where(e => e.HasAttribute("refId")).Select(e => e.GetAttribute("refId")).ToList();
            var idSet = new HashSet<string>(ids, StringComparer.Ordinal);
            if (ids.Count != idSet.Count) throw new GeneratorException("Reference package " + label + ": duplicate pipeline refId");
            var lineageValues = elements.Where(e => e.Name == "outputColumn").Select(e => e.GetAttribute("lineageId")).ToList();
            var lineage = new HashSet<string>(lineageValues, StringComparer.Ordinal);
            if (lineageValues.Any(v => v.Length == 0) || lineageValues.Count != lineage.Count)
                throw new GeneratorException("Reference package " + label + ": missing or duplicate output lineageId");
            var executables = new HashSet<string>(elements.Where(e => e.LocalName == "Executable").Select(e => e.GetAttribute("DTS:refId")), StringComparer.Ordinal);
            foreach (var el in elements)
            {
                foreach (var attr in new[] { "externalMetadataColumnId", "startId", "endId", "synchronousInputId" })
                {
                    var value = el.GetAttribute(attr);
                    if (value.Length > 0 && value != "0" && !idSet.Contains(value))
                        throw new GeneratorException("Reference package " + label + ": unresolved " + attr + " " + Py.Repr(value));
                }
                if (el.Name == "inputColumn" && !lineage.Contains(el.GetAttribute("lineageId")))
                    throw new GeneratorException("Reference package " + label + ": unresolved input lineageId " + Py.Repr(el.GetAttribute("lineageId")));
                foreach (var attr in new[] { "DTS:From", "DTS:To" })
                {
                    var value = el.GetAttribute(attr);
                    if (value.Length > 0 && !executables.Contains(value))
                        throw new GeneratorException("Reference package " + label + ": unresolved " + attr + " " + Py.Repr(value));
                }
                if (el.Name == "property" && el.GetAttribute("name") == "Expression")
                    foreach (Match m in ExpressionLineage.Matches(Xml.Text(el)))
                        if (!lineage.Contains(m.Groups[1].Value))
                            throw new GeneratorException("Reference package " + label + ": unresolved expression lineage " + Py.Repr(m.Groups[1].Value));
            }
        }

        public static bool SameSqlObject(string actual, string expected)
        {
            actual = actual.Replace("[", "").Replace("]", "").ToLowerInvariant();
            expected = expected.Replace("[", "").Replace("]", "").ToLowerInvariant();
            return actual == expected || (!actual.Contains(".") && actual == expected.Split('.').Last());
        }
    }
}
