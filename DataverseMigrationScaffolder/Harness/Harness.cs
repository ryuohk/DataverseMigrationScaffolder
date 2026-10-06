using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace DataverseMigrationScaffolder.HarnessGen
{
    internal sealed class SqlTask
    {
        public SqlTask(string name, string sql) { Name = name; Sql = sql; }
        public string Name { get; }
        public string Sql { get; }
    }

    internal sealed class StageItem
    {
        public string TaskName;
        public string ManagerName;
        public string SqlPath;
    }

    /// <summary>
    /// Compose the packages of a generated migration harness. For each scaffolder file group NN:
    /// "NNa - Staging" (Create Staging Tables, then one Stage &lt;Table&gt; task per table when the
    /// reference has a staging template) and "NNb - Harness" (Create GUID Tables, then one
    /// Migrate &lt;Table&gt; data flow per table). Plus "00 - Error and UpdateTime Tables" (error and
    /// UpdateTime tables and every GUID table) and "NN - Deferred Updates".
    /// </summary>
    internal static class Harness
    {
        public const string SetupName = "00 - Error and UpdateTime Tables";
        public const string ErrorTable = "[dbo].[Error]";
        public const string UpdateTimeSql = "SET ANSI_NULLS ON;\nSET QUOTED_IDENTIFIER ON;\n\n"
            + "IF OBJECT_ID(N'[dbo].[UpdateTime]', N'U') IS NULL\nBEGIN\n    CREATE TABLE [dbo].[UpdateTime](\n"
            + "        [DeltaDate] DATETIME2(7) NOT NULL,\n        [rowid] TINYINT NULL\n    );\nEND\n\n"
            + "IF NOT EXISTS (SELECT 1 FROM [dbo].[UpdateTime] WHERE [rowid] = 1)\nBEGIN\n"
            + "    INSERT INTO [dbo].[UpdateTime] ([DeltaDate], [rowid])\n"
            + "    VALUES (CONVERT(datetime2(7), '1753-01-01T00:00:00.0000000'), 1);\nEND\n";
        private static readonly Regex InvalidNameChars = Py.Re(@"[\\/:\[\].=;""'<>|*?]+");
        private static readonly Regex Spaces = Py.Re(@"\s+");

        public static string StagingName(int number) { return Py.D2(number) + "a - Staging"; }
        public static string HarnessName(int number) { return Py.D2(number) + "b - Harness"; }
        public static string DeferredName(int number) { return Py.D2(number) + " - Deferred Updates"; }

        /// <summary>Task label per table: its display name, made unique within one package.</summary>
        public static Dictionary<string, string> TaskLabels(IEnumerable<Table> tables)
        {
            var clean = new List<KeyValuePair<string, string>>();
            foreach (var t in tables)
            {
                var label = Spaces.Replace(InvalidNameChars.Replace(t.DisplayName, m => " "), m => " ").Trim();
                var entry = new KeyValuePair<string, string>(t.LogicalName, label.Length > 0 ? label : t.LogicalName);
                var i = clean.FindIndex(p => p.Key == t.LogicalName);
                if (i >= 0) clean[i] = entry; else clean.Add(entry);
            }
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in clean)
            {
                var key = kv.Value.ToLowerInvariant();
                counts[key] = (counts.ContainsKey(key) ? counts[key] : 0) + 1;
            }
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in clean)
                result[kv.Key] = counts[kv.Value.ToLowerInvariant()] == 1 ? kv.Value : kv.Value + " (" + kv.Key + ")";
            return result;
        }

        /// <summary>A package with the reference's package-level settings (format version,
        /// protection level, parameters) and no tasks, connections, variables or layout.</summary>
        public static XmlDocument BlankPackage(byte[] templateXml, string label, Table reference, string packageName, string seed)
        {
            var baseRendered = Package.Render(templateXml, label, reference, reference, packageName, seed);
            var doc = Xml.Parse(baseRendered.Xml);
            var root = doc.DocumentElement;
            foreach (var node in Xml.Children(root))
                if (new[] { "Variables", "Executables", "PrecedenceConstraints", "EventHandlers", "DesignTimeProperties", "ConnectionManagers" }.Contains(node.LocalName))
                    root.RemoveChild(node);
            if (Xml.Children(root, "PropertyExpression").Count > 0)
                throw new GeneratorException("Generated packages cannot keep package-level property expressions from the template");
            root.SetAttribute("DTS:ObjectName", packageName);
            root.SetAttribute("DTS:DTSID", Package.DeterministicGuid(seed, packageName, "package"));
            root.SetAttribute("DTS:VersionGUID", Package.DeterministicGuid(seed, packageName, "version"));
            return doc;
        }

        /// <summary>Designer layout: tasks stacked top to bottom, plus each data flow's own layout.</summary>
        private static string Layout(List<Tuple<string, int>> nodes, List<XmlElement> extra)
        {
            var doc = Xml.Parse("<Objects Version=\"8\"><Package design-time-name=\"Package\"><LayoutInfo>"
                                + "<GraphLayout xmlns=\"" + Xml.GraphNs + "\"/></LayoutInfo></Package></Objects>");
            var graph = Xml.ByTag(doc, "GraphLayout")[0];
            graph.SetAttribute("Capacity", Py.Str(Math.Max(4, nodes.Count)));
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = doc.CreateElement("NodeLayout", Xml.GraphNs);
                node.SetAttribute("Size", nodes[i].Item2 + ",42");
                node.SetAttribute("Id", nodes[i].Item1);
                node.SetAttribute("TopLeft", "40," + (30 + i * 90));
                graph.AppendChild(node);
            }
            foreach (var item in extra) doc.DocumentElement.AppendChild(doc.ImportNode(item, true));
            return Xml.ElementXml(doc.DocumentElement);
        }

        /// <summary>Append tasks chained by Success constraints (run in order, stop at the first failure).</summary>
        private static RenderedPackage Finish(XmlDocument doc, List<XmlElement> tasks, string packageName, string seed, List<XmlElement> layouts = null)
        {
            var root = doc.DocumentElement;
            var executables = Xml.Dts(doc, "Executables");
            foreach (var task in tasks) executables.AppendChild(task);
            root.AppendChild(executables);
            if (tasks.Count > 1)
            {
                var constraints = Xml.Dts(doc, "PrecedenceConstraints");
                for (var i = 1; i < tasks.Count; i++)
                {
                    var label = "Constraint " + i;
                    constraints.AppendChild(Xml.Dts(doc, "PrecedenceConstraint",
                        "refId", "Package.PrecedenceConstraints[" + label + "]", "CreationName", "",
                        "DTSID", Package.DeterministicGuid(seed, packageName, label), "From", tasks[i - 1].GetAttribute("DTS:refId"),
                        "LogicalAnd", "True", "ObjectName", label, "To", tasks[i].GetAttribute("DTS:refId")));
                }
                root.AppendChild(constraints);
            }
            var design = Xml.Dts(doc, "DesignTimeProperties");
            var nodes = tasks.Select(t => Tuple.Create(t.GetAttribute("DTS:refId"), Math.Max(200, 8 * t.GetAttribute("DTS:ObjectName").Length + 60))).ToList();
            design.AppendChild(doc.CreateCDataSection(Layout(nodes, layouts ?? new List<XmlElement>())));
            root.AppendChild(design);
            ValidateGroup(root, packageName);
            return new RenderedPackage(packageName, root.GetAttribute("DTS:DTSID"), root.GetAttribute("DTS:VersionGUID"), Xml.ToXml(doc));
        }

        /// <summary>The data flow layouts (TaskHost entries) from a rendered package's designer section.</summary>
        private static List<XmlElement> TaskLayouts(XmlElement root)
        {
            var items = new List<XmlElement>();
            foreach (var design in Xml.Children(root, "DesignTimeProperties"))
            {
                var text = Xml.Text(design);
                if (text.Length == 0) continue;
                XmlDocument parsed;
                try { parsed = Xml.Parse(text); }
                catch (XmlException ex) { throw new GeneratorException("Invalid template designer layout: " + ex.Message, ex); }
                items.AddRange(Xml.Children(parsed.DocumentElement)
                    .Where(n => !(n.LocalName == "Package" && n.GetAttribute("design-time-name") == "Package")));
            }
            return items;
        }

        private static void RenameTask(XmlElement root, string old, string renamed)
        {
            var oldRef = "Package\\" + old;
            var newRef = "Package\\" + renamed;
            var pattern = Py.Re(Regex.Escape(oldRef) + @"(?=$|[\\.\]}\s])");
            Package.Apply(root, s => pattern.Replace(s, m => newRef), new NodeSet());
            foreach (var el in Xml.Elements(root))
                if (el.GetAttribute("DTS:refId") == newRef) el.SetAttribute("DTS:ObjectName", renamed);
        }

        /// <summary>Render the reference flow for one table: (package root, top-level tasks, warnings).</summary>
        private static Tuple<XmlElement, List<XmlElement>, List<string>> TableTask(byte[] templateXml, string label, Table reference, Table table,
                                                                                 string packageName, string seed)
        {
            var rendered = Package.Render(templateXml, label, reference, table, packageName + "/" + table.LogicalName, seed);
            var root = Xml.Parse(rendered.Xml).DocumentElement;
            var tasks = Xml.Children(root, "Executables").SelectMany(c => Xml.Children(c, "Executable")).ToList();
            return Tuple.Create(root, tasks, rendered.Warnings);
        }

        /// <param name="first">an Execute SQL task (Create GUID Tables) that runs before the flows.</param>
        public static RenderedPackage RenderHarness(byte[] templateXml, string label, Table reference, string taskName, List<Table> tables,
                                                    string packageName, string seed, Dictionary<string, List<Tuple<string, string>>> dbColumns,
                                                    SqlTask first = null, string connectionId = null)
        {
            if (tables.Count == 0) throw new GeneratorException(packageName + ": no tables to migrate");
            var doc = BlankPackage(templateXml, label, reference, packageName, seed);
            var labels = TaskLabels(tables);
            var tasks = new List<XmlElement>();
            var layouts = new List<XmlElement>();
            var warnings = new List<string>();
            if (first != null) tasks.Add(SqlTaskElement(doc, first.Name, first.Sql, connectionId, packageName, seed));
            foreach (var original in tables)
            {
                var table = WithStagingExtras(original, reference, dbColumns);
                var rendered = TableTask(templateXml, label, reference, table.With(displayName: labels[table.LogicalName]), packageName, seed);
                var expected = "Migrate " + labels[table.LogicalName];
                if (rendered.Item2.Count != 1 || rendered.Item2[0].GetAttribute("DTS:ObjectName") != expected)
                    throw new GeneratorException(packageName + ": rendered flow for " + table.LogicalName + " is not named " + Py.Repr(expected)
                                                 + "; the reference task " + Py.Repr(taskName) + " must be named \"Migrate <display name>\"");
                CheckGuidColumns(rendered.Item2[0], table, dbColumns);
                SyncExternalColumns(rendered.Item2[0], dbColumns);
                tasks.Add((XmlElement)doc.ImportNode(rendered.Item2[0], true));
                layouts.AddRange(TaskLayouts(rendered.Item1));
                warnings.AddRange(rendered.Item3);
            }
            var result = Finish(doc, tasks, packageName, seed, layouts);
            return new RenderedPackage(result.PackageName, result.Dtsid, result.VersionGuid, result.Xml, warnings);
        }

        private static XmlElement SqlTaskElement(XmlDocument doc, string name, string sql, string connectionId, string packageName, string seed)
        {
            var task = Xml.Dts(doc, "Executable", "refId", "Package\\" + name, "CreationName", "Microsoft.ExecuteSQLTask",
                               "Description", "Execute SQL Task", "DTSID", Package.DeterministicGuid(seed, packageName, name),
                               "ExecutableType", "Microsoft.ExecuteSQLTask", "LocaleID", "-1", "ObjectName", name, "ThreadHint", "0");
            task.AppendChild(Xml.Dts(doc, "Variables"));
            var data = Xml.Dts(doc, "ObjectData");
            var body = doc.CreateElement("SQLTask", "SqlTaskData", Xml.SqlTaskNs);
            body.SetAttribute("xmlns:SQLTask", Xml.SqlTaskNs);
            Xml.SetNs(body, "SQLTask", "Connection", Xml.SqlTaskNs, connectionId);
            Xml.SetNs(body, "SQLTask", "SqlStatementSource", Xml.SqlTaskNs, sql);
            data.AppendChild(body);
            task.AppendChild(data);
            return task;
        }

        /// <summary>A package of Execute SQL tasks, then one Stage task per stage item (cloned from the template).</summary>
        public static RenderedPackage RenderSqlPackage(byte[] templateXml, string label, Table reference, string packageName, string seed,
                                                       List<SqlTask> tasks, string connectionId, StageTemplate stageTemplate = null,
                                                       List<StageItem> stageItems = null)
        {
            var doc = BlankPackage(templateXml, label, reference, packageName, seed);
            var elements = tasks.Select(t => SqlTaskElement(doc, t.Name, t.Sql, connectionId, packageName, seed)).ToList();
            var managers = new List<XmlElement>();
            foreach (var item in stageItems ?? new List<StageItem>())
            {
                var pair = Stage.Task(doc, stageTemplate, item.TaskName, item.ManagerName, item.SqlPath, packageName, seed);
                elements.Add(pair.Item1);
                managers.Add(pair.Item2);
            }
            if (managers.Count > 0)
            {
                var collection = Xml.Dts(doc, "ConnectionManagers");
                foreach (var manager in managers) collection.AppendChild(manager);
                var root = doc.DocumentElement;
                // Package connection managers follow the package properties, as SSIS writes them.
                var after = Xml.Children(root).Where(n => n.LocalName == "Property").ToList();
                var anchor = after.Count > 0 ? after.Last().NextSibling : root.FirstChild;
                while (anchor != null && anchor.NodeType != XmlNodeType.Element) anchor = anchor.NextSibling;
                root.InsertBefore(collection, anchor);
            }
            return Finish(doc, elements, packageName, seed);
        }

        private static readonly Dictionary<string, string> SqlTypes = new Dictionary<string, string>
        {
            { "i1", "SMALLINT" }, { "i2", "SMALLINT" }, { "i4", "INT" }, { "i8", "BIGINT" }, { "ui1", "TINYINT" }, { "bool", "BIT" },
            { "guid", "UNIQUEIDENTIFIER" }, { "nText", "NVARCHAR(MAX)" }, { "text", "VARCHAR(MAX)" }, { "r4", "REAL" },
            { "r8", "FLOAT" }, { "cy", "MONEY" }, { "dbDate", "DATE" }, { "dbTimeStamp", "DATETIME" },
        };

        /// <summary>CREATE TABLE for the error table the reference flow writes to, from the columns its
        /// error destination declares; null if the reference has no error destination.</summary>
        public static string ErrorTableSql(byte[] templateXml)
        {
            var doc = Xml.Parse(templateXml);
            foreach (var component in Xml.ByTag(doc, "component"))
            {
                var props = Xml.Props(component);
                if (!props.Has("OpenRowset")) continue;
                var rowset = props.Text("OpenRowset").Replace(" ", "").ToLowerInvariant();
                if (rowset != ErrorTable.ToLowerInvariant() && rowset != "dbo.error" && rowset != "[error]" && rowset != "error") continue;
                var mapped = new HashSet<string>(Xml.ByTag(component, "inputColumn").Select(i => i.GetAttribute("externalMetadataColumnId")), StringComparer.Ordinal);
                var lines = new List<string>();
                foreach (var ext in Xml.ByTag(component, "externalMetadataColumn"))
                {
                    var name = ext.GetAttribute("name");
                    var kind = ext.GetAttribute("dataType");
                    var length = ext.GetAttribute("length");
                    var scale = ext.GetAttribute("scale");
                    string sql;
                    if (kind == "wstr") sql = "NVARCHAR(" + (length.Length > 0 ? length : "4000") + ")";
                    else if (kind == "str") sql = "VARCHAR(" + (length.Length > 0 ? length : "8000") + ")";
                    else if (kind == "dbTimeStamp2") sql = "DATETIME2(" + (scale.Length > 0 ? scale : "7") + ")";
                    else if (kind == "numeric")
                        sql = "DECIMAL(" + (ext.GetAttribute("precision").Length > 0 ? ext.GetAttribute("precision") : "18") + "," + (scale.Length > 0 ? scale : "0") + ")";
                    else sql = SqlTypes.ContainsKey(kind) ? SqlTypes[kind] : "NVARCHAR(MAX)";
                    var unmapped = !mapped.Contains(ext.GetAttribute("refId"));
                    if (unmapped && lines.Count == 0 && (kind == "i4" || kind == "i8")) sql += " IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Error] PRIMARY KEY";
                    else if (unmapped && kind == "dbTimeStamp2" && name.ToLowerInvariant().EndsWith("utc", StringComparison.Ordinal))
                        sql += " NOT NULL CONSTRAINT [DF_Error_" + name + "] DEFAULT (SYSUTCDATETIME())";
                    else sql += " NULL";
                    lines.Add("        [" + name + "] " + sql);
                }
                return "SET ANSI_NULLS ON;\nSET QUOTED_IDENTIFIER ON;\n\n"
                       + "IF OBJECT_ID(N'" + ErrorTable + "', N'U') IS NULL\nBEGIN\n    CREATE TABLE " + ErrorTable + "(\n"
                       + string.Join(",\n", lines) + "\n    );\nEND\n";
            }
            return null;
        }

        private static readonly Regex CreateTable = Py.Re(@"CREATE\s+TABLE\s+((?:\[?\w+\]?\.)?\[?(\w+)\]?)\s*\((.*?)\n\s*\)\s*;",
                                                          RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex ColumnDef = Py.Re(@"^\s*\[?(\w+)\]?\s+([A-Za-z0-9]+(?:\s*\([^)]*\))?)", RegexOptions.Multiline);
        private static readonly Regex LineComment = Py.Re(@"--[^\n]*");

        /// <summary>{table name (lower): [(column, SQL type)]} for every CREATE TABLE in a scaffolder script.</summary>
        public static Dictionary<string, List<Tuple<string, string>>> ScriptTables(string sql)
        {
            var tables = new Dictionary<string, List<Tuple<string, string>>>(StringComparer.Ordinal);
            foreach (Match match in CreateTable.Matches(sql))
            {
                var body = LineComment.Replace(match.Groups[3].Value, m => "");
                tables[match.Groups[2].Value.ToLowerInvariant()] = ColumnDef.Matches(body).Cast<Match>()
                    .Where(m => !new[] { "CONSTRAINT", "PRIMARY", "INDEX" }.Contains(m.Groups[1].Value.ToUpperInvariant()))
                    .Select(m => Tuple.Create(m.Groups[1].Value, m.Groups[2].Value)).ToList();
            }
            return tables;
        }

        private static bool TryType(string sqlType, out SsisType type)
        {
            try { type = Metadata.SsisTypeFor(sqlType); return true; }
            catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is OverflowException) { type = null; return false; }
        }

        /// <summary>Add the staging-only columns the reference flow reads (scaffolder boilerplate such as
        /// statecode, which is not a manifest column) from the table's CREATE TABLE script.</summary>
        public static Table WithStagingExtras(Table table, Table reference, Dictionary<string, List<Tuple<string, string>>> dbColumns)
        {
            List<Tuple<string, string>> staged;
            if (!dbColumns.TryGetValue(Metadata.ObjectName(table.StagingTable).ToLowerInvariant(), out staged)) return table;
            var have = new HashSet<string>(table.Columns.Select(c => c.Name.ToLowerInvariant()));
            var read = new HashSet<string>(reference.Columns.Select(c => c.Name.ToLowerInvariant()));
            var extras = new List<Column>();
            foreach (var col in staged)
            {
                if (have.Contains(col.Item1.ToLowerInvariant()) || !read.Contains(col.Item1.ToLowerInvariant())) continue;
                SsisType kind;
                if (!TryType(col.Item2, out kind)) continue;
                extras.Add(new Column { Name = col.Item1, SqlType = col.Item2, DataverseType = "", SsisType = kind });
            }
            return extras.Count > 0 ? table.With(columns: table.Columns.Concat(extras).ToList()) : table;
        }

        /// <summary>Fail if the flow writes GUID table columns the scaffolder's GUID script does not create.</summary>
        private static void CheckGuidColumns(XmlElement root, Table table, Dictionary<string, List<Tuple<string, string>>> dbColumns)
        {
            List<Tuple<string, string>> created;
            if (!dbColumns.TryGetValue(Metadata.ObjectName(table.GuidTable).ToLowerInvariant(), out created) || created.Count == 0) return;
            var needed = new List<string>();
            foreach (var component in Xml.ByTag(root, "component"))
            {
                var props = Xml.Props(component);
                if (props.Has("OpenRowset") && Package.SameSqlObject(props.Text("OpenRowset"), table.GuidTable))
                {
                    var ext = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var e in Xml.ByTag(component, "externalMetadataColumn")) ext[e.GetAttribute("refId")] = e.GetAttribute("name");
                    foreach (var i in Xml.ByTag(component, "inputColumn"))
                    {
                        string n;
                        if (ext.TryGetValue(i.GetAttribute("externalMetadataColumnId"), out n)) needed.Add(n);
                    }
                }
                var parsed = props.Has("SqlCommand") ? Package.ParseGuidUpdate(props.Text("SqlCommand")) : null;
                if (parsed != null && Package.SameSqlObject(parsed.Item1, table.GuidTable))
                {
                    needed.AddRange(parsed.Item2);
                    needed.Add(parsed.Item3);
                }
            }
            var have = new HashSet<string>(created.Select(c => c.Item1.ToLowerInvariant()));
            var missing = Py.Sorted(needed.Where(n => !have.Contains(n.ToLowerInvariant())).Distinct());
            if (missing.Count > 0)
                throw new GeneratorException(table.LogicalName + ": the reference flow writes " + string.Join(", ", missing) + " to "
                                             + table.GuidTable + ", but the scaffolder's GUID table script does not create "
                                             + (missing.Count == 1 ? "it" : "them") + "; regenerate the scripts with a scaffolder "
                                             + "version that does, or stop writing them in the reference flow");
        }

        /// <summary>Add the table columns a flow does not use to the cached external metadata of OLE DB
        /// sources and destinations that name a table, so SSIS sees them in sync with the database.</summary>
        private static void SyncExternalColumns(XmlElement root, Dictionary<string, List<Tuple<string, string>>> dbColumns)
        {
            foreach (var component in Xml.ByTag(root, "component"))
            {
                var props = Xml.Props(component);
                var cls = component.GetAttribute("componentClassID").ToLowerInvariant();
                var mode = props.Has("AccessMode") ? props.Text("AccessMode") : "0";
                if (!cls.Contains("oledb") || !props.Has("OpenRowset") || mode == "2") continue;   // SQL command sources describe their query
                List<Tuple<string, string>> columns;
                if (!dbColumns.TryGetValue(Metadata.ObjectName(props.Text("OpenRowset")).ToLowerInvariant(), out columns) || columns.Count == 0) continue;
                var holder = Xml.ByTag(component, cls.Contains("source") ? "output" : "input").FirstOrDefault(io => io.GetAttribute("isErrorOut") != "true");
                var collections = holder != null ? Xml.ByTag(holder, "externalMetadataColumns") : new List<XmlElement>();
                if (collections.Count == 0) continue;
                var collection = collections[0];
                var existing = Xml.Children(collection);
                if (existing.Count == 0) continue;
                var have = new HashSet<string>(existing.Select(c => c.GetAttribute("name").ToLowerInvariant()));
                var refId = existing[0].GetAttribute("refId");
                var at = refId.LastIndexOf(".ExternalColumns[", StringComparison.Ordinal);
                var prefix = at >= 0 ? refId.Substring(0, at) : refId;
                foreach (var col in columns)
                {
                    if (have.Contains(col.Item1.ToLowerInvariant())) continue;
                    SsisType kind;
                    if (!TryType(col.Item2, out kind)) continue;
                    var el = root.OwnerDocument.CreateElement("externalMetadataColumn");
                    el.SetAttribute("refId", prefix + ".ExternalColumns[" + col.Item1 + "]");
                    var attrs = new[] { Tuple.Create("codePage", kind.CodePage), Tuple.Create("dataType", (int?)null), Tuple.Create("length", kind.Length),
                                        Tuple.Create("precision", kind.Precision), Tuple.Create("scale", kind.Scale) };
                    foreach (var a in attrs)
                    {
                        if (a.Item1 == "dataType") el.SetAttribute("dataType", kind.DataType);
                        else if (a.Item2.HasValue) el.SetAttribute(a.Item1, Py.Str(a.Item2.Value));
                    }
                    el.SetAttribute("name", col.Item1);
                    collection.AppendChild(el);
                }
            }
        }

        /// <summary>Remove the statements for unselected tables from a scaffolder script. Statements are
        /// blank-line separated blocks; a block is dropped when it names only unselected tables.</summary>
        public static string FilterSql(string sql, List<Table> keep, List<Table> drop)
        {
            if (drop.Count == 0) return sql;
            Func<List<Table>, List<Regex>> names = tables => tables
                .SelectMany(t => new[] { Metadata.ObjectName(t.StagingTable), Metadata.ObjectName(t.GuidTable) })
                .Select(n => Py.Re("(?<![A-Za-z0-9_])" + Regex.Escape(n) + "(?![A-Za-z0-9_])", RegexOptions.IgnoreCase)).ToList();
            var kept = names(keep);
            var dropped = names(drop);
            var blocks = Py.Re(@"\n\s*\n").Split(sql.Replace("\r\n", "\n"));
            var output = blocks.Where(b => !(dropped.Any(p => p.IsMatch(b)) && !kept.Any(p => p.IsMatch(b))));
            return string.Join("\n\n", output) + "\n";
        }

        // ---- deferred lookup updates --------------------------------------------------------

        /// <summary>The table restricted to what the deferred pass reads: legacy key, record id and the deferred lookups.</summary>
        public static Table DeferredView(Table table, List<Column> deferred)
        {
            var wanted = new HashSet<string>(new[] { table.MatchKeys[0].ToLowerInvariant(), table.PrimaryId.ToLowerInvariant() }
                                             .Concat(deferred.Select(c => c.Name.ToLowerInvariant())));
            return table.With(columns: table.Columns.Where(c => wanted.Contains(c.Name.ToLowerInvariant())).ToList());
        }

        public static string DeferredSql(Table table, Table view)
        {
            var key = table.MatchKeys[0];
            var pid = table.PrimaryId;
            var columns = view.Columns.Select(c => c.Name.ToLowerInvariant() == pid.ToLowerInvariant()
                ? "    CAST(g.[" + pid + "] AS " + (string.IsNullOrEmpty(c.SqlType) ? "NVARCHAR(100)" : c.SqlType) + ") AS [" + pid + "]"
                : "    s.[" + c.Name + "]");
            return "SELECT\n" + string.Join(",\n", columns) + "\nFROM " + table.StagingTable + " s\n"
                   + "INNER JOIN " + table.GuidTable + " g ON g.[" + key + "] = s.[" + key + "]\n"
                   + "WHERE g.[" + pid + "] IS NOT NULL";
        }

        /// <summary>Drop the create destination and everything fed by it, and whatever the update
        /// destination's Default Output feeds (GUID table sync); every deferred row already has a
        /// record id and a GUID table row, so only the update and its error handling are used.</summary>
        private static void RemoveCreateBranch(XmlElement root)
        {
            var components = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
            foreach (var c in Xml.ByTag(root, "component")) components[c.GetAttribute("refId")] = c;
            var doomed = new HashSet<string>(components.Where(kv =>
            {
                var p = Xml.Props(kv.Value);
                return p.Has("DestinationEntity") && p.Has("ActionType") && (p.Text("ActionType").ToLowerInvariant() == "1" || p.Text("ActionType").ToLowerInvariant() == "create");
            }).Select(kv => kv.Key), StringComparer.Ordinal);
            var outputs = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
            foreach (var o in Xml.ByTag(root, "output")) outputs[o.GetAttribute("refId")] = o;
            var paths = Xml.ByTag(root, "path");
            foreach (var path in paths)
            {
                var start = path.GetAttribute("startId");
                XmlElement owner, output;
                components.TryGetValue(Before(start, ".Outputs["), out owner);
                outputs.TryGetValue(start, out output);
                if (owner != null && Xml.Props(owner).Has("DestinationEntity") && output != null && output.GetAttribute("isErrorOut") != "true")
                    doomed.Add(Before(path.GetAttribute("endId"), ".Inputs["));
            }
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var path in paths)
                {
                    var start = Before(path.GetAttribute("startId"), ".Outputs[");
                    var end = Before(path.GetAttribute("endId"), ".Inputs[");
                    if (doomed.Contains(start) && !doomed.Contains(end)) { doomed.Add(end); changed = true; }
                }
            }
            var removedPaths = new List<string>();
            foreach (var path in paths)
            {
                var start = Before(path.GetAttribute("startId"), ".Outputs[");
                var end = Before(path.GetAttribute("endId"), ".Inputs[");
                if (doomed.Contains(start) || doomed.Contains(end))
                {
                    removedPaths.Add(path.GetAttribute("refId"));
                    path.ParentNode.RemoveChild(path);
                }
            }
            foreach (var reference in doomed)
            {
                var component = components[reference];
                component.ParentNode.RemoveChild(component);
            }
            foreach (var design in Xml.Children(root, "DesignTimeProperties"))
            {
                var text = Xml.Text(design);
                if (text.Length == 0) continue;
                foreach (var reference in doomed)
                    text = Py.Re(@"<NodeLayout\b[^>]*?Id=""" + Regex.Escape(reference) + @"""[^>]*/>", RegexOptions.Singleline).Replace(text, m => "");
                foreach (var reference in removedPaths)
                    text = Py.Re(@"<EdgeLayout\b[^>]*?Id=""" + Regex.Escape(reference) + @""".*?</EdgeLayout>", RegexOptions.Singleline).Replace(text, m => "");
                Xml.ClearChildren(design);
                design.AppendChild(root.OwnerDocument.CreateCDataSection(text));
            }
        }

        private static string Before(string value, string marker)
        {
            var at = value.IndexOf(marker, StringComparison.Ordinal);
            return at >= 0 ? value.Substring(0, at) : value;
        }

        /// <summary>One "Update &lt;Table&gt; Lookups" data flow per cycle member: the reference flow's update
        /// branch reading the record id (from the GUID table) and the deferred lookups.</summary>
        public static RenderedPackage RenderDeferred(byte[] templateXml, string label, Table reference, List<DeferredPass> passes, string packageName,
                                                     string seed, Dictionary<string, List<Tuple<string, string>>> dbColumns,
                                                     Func<DeferredPass, Table, string> sourceSql = null)
        {
            var doc = BlankPackage(templateXml, label, reference, packageName, seed);
            var labels = TaskLabels(passes.Select(p => p.Table));
            var tasks = new List<XmlElement>();
            var layouts = new List<XmlElement>();
            // Strip the create and GUID table branches before rendering: they carry columns (primary
            // name, statecode) a deferred view does not read.
            var updateOnly = Xml.Parse(templateXml);
            RemoveCreateBranch(updateOnly.DocumentElement);
            var updateXml = Xml.ToXml(updateOnly);
            foreach (var p in passes)
            {
                var view = DeferredView(p.Table, p.UpdateColumns).With(displayName: labels[p.Table.LogicalName]);
                var rendered = TableTask(updateXml, label, reference, view, packageName, seed);
                var root = rendered.Item1;
                RenameTask(root, "Migrate " + labels[p.Table.LogicalName], "Update " + labels[p.Table.LogicalName] + " Lookups");
                var task = rendered.Item2[0];
                foreach (var component in Xml.ByTag(task, "component"))
                {
                    if (!component.GetAttribute("componentClassID").ToLowerInvariant().Contains("oledbsource")) continue;
                    var props = Xml.Props(component);
                    var sql = sourceSql != null ? sourceSql(p, view) : DeferredSql(p.Table, view);
                    Xml.SetText(props["AccessMode"], "2");
                    Xml.SetText(props["SqlCommand"], sql);
                }
                SyncExternalColumns(task, dbColumns);
                tasks.Add((XmlElement)doc.ImportNode(task, true));
                layouts.AddRange(TaskLayouts(root));
            }
            return Finish(doc, tasks, packageName, seed, layouts);
        }

        // ---- shared package checks ------------------------------------------------------------

        public static void ValidateGroup(XmlElement root, string label)
        {
            Package.ValidateReferences(root, label);
            var elements = Xml.Elements(root);
            foreach (var attribute in new[] { "DTS:DTSID", "DTS:refId", "refId" })
            {
                var values = elements.Where(e => e.HasAttribute(attribute)).Select(e => e.GetAttribute(attribute)).ToList();
                if (values.Count != new HashSet<string>(values, StringComparer.Ordinal).Count)
                    throw new GeneratorException("Package " + label + ": duplicate " + attribute);
            }
            // Constraints must remain inside their own table scope, not merely resolve globally.
            foreach (var collection in elements.Where(e => e.LocalName == "PrecedenceConstraints"))
            {
                var scope = collection.ParentNode;
                var tasks = Xml.Children(scope, "Executables");
                var identities = tasks.Count > 0
                    ? new HashSet<string>(Xml.Children(tasks[0]).Select(e => e.GetAttribute("DTS:refId")), StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                foreach (var constraint in Xml.Children(collection))
                    if (!identities.Contains(constraint.GetAttribute("DTS:From")) || !identities.Contains(constraint.GetAttribute("DTS:To")))
                        throw new GeneratorException("Package " + label + ": precedence constraint crosses a scope");
            }
        }
    }
}
