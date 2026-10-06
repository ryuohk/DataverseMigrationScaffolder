using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace DataverseMigrationScaffolder.HarnessGen
{
    /// <summary>
    /// Re-read the assembled project and check that it is internally consistent. These are XML
    /// checks on the files about to be written; they do not show that Visual Studio can build the
    /// project, that KingswaySoft accepts the components, or that a migration runs.
    /// </summary>
    internal static class Audit
    {
        private static readonly Regex Placeholder = Py.Re(@"Flattened_StagingTemplateEntity|\bTODO\b|\bTO DO\b|\{\{.*?\}\}", RegexOptions.IgnoreCase);

        public static readonly string[] Checks =
        {
            "project lists exactly the generated packages, with matching package metadata",
            "project lists exactly the carried project connection managers",
            "package ids are unique and match the project metadata",
            "entry point calls exactly the setup, harness and deferred update packages",
            "no unresolved placeholder SQL or expressions",
            "each table's data flow reads its own staging table, targets its own entity and syncs its own GUID table",
            "no reference-table identifiers left in other tables' scopes (lookups excluded)",
        };

        private static List<string> Duplicates(IEnumerable<string> names)
        {
            var lowered = names.Select(n => n.ToLowerInvariant()).ToList();
            return Py.Sorted(lowered.Where(n => lowered.Count(x => x == n) > 1).Distinct());
        }

        private static bool IsLookup(XmlElement component)
        {
            var props = Xml.Props(component);
            var name = props.Items().Where(kv => kv.Key.ToLowerInvariant() == "usercomponenttypename").Select(kv => kv.Value).LastOrDefault() ?? "";
            return (component.GetAttribute("componentClassID") + " " + name).ToLowerInvariant().Contains("lookup");
        }

        private static Regex Token(string text)
        {
            return Py.Re("(?<![A-Za-z0-9])" + Regex.Escape(text) + "(?![A-Za-z0-9_])", RegexOptions.IgnoreCase);
        }

        /// <summary>Raise on the first inconsistency; return the checks that passed. scopes maps a file to
        /// the tables it loads as (top-level task name, table).</summary>
        public static List<string> AuditProject(byte[] dtprojXml, List<KeyValuePair<string, byte[]>> packages, List<string> connectionFiles,
                                                List<KeyValuePair<string, List<Tuple<string, Table>>>> scopes, Table reference,
                                                string orchestrator, HashSet<string> called, HashSet<string> generated)
        {
            Action<string> fail = m => { throw new GeneratorException("Output check failed: " + m); };
            var project = Xml.Parse(dtprojXml);
            var listed = Xml.ByTagNs(project, Xml.SsisNs, "Package").Where(e => e.ParentNode.LocalName == "Packages")
                            .Select(e => Xml.GetNs(e, Xml.SsisNs, "Name")).ToList();
            var meta = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
            foreach (var e in Xml.ByTagNs(project, Xml.SsisNs, "PackageMetaData")) meta[Xml.GetNs(e, Xml.SsisNs, "Name")] = e;
            var files = packages.Select(p => p.Key).ToList();
            if (Duplicates(listed).Count > 0) fail("project lists packages more than once: " + string.Join(", ", Duplicates(listed)));
            if (!Py.SortedLower(listed).SequenceEqual(Py.SortedLower(files)))
                fail("project package list " + Py.Repr(Py.Sorted(listed)) + " does not match the generated files " + Py.Repr(Py.Sorted(files)));
            if (meta.Count > 0 && !Py.SortedLower(meta.Keys).SequenceEqual(Py.SortedLower(files)))
                fail("project package metadata does not match the generated files");
            var managers = Xml.ByTagNs(project, Xml.SsisNs, "ConnectionManager").Where(e => e.ParentNode.LocalName == "ConnectionManagers")
                              .Select(e => Xml.GetNs(e, Xml.SsisNs, "Name")).ToList();
            if (Duplicates(managers).Count > 0 || !Py.SortedLower(managers).SequenceEqual(Py.SortedLower(connectionFiles)))
                fail("project connection managers " + Py.Repr(Py.Sorted(managers)) + " do not match the carried files " + Py.Repr(Py.Sorted(connectionFiles)));

            var ids = new Dictionary<string, string>(StringComparer.Ordinal);
            var roots = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
            foreach (var pair in packages)
            {
                var file = pair.Key;
                var root = Xml.Parse(pair.Value).DocumentElement;
                roots[file] = root;
                var dtsid = root.GetAttribute("DTS:DTSID").ToUpperInvariant();
                if (ids.ContainsKey(dtsid)) fail(file + " and " + ids[dtsid] + " share package id " + dtsid);
                ids[dtsid] = file;
                XmlElement entry;
                meta.TryGetValue(file, out entry);
                var props = new Dictionary<string, string>(StringComparer.Ordinal);
                if (entry != null)
                    foreach (var p in Xml.ByTagNs(entry, Xml.SsisNs, "Property").Where(p => p.ParentNode.ParentNode == entry))
                        props[Xml.GetNs(p, Xml.SsisNs, "Name")] = Xml.Text(p);
                var stem = Path.GetFileNameWithoutExtension(file);
                string id, name;
                if (entry != null && generated.Contains(file)
                    && ((props.TryGetValue("ID", out id) ? id : "").ToUpperInvariant() != dtsid || (props.TryGetValue("Name", out name) ? name : null) != stem
                        || root.GetAttribute("DTS:ObjectName") != stem))
                    fail("project metadata for " + file + " does not match the package's id or name");
                foreach (var el in Xml.Elements(root))
                    foreach (var value in Xml.Attributes(el).Select(a => a.Value).Concat(new[] { Xml.Text(el) }))
                    {
                        var m = Placeholder.Match(value);
                        if (m.Success) fail(file + " contains unresolved placeholder text " + Py.Repr(m.Value));
                    }
            }

            if (orchestrator != null)
            {
                var invoked = Xml.ByTag(roots[orchestrator], "PackageName").Where(n => n.FirstChild != null).Select(n => Xml.Data(n.FirstChild)).ToList();
                if (Duplicates(invoked).Count > 0 || !new HashSet<string>(invoked, StringComparer.Ordinal).SetEquals(called ?? new HashSet<string>()))
                    fail(orchestrator + " does not call exactly the setup, harness and deferred update packages");
                if (!invoked.All(listed.Contains)) fail(orchestrator + " calls packages that are not in the project");
            }

            foreach (var scopeEntry in scopes)
            {
                var root = roots[scopeEntry.Key];
                var sequences = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
                foreach (var s in Xml.Children(root, "Executables").SelectMany(c => Xml.Children(c, "Executable"))) sequences[s.GetAttribute("DTS:ObjectName")] = s;
                foreach (var item in scopeEntry.Value)
                {
                    XmlElement scope;
                    if (item.Item1 == null) scope = root;
                    else if (!sequences.TryGetValue(item.Item1, out scope)) fail(scopeEntry.Key + " has no task " + Py.Repr(item.Item1) + " for " + item.Item2.LogicalName);
                    AuditScope(scope, item.Item2, reference, scopeEntry.Key + " (" + item.Item2.LogicalName + ")", fail);
                }
            }
            return Checks.ToList();
        }

        private static void AuditScope(XmlElement scope, Table table, Table reference, string label, Action<string> fail)
        {
            var lookups = new NodeSet();
            var destinations = 0;
            foreach (var component in Xml.ByTag(scope, "component"))
            {
                if (IsLookup(component)) { lookups.Add(component); continue; }
                var props = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var p in Xml.ByTag(component, "property").Where(p => p.ParentNode.ParentNode == component)) props[p.GetAttribute("name")] = Xml.Text(p);
                foreach (var key in new[] { "SourceEntity", "DestinationEntity" })
                    if (props.ContainsKey(key) && props[key].ToLowerInvariant() != table.LogicalName.ToLowerInvariant())
                        fail(label + ": " + key + " is " + Py.Repr(props[key]));
                if (props.ContainsKey("DestinationEntity")) destinations++;
                string sql;
                props.TryGetValue("SqlCommand", out sql);
                if (component.GetAttribute("componentClassID").ToLowerInvariant().Contains("oledbcommand"))
                {
                    var update = Package.ParseGuidUpdate(sql ?? "");
                    if (update == null || !Package.SameSqlObject(update.Item1, table.GuidTable) || update.Item3.ToLowerInvariant() != table.PrimaryId.ToLowerInvariant())
                        fail(label + ": OLE DB Command does not update " + table.GuidTable + " by " + table.PrimaryId);
                    continue;
                }
                var match = Package.Select.Match(sql ?? "");
                if (!string.IsNullOrEmpty(sql) && (!match.Success || !Package.SameSqlObject(match.Groups[2].Value.TrimEnd(';'), table.StagingTable)))
                    fail(label + ": source query does not read " + table.StagingTable);
                var isSource = component.GetAttribute("componentClassID").ToLowerInvariant().Contains("source");
                string rowset;
                if (isSource && props.TryGetValue("OpenRowset", out rowset) && rowset.Length > 0 && !Package.SameSqlObject(rowset, table.StagingTable))
                    fail(label + ": source table " + Py.Repr(rowset) + " is not " + table.StagingTable);
            }
            if (destinations == 0) fail(label + ": no destination component targets the table");
            if (table.LogicalName.ToLowerInvariant() == reference.LogicalName.ToLowerInvariant()) return;

            // A reference identifier may legitimately appear when this table declares the same
            // name, for example a lookup column to the reference table.
            var declared = new HashSet<string>(new[] { table.LogicalName, table.SchemaName, table.PrimaryId,
                    Metadata.ObjectName(table.StagingTable), Metadata.ObjectName(table.GuidTable) }
                .Concat(table.Columns.Select(c => c.Name)).Concat(table.Columns.SelectMany(c => c.LookupTargets))
                .Concat(table.Dependencies).Concat(table.DroppedDependencies).Concat(table.ExternalDependencies)
                .Select(n => n.ToLowerInvariant()), StringComparer.Ordinal);
            var leftovers = new[] { reference.LogicalName, reference.SchemaName, reference.PrimaryId,
                                    Metadata.ObjectName(reference.StagingTable), Metadata.ObjectName(reference.GuidTable) }
                .Distinct(StringComparer.Ordinal).Where(t => !declared.Contains(t.ToLowerInvariant())).ToList();
            var patterns = leftovers.Select(t => Tuple.Create(t, Token(t))).ToList();
            var stack = new Stack<XmlElement>();
            stack.Push(scope);
            while (stack.Count > 0)
            {
                var el = stack.Pop();
                if (lookups.Contains(el)) continue;
                var values = Xml.Attributes(el).Select(a => a.Value).Concat(new[] { Xml.Text(el) }).ToList();
                foreach (var pattern in patterns)
                    if (values.Any(v => pattern.Item2.IsMatch(v)))
                        fail(label + ": reference table identifier " + Py.Repr(pattern.Item1) + " remains in <" + el.Name + ">");
                foreach (var child in Xml.Children(el)) stack.Push(child);
            }
        }
    }
}
