using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace DataverseMigrationScaffolder.HarnessGen
{
    internal sealed class StageParts
    {
        public XmlElement Task;
        public XmlElement Manager;
        public string FileName;
        public string Sql;
    }

    internal sealed class StageTemplate
    {
        public XmlElement Task;            // the template Execute SQL task (detached)
        public XmlElement Manager;         // its package FILE connection manager (detached)
        public string FileName;            // e.g. stage_new_Project.sql
        public string Sql;
        public List<string> Columns;       // staging columns the template loads, in order
        public string LegacyTable;         // e.g. [Legacy].[dbo].[CaseType]
        public string LegacyAlias;         // m
        public string GuidAlias;           // glt
        public string Rest;                // everything after "FROM <legacy table> <alias>"
        public bool StripPrefix;           // legacy names drop the publisher prefix
        public HashSet<string> Excluded;   // reference staging columns not loaded
        public bool ExcludesPrimaryName;
    }

    internal sealed class StageSql
    {
        public string Sql;
        public string FileName;
        public List<string> Unresolved;    // lookups staged as NULL, with the reason
        public List<string> Assumptions;   // polymorphic lookups resolved by their type column
    }

    /// <summary>
    /// Stage &lt;Table&gt; SQL tasks: load each staging table from the legacy database. The
    /// reference package may hold, next to its Migrate flow, one Execute SQL task named
    /// "Stage &lt;Display Name&gt;" that runs a .sql file through a package FILE connection. From it
    /// the generator learns which staging columns are loaded, how staging columns map to legacy
    /// columns (same name, or without the publisher prefix) and how the staging table maps to
    /// the legacy table. The record id comes from the table's own GUID table; everything after
    /// FROM is kept and retargeted. Lookups are resolved through the target table's GUID table
    /// (polymorphic ones by their &lt;lookup&gt;type column); unresolvable ones are staged as NULL.
    /// </summary>
    internal static class Stage
    {
        public const string TaskPrefix = "Stage ";
        private static readonly Regex InsertRe = Py.Start(@"^\s*INSERT\s+INTO\s+(\S+)\s*\((.*?)\)\s*SELECT\s+(.*?)\s+FROM\s+(\S+)\s+(?:AS\s+)?(\w+)\b(.*)$",
                                                           RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex ExprRe = Py.Start(@"^\s*(\w+)\.\[?(\w+)\]?\s*$");
        private static readonly Regex NameRe = Py.Start(@"^\s*\[?(\w+)\]?\s*$");
        private static readonly Regex LineComment = Py.Re(@"--[^\n]*");
        private static readonly Regex AsAlias = Py.Re(@"\bAS\s+(\w+)", RegexOptions.IgnoreCase);
        private static readonly Regex LastPart = Py.Re(@"\[?\w+\]?$");

        private static List<string> SplitList(string text)
        {
            var parts = new List<string>();
            var depth = 0;
            var current = "";
            foreach (var ch in text)
            {
                if (ch == '(') depth++;
                if (ch == ')') depth--;
                if (ch == ',' && depth == 0) { parts.Add(current); current = ""; }
                else current += ch;
            }
            parts.Add(current);
            return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        }

        /// <summary>(package without Stage tasks, stage template parts or null). The returned package
        /// holds only the Migrate flow. At most one Execute SQL task is allowed, reading its SQL from
        /// a package FILE connection.</summary>
        public static Tuple<byte[], StageParts> SplitReference(byte[] xml, string label, string projectDir)
        {
            var doc = Package.ParsePackage(xml, label);
            var root = doc.DocumentElement;
            var tasks = Xml.Children(root, "Executables").SelectMany(c => Xml.Children(c, "Executable")).ToList();
            var sqlTasks = tasks.Where(t => t.GetAttribute("DTS:ExecutableType") == "Microsoft.ExecuteSQLTask").ToList();
            if (sqlTasks.Count == 0) return Tuple.Create(xml, (StageParts)null);
            Action<string> fail = m => { throw new GeneratorException("Reference package " + label + ": " + m); };

            if (sqlTasks.Count > 1)
                fail("has " + sqlTasks.Count + " Execute SQL tasks; keep one 'Stage <Display Name>' task as the staging template");
            var task = sqlTasks[0];
            var name = task.GetAttribute("DTS:ObjectName");
            if (!name.StartsWith(TaskPrefix, StringComparison.Ordinal))
                fail("Execute SQL task " + Py.Repr(name) + " must be named 'Stage <Display Name>' to be used as the staging template");
            var data = Xml.ByTagNs(task, Xml.SqlTaskNs, "SqlTaskData");
            if (data.Count == 0 || Xml.GetNs(data[0], Xml.SqlTaskNs, "SqlStmtSourceType") != "FileConnection")
                fail("task " + Py.Repr(name) + " must read its SQL from a file connection (SQLSourceType File connection)");
            var source = Xml.GetNs(data[0], Xml.SqlTaskNs, "SqlStatementSource");
            var managers = Xml.Children(root, "ConnectionManagers").SelectMany(c => Xml.Children(c, "ConnectionManager")).ToList();
            var manager = managers.FirstOrDefault(m => (source == m.GetAttribute("DTS:ObjectName") || source == m.GetAttribute("DTS:DTSID"))
                                                       && m.GetAttribute("DTS:CreationName") == "FILE");
            if (manager == null) fail("task " + Py.Repr(name) + " reads " + Py.Repr(source) + ", which is not a package FILE connection manager");
            var inner = Xml.ByTag(manager, "DTS:ConnectionManager");
            var pathText = inner.Count > 0 ? inner[0].GetAttribute("DTS:ConnectionString") : "";
            var sqlPath = FindSql(pathText, projectDir);
            if (sqlPath == null)
                fail("cannot find the staging template SQL file " + Py.Repr(pathText) + " (also looked next to the project and in "
                     + "a Queries folder beside it)");
            var sql = Metadata.ReadText(sqlPath);

            // Remove the task, its constraints, its layout and its FILE manager from the flow template.
            var reference = task.GetAttribute("DTS:refId");
            task.ParentNode.RemoveChild(task);
            foreach (var constraints in Xml.Children(root, "PrecedenceConstraints"))
            {
                foreach (var pc in Xml.Children(constraints))
                    if (pc.GetAttribute("DTS:From") == reference || pc.GetAttribute("DTS:To") == reference) constraints.RemoveChild(pc);
                if (Xml.Children(constraints).Count == 0) root.RemoveChild(constraints);
            }
            manager.ParentNode.RemoveChild(manager);
            foreach (var design in Xml.Children(root, "DesignTimeProperties"))
            {
                var text = string.Concat(design.ChildNodes.Cast<XmlNode>().Where(Xml.IsTextOrCData).Select(Xml.Data));
                text = Py.Re(@"<NodeLayout\b[^>]*?Id=""" + Regex.Escape(reference) + @"""[^>]*/>", RegexOptions.Singleline).Replace(text, m => "");
                if (Xml.Children(root, "PrecedenceConstraints").Count == 0)
                    text = Py.Re(@"<EdgeLayout\b[^>]*?Id=""Package\.PrecedenceConstraints\[[^""]*""[^>]*>.*?</EdgeLayout>", RegexOptions.Singleline)
                             .Replace(text, m => "");
                Xml.ClearChildren(design);
                design.AppendChild(doc.CreateCDataSection(text));
            }
            return Tuple.Create(Xml.ToXml(doc), new StageParts { Task = task, Manager = manager, FileName = Path.GetFileName(sqlPath), Sql = sql });
        }

        private static string FindSql(string pathText, string projectDir)
        {
            if (string.IsNullOrEmpty(pathText)) return null;
            var name = pathText.Replace("\\", "/").Split('/').Last();
            var candidates = new[]
            {
                pathText, Path.Combine(projectDir, name), Path.Combine(projectDir, "Queries", name),
                Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectDir)) ?? projectDir, "Queries", name),
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        public static StageTemplate ParseTemplate(StageParts parts, Table reference, HashSet<string> prefixes, string label)
        {
            Action<string> fail = msg => { throw new GeneratorException("Staging template " + parts.FileName + ": " + msg); };
            var sql = LineComment.Replace(parts.Sql, m => "");
            var match = InsertRe.Match(sql);
            if (!match.Success)
                fail("expected INSERT INTO <staging table> (<columns>) SELECT <alias>.[<column>], ... FROM <legacy table> <alias> ...");
            var target = match.Groups[1].Value;
            var legacyTable = match.Groups[4].Value;
            var alias = match.Groups[5].Value;
            var rest = match.Groups[6].Value;
            if (Metadata.ObjectName(target).ToLowerInvariant() != Metadata.ObjectName(reference.StagingTable).ToLowerInvariant())
                fail("inserts into " + target + ", not the reference staging table " + reference.StagingTable);
            var columns = new List<string>();
            foreach (var c in SplitList(match.Groups[2].Value))
            {
                var m = NameRe.Match(c);
                if (!m.Success) fail("unsupported column " + Py.Repr(c));
                columns.Add(m.Groups[1].Value);
            }
            var exprs = new List<Match>();
            foreach (var e in SplitList(match.Groups[3].Value))
            {
                var m = ExprRe.Match(e);
                if (!m.Success) fail("unsupported select expression " + Py.Repr(e) + "; use <alias>.[<column>]");
                exprs.Add(m);
            }
            if (columns.Count != exprs.Count) fail("inserts " + columns.Count + " columns but selects " + exprs.Count + " values");
            string guidAlias = null;
            var modes = new HashSet<string>();
            var names = new HashSet<string>(reference.Columns.Select(c => c.Name.ToLowerInvariant()), StringComparer.Ordinal);
            for (var i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                if (!names.Contains(col.ToLowerInvariant())) fail("loads " + Py.Repr(col) + ", which the reference flow does not read from staging");
                var exprAlias = exprs[i].Groups[1].Value;
                var source = exprs[i].Groups[2].Value;
                if (col.ToLowerInvariant() == reference.PrimaryId.ToLowerInvariant())
                {
                    if (exprAlias == alias || source.ToLowerInvariant() != reference.PrimaryId.ToLowerInvariant())
                        fail(col + " must come from the GUID table (" + reference.GuidTable + "), as <alias>.[" + col + "]");
                    guidAlias = exprAlias;
                    continue;
                }
                if (exprAlias != alias) fail(col + " must come from the legacy table alias " + Py.Repr(alias));
                var mode = Mapping(col, source, prefixes);
                if (mode == null)
                    fail("maps " + col + " to " + alias + ".[" + source + "]; only the same name or the name without its publisher prefix ("
                         + string.Join(", ", Py.Sorted(prefixes)) + ") is supported");
                if (PrefixOf(col, prefixes) != null) modes.Add(mode);
            }
            if (guidAlias == null) fail("does not stage the record id " + reference.PrimaryId + " from the GUID table");
            if (modes.Count > 1) fail("maps some prefixed columns with their publisher prefix and some without; use one rule");
            var strip = modes.Count == 1 && modes.Contains("strip");
            if (!Package.ContainsToken(rest, Metadata.ObjectName(reference.GuidTable)))
                fail("does not join the GUID table " + reference.GuidTable);
            var legacyName = NameRe.Match(legacyTable.Split('.').Last());
            var expected = legacyName.Success ? LegacyNameFor(reference.SchemaName, prefixes, strip) : null;
            if (!legacyName.Success || legacyName.Groups[1].Value.ToLowerInvariant() != expected.ToLowerInvariant())
                fail("reads " + legacyTable + "; the legacy table must be named after the staging table's schema name (" + (expected ?? "None") + ")");
            var loaded = new HashSet<string>(columns.Select(c => c.ToLowerInvariant()), StringComparer.Ordinal);
            var excluded = new HashSet<string>(reference.Columns.Select(c => c.Name.ToLowerInvariant()).Where(n => !loaded.Contains(n)), StringComparer.Ordinal);
            var excludesPrimaryName = !string.IsNullOrEmpty(reference.PrimaryName) && excluded.Contains(reference.PrimaryName.ToLowerInvariant());
            if (excludesPrimaryName) excluded.Remove(reference.PrimaryName.ToLowerInvariant());
            return new StageTemplate
            {
                Task = parts.Task, Manager = parts.Manager, FileName = parts.FileName, Sql = parts.Sql, Columns = columns,
                LegacyTable = legacyTable, LegacyAlias = alias, GuidAlias = guidAlias, Rest = rest, StripPrefix = strip,
                Excluded = excluded, ExcludesPrimaryName = excludesPrimaryName,
            };
        }

        private static string PrefixOf(string name, HashSet<string> prefixes)
        {
            return prefixes.OrderByDescending(p => p.Length).ThenBy(p => p, StringComparer.Ordinal)
                           .FirstOrDefault(p => name.ToLowerInvariant().StartsWith(p, StringComparison.Ordinal));
        }

        private static string Mapping(string col, string source, HashSet<string> prefixes)
        {
            if (source.ToLowerInvariant() == col.ToLowerInvariant()) return "same";
            var prefix = PrefixOf(col, prefixes);
            if (prefix != null && source.ToLowerInvariant() == col.Substring(prefix.Length).ToLowerInvariant()) return "strip";
            return null;
        }

        public static string LegacyNameFor(string name, HashSet<string> prefixes, bool strip)
        {
            var prefix = strip ? PrefixOf(name, prefixes) : null;
            return prefix != null ? name.Substring(prefix.Length) : name;
        }

        /// <summary>Publisher prefixes ('new_', 'msa_', ...) of the scaffolded tables' logical names.</summary>
        public static HashSet<string> PublisherPrefixes(IEnumerable<Table> tables)
        {
            return new HashSet<string>(tables.Where(t => t.LogicalName.Contains("_"))
                                             .Select(t => t.LogicalName.Split(new[] { '_' }, 2)[0].ToLowerInvariant() + "_"), StringComparer.Ordinal);
        }

        private sealed class Joins
        {
            private readonly HashSet<string> _taken;
            public readonly List<string> Lines = new List<string>();

            public Joins(IEnumerable<string> taken)
            {
                _taken = new HashSet<string>(taken.Select(t => t.ToLowerInvariant()), StringComparer.Ordinal);
            }

            public string Alias()
            {
                var n = Lines.Count + 1;
                while (_taken.Contains("lk" + n)) n++;
                _taken.Add("lk" + n);
                return "lk" + n;
            }
        }

        /// <summary>(SQL expression for the lookup's GUID or null, reason when null).</summary>
        private static Tuple<string, string> LookupExpr(Column column, string legacyValue, string typeValue, Dictionary<string, Table> tables, Joins joins)
        {
            var targets = column.LookupTargets.Select(t => { Table x; return tables.TryGetValue(t.ToLowerInvariant(), out x) ? x : null; }).ToList();
            var usable = targets.Where(t => t != null && t.MatchKeys.Count == 1).ToList();
            var polymorphic = column.IsPolymorphic || column.LookupTargets.Count != 1;
            if (!polymorphic)
            {
                if (usable.Count == 0)
                    return Tuple.Create((string)null, targets[0] != null
                        ? "target " + column.LookupTargets[0] + " has no legacy key, so its GUIDs cannot be looked up"
                        : "target " + column.LookupTargets[0] + " is not in the manifest");
                var target = usable[0];
                var alias = joins.Alias();
                joins.Lines.Add("LEFT JOIN " + target.GuidTable + " AS " + alias + " ON " + alias + ".[" + target.MatchKeys[0] + "] = " + legacyValue);
                return Tuple.Create(alias + ".[" + target.PrimaryId + "]", (string)null);
            }
            if (typeValue == null)
                return Tuple.Create((string)null, "polymorphic lookup without a " + column.Name + "type column naming its target table");
            if (usable.Count == 0)
                return Tuple.Create((string)null, "none of its targets has a legacy key, so their GUIDs cannot be looked up");
            var cases = new List<string>();
            foreach (var target in usable)
            {
                var alias = joins.Alias();
                joins.Lines.Add("LEFT JOIN " + target.GuidTable + " AS " + alias + " ON " + typeValue + " = N'" + target.LogicalName + "' AND "
                                + alias + ".[" + target.MatchKeys[0] + "] = " + legacyValue);
                cases.Add("WHEN N'" + target.LogicalName + "' THEN " + alias + ".[" + target.PrimaryId + "]");
            }
            return Tuple.Create("CASE " + typeValue + " " + string.Join(" ", cases) + " END", (string)null);
        }

        private static List<Column> StagedColumns(StageTemplate template, Table table)
        {
            var skip = new HashSet<string>(template.Excluded, StringComparer.Ordinal);
            if (template.ExcludesPrimaryName && !string.IsNullOrEmpty(table.PrimaryName)) skip.Add(table.PrimaryName.ToLowerInvariant());
            return table.PackageColumns.Where(c => !skip.Contains(c.Name.ToLowerInvariant())).ToList();
        }

        private static string LegacyTableFor(StageTemplate template, Table table, HashSet<string> prefixes)
        {
            var replacement = "[" + LegacyNameFor(table.SchemaName, prefixes, template.StripPrefix) + "]";
            return LastPart.Replace(template.LegacyTable, m => replacement);
        }

        /// <summary>The Stage &lt;Table&gt; SQL for one target table.</summary>
        public static StageSql StageSqlFor(StageTemplate template, Table reference, Table table, Dictionary<string, Table> tables, HashSet<string> prefixes)
        {
            Func<string, string> legacy = n => template.LegacyAlias + ".[" + LegacyNameFor(n, prefixes, template.StripPrefix) + "]";
            var joins = new Joins(new[] { template.LegacyAlias, template.GuidAlias }.Concat(AsAlias.Matches(template.Rest).Cast<Match>().Select(m => m.Groups[1].Value)));
            var byName = new Dictionary<string, Column>(StringComparer.Ordinal);
            foreach (var c in table.Columns) byName[c.Name.ToLowerInvariant()] = c;
            var columns = new List<string>();
            var values = new List<string>();
            var unresolved = new List<string>();
            var assumptions = new List<string>();
            foreach (var column in StagedColumns(template, table))
            {
                string value;
                if (column.Name.ToLowerInvariant() == table.PrimaryId.ToLowerInvariant())
                    value = template.GuidAlias + ".[" + table.PrimaryId + "]";
                else if (column.IsLookup)
                {
                    Column typeColumn;
                    byName.TryGetValue((column.Name + "type").ToLowerInvariant(), out typeColumn);
                    var result = LookupExpr(column, legacy(column.Name), typeColumn != null ? legacy(typeColumn.Name) : null, tables, joins);
                    value = result.Item1;
                    if (value == null)
                    {
                        unresolved.Add(column.Name + ": " + result.Item2 + "; staged as NULL");
                        value = "NULL";
                    }
                    else if (column.LookupTargets.Count != 1 || column.IsPolymorphic)
                        assumptions.Add(column.Name + ": legacy " + legacy(typeColumn.Name) + " must hold the target table's logical name");
                }
                else value = legacy(column.Name);
                columns.Add(column.Name);
                values.Add(value);
            }

            var refKey = reference.MatchKeys[0];
            var key = table.MatchKeys[0];
            var subst = new Substituter(new[]
            {
                Pair(reference.GuidTable, table.GuidTable),
                Pair(Metadata.ObjectName(reference.GuidTable), Metadata.ObjectName(table.GuidTable)),
                Pair(reference.StagingTable, table.StagingTable),
                Pair(Metadata.ObjectName(reference.StagingTable), Metadata.ObjectName(table.StagingTable)),
                Pair(refKey, key),
                Pair(LegacyNameFor(refKey, prefixes, template.StripPrefix), LegacyNameFor(key, prefixes, template.StripPrefix)),
            });
            var rest = subst.Apply(template.Rest).Trim();
            var lines = new List<string> { "INSERT INTO " + table.StagingTable + " (" };
            for (var i = 0; i < columns.Count; i++) lines.Add("[" + columns[i] + "]" + (i < columns.Count - 1 ? "," : ")"));
            lines.Add("SELECT " + string.Join(",\n", values));
            lines.Add("FROM " + LegacyTableFor(template, table, prefixes) + " " + template.LegacyAlias);
            lines.AddRange(joins.Lines);
            lines.Add(rest);
            var sql = string.Join("\n", lines) + "\n";
            var fileName = new Substituter(new[] { Pair(Metadata.ObjectName(reference.StagingTable), Metadata.ObjectName(table.StagingTable)) })
                .Apply(template.FileName);
            if (fileName == template.FileName && table.LogicalName.ToLowerInvariant() != reference.LogicalName.ToLowerInvariant())
                fileName = Metadata.ObjectName(table.StagingTable) + ".sql";
            return new StageSql { Sql = sql, FileName = fileName, Unresolved = unresolved, Assumptions = assumptions };
        }

        private static KeyValuePair<string, string> Pair(string k, string v) { return new KeyValuePair<string, string>(k, v); }

        /// <summary>Source query of an Update &lt;Table&gt; Lookups flow: the record id from the GUID table
        /// and each deferred lookup resolved again from the legacy row, now that its targets are loaded.</summary>
        public static Tuple<string, List<string>> DeferredSourceSql(StageTemplate template, Table table, List<Column> viewColumns,
                                                                    HashSet<string> deferred, Dictionary<string, Table> tables, HashSet<string> prefixes)
        {
            var key = table.MatchKeys[0];
            var pid = table.PrimaryId;
            Func<string, string> legacy = n => "m.[" + LegacyNameFor(n, prefixes, template.StripPrefix) + "]";
            var joins = new Joins(new[] { "s", "g", "m" });
            var byName = new Dictionary<string, Column>(StringComparer.Ordinal);
            foreach (var c in table.Columns) byName[c.Name.ToLowerInvariant()] = c;
            var select = new List<string>();
            var unresolved = new List<string>();
            foreach (var column in viewColumns)
            {
                var name = column.Name;
                var sqlType = string.IsNullOrEmpty(column.SqlType) ? "NVARCHAR(100)" : column.SqlType;
                if (name.ToLowerInvariant() == pid.ToLowerInvariant())
                    select.Add("    CAST(g.[" + pid + "] AS " + sqlType + ") AS [" + pid + "]");
                else if (deferred.Contains(name.ToLowerInvariant()))
                {
                    Column typeColumn;
                    byName.TryGetValue((name + "type").ToLowerInvariant(), out typeColumn);
                    var result = LookupExpr(column, legacy(name), typeColumn != null ? "s.[" + typeColumn.Name + "]" : null, tables, joins);
                    string value;
                    if (result.Item1 == null)
                    {
                        unresolved.Add(name + ": " + result.Item2 + "; the staged value is sent");
                        value = "s.[" + name + "]";
                    }
                    else value = "CAST(" + result.Item1 + " AS " + sqlType + ")";   // GUID tables hold VARCHAR(100) ids
                    select.Add("    " + value + " AS [" + name + "]");
                }
                else select.Add("    s.[" + name + "]");
            }
            var sql = "SELECT\n" + string.Join(",\n", select) + "\nFROM " + table.StagingTable + " s\n"
                      + "INNER JOIN " + table.GuidTable + " g ON g.[" + key + "] = s.[" + key + "]\n"
                      + "INNER JOIN " + LegacyTableFor(template, table, prefixes) + " m ON " + legacy(key) + " = s.[" + key + "]\n"
                      + string.Concat(joins.Lines.Select(l => l + "\n"))
                      + "WHERE g.[" + pid + "] IS NOT NULL";
            return Tuple.Create(sql, unresolved);
        }

        /// <summary>(Execute SQL task, FILE connection manager) for one table, cloned from the template
        /// so every task setting matches it.</summary>
        public static Tuple<XmlElement, XmlElement> Task(XmlDocument doc, StageTemplate template, string taskName, string managerName,
                                                         string sqlPath, string packageName, string seed)
        {
            var task = (XmlElement)doc.ImportNode(template.Task, true);
            var oldRef = task.GetAttribute("DTS:refId");
            var newRef = "Package\\" + taskName;
            foreach (var el in Xml.Elements(task))
                foreach (XmlAttribute attr in el.Attributes.Cast<XmlAttribute>().ToList())
                    if (attr.Value.Contains(oldRef)) attr.Value = attr.Value.Replace(oldRef, newRef);
            task.SetAttribute("DTS:refId", newRef);
            task.SetAttribute("DTS:ObjectName", taskName);
            task.SetAttribute("DTS:DTSID", Package.DeterministicGuid(seed, packageName, taskName));
            var data = Xml.ByTagNs(task, Xml.SqlTaskNs, "SqlTaskData")[0];
            Xml.SetNs(data, "SQLTask", "SqlStatementSource", Xml.SqlTaskNs, managerName);
            var manager = (XmlElement)doc.ImportNode(template.Manager, true);
            manager.SetAttribute("DTS:refId", "Package.ConnectionManagers[" + managerName + "]");
            manager.SetAttribute("DTS:ObjectName", managerName);
            manager.SetAttribute("DTS:DTSID", Package.DeterministicGuid(seed, packageName, "file:" + managerName));
            Xml.ByTag(manager, "DTS:ConnectionManager")[0].SetAttribute("DTS:ConnectionString", sqlPath);
            return Tuple.Create(task, manager);
        }
    }
}
