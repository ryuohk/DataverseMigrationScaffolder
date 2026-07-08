using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DataverseMigrationScaffolder.Core
{
    /// <summary>
    /// Emits harness scripts with STRICT one-tier-per-file batching (tier 0 = no
    /// dependencies, tier n = deepest dependency chain of length n), matching SSIS
    /// packages organized by dependency layer. A tier larger than Settings.BatchSize
    /// is split across several files, but tiers are never mixed within one file.
    ///
    /// Output options (settings): staging tables, guid tables, drop-and-recreate vs
    /// create-if-missing per kind, TRUNCATE for staging, guarded legacyid indexes,
    /// teardown script, manifest CSV.
    /// </summary>
    public class ScriptGenerator
    {
        private readonly ToolSettings _settings;

        public ScriptGenerator(ToolSettings settings)
        {
            _settings = settings;
        }

        private class TierChunk
        {
            public int TierIndex;
            public int Part;            // 1-based
            public int TotalParts;
            public List<TableModel> Tables;
        }

        public GenerationResult Generate(IList<TableModel> selectedTables)
        {
            var result = new GenerationResult();
            var droppedEdges = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var tiers = DependencySorter.SortIntoTiers(selectedTables, result.Warnings, droppedEdges);
            result.OrderedTables.AddRange(tiers.SelectMany(t => t).Select(t => t.LogicalName));

            var batchSize = Math.Max(1, _settings.BatchSize);
            var chunks = new List<TierChunk>();
            for (var tierIndex = 0; tierIndex < tiers.Count; tierIndex++)
            {
                var tier = tiers[tierIndex];
                var parts = (tier.Count + batchSize - 1) / batchSize;
                for (var p = 0; p < parts; p++)
                {
                    chunks.Add(new TierChunk
                    {
                        TierIndex = tierIndex,
                        Part = p + 1,
                        TotalParts = parts,
                        Tables = tier.Skip(p * batchSize).Take(batchSize).ToList()
                    });
                }
            }

            if (_settings.GenerateStaging)
            {
                for (var i = 0; i < chunks.Count; i++)
                {
                    var file = new GeneratedFile
                    {
                        FileName = string.Format("{0:00}_create_staging.sql", i + 1),
                        Content = BuildStagingScript(chunks[i], i + 1, chunks.Count, result.Warnings),
                        Description = DescribeChunk(chunks[i])
                    };
                    file.Tables.AddRange(chunks[i].Tables.Select(t => t.LogicalName));
                    result.Files.Add(file);
                }
            }

            if (_settings.GenerateGuid)
            {
                for (var i = 0; i < chunks.Count; i++)
                {
                    var file = new GeneratedFile
                    {
                        FileName = string.Format("{0:00}_create_guid.sql", i + 1),
                        Content = BuildGuidScript(chunks[i], i + 1, chunks.Count, result.Warnings),
                        Description = DescribeChunk(chunks[i])
                    };
                    file.Tables.AddRange(chunks[i].Tables.Select(t => t.LogicalName));
                    result.Files.Add(file);
                }
            }

            if (_settings.GenerateTruncateScript)
            {
                result.Files.Add(BuildTruncate(chunks));
            }

            if (_settings.GenerateTeardown)
            {
                result.Files.Add(BuildTeardown(chunks));
            }

            if (_settings.GenerateDataDictionary)
            {
                result.Files.Add(BuildDataDictionary(chunks, droppedEdges));
            }

            if (_settings.GenerateMermaid)
            {
                result.Files.Add(BuildMermaid(tiers, droppedEdges));
            }

            return result;
        }

        private static string DescribeChunk(TierChunk chunk)
        {
            return string.Format("tier {0}{1}, {2} table{3}",
                chunk.TierIndex,
                chunk.TotalParts > 1 ? string.Format(" pt {0}/{1}", chunk.Part, chunk.TotalParts) : "",
                chunk.Tables.Count,
                chunk.Tables.Count == 1 ? "" : "s");
        }

        private string Header(string kind, TierChunk chunk, int fileNumber, int totalFiles, List<string> warnings)
        {
            var sb = new StringBuilder();
            sb.AppendLine("/*");
            sb.AppendLine(string.Format(" * {0} tables - file {1} of {2}", kind, fileNumber, totalFiles));
            sb.AppendLine(string.Format(" * Dependency tier {0}{1}: every table here depends only on tables from earlier tiers.",
                chunk.TierIndex,
                chunk.TotalParts > 1 ? string.Format(" (part {0} of {1})", chunk.Part, chunk.TotalParts) : ""));
            sb.AppendLine(string.Format(" * Generated by Dataverse Migration Scaffolder on {0:yyyy-MM-dd HH:mm}", DateTime.Now));
            sb.AppendLine(" * Tables:");
            foreach (var t in chunk.Tables)
            {
                sb.AppendLine(" *   " + t.SchemaName);
            }
            if (warnings.Count > 0)
            {
                sb.AppendLine(" * Notes:");
                foreach (var w in warnings)
                {
                    sb.AppendLine(" *   " + w);
                }
            }
            sb.AppendLine(" */");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- staging

        private string BuildStagingScript(TierChunk chunk, int fileNumber, int totalFiles, List<string> warnings)
        {
            var sb = new StringBuilder();
            sb.Append(Header("Staging", chunk, fileNumber, totalFiles, warnings));
            sb.AppendLine("SET ANSI_NULLS ON;");
            sb.AppendLine("SET QUOTED_IDENTIFIER ON;");

            foreach (var table in chunk.Tables)
            {
                EmitStagingTable(sb, table);
            }

            return sb.ToString();
        }

        private void EmitStagingTable(StringBuilder sb, TableModel table)
        {
            var fullName = string.Format("[{0}].[{1}{2}]", _settings.SchemaName, _settings.StagingPrefix, table.SchemaName);

            // definition + optional trailing comment (placed after the comma)
            var lines = new List<Tuple<string, string>>();

            foreach (var col in table.Columns)
            {
                lines.Add(Tuple.Create(
                    string.Format("    [{0}] {1}", col.Name, col.SqlType),
                    ColumnComment(col)));
            }

            // Fixed boilerplate block (always last, in this order). These are standard
            // Dataverse concepts valid for any table; everything else must exist in metadata.
            lines.Add(Tuple.Create("    [overriddencreatedon] DATETIME2(7)", (string)null));
            lines.Add(Tuple.Create("    [ownerid] NVARCHAR(100)", (string)null));
            lines.Add(Tuple.Create("    [owneridtype] NVARCHAR(100)", (string)null));
            lines.Add(Tuple.Create("    [statecode] INT", (string)null));

            if (_settings.StagingDropRecreate)
            {
                sb.AppendLine(string.Format("DROP TABLE IF EXISTS {0};", fullName));
                sb.AppendLine("GO");
                sb.AppendLine(string.Format("CREATE TABLE {0}(", fullName));
                AppendColumnLines(sb, lines, "");
                sb.AppendLine(") ON [PRIMARY];");
                sb.AppendLine("GO");
            }
            else
            {
                sb.AppendLine(string.Format("IF OBJECT_ID(N'{0}', N'U') IS NULL", fullName));
                sb.AppendLine("BEGIN");
                sb.AppendLine(string.Format("    CREATE TABLE {0}(", fullName));
                AppendColumnLines(sb, lines, "    ");
                sb.AppendLine("    ) ON [PRIMARY];");
                sb.AppendLine("END");
                sb.AppendLine("GO");
            }

            if (_settings.IndexLegacyIdColumns)
            {
                EmitLegacyIdIndexes(sb, fullName, _settings.StagingPrefix + table.SchemaName, table);
            }
        }

        private static void AppendColumnLines(StringBuilder sb, List<Tuple<string, string>> lines, string indent)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                sb.Append(indent).Append(lines[i].Item1);
                if (i < lines.Count - 1) sb.Append(",");
                if (!string.IsNullOrEmpty(lines[i].Item2)) sb.Append("    -- " + lines[i].Item2);
                sb.AppendLine();
            }
        }

        /// <summary>Documents what the tool detected, so lookup handling is verifiable in the output.</summary>
        private static string ColumnComment(SqlColumn col)
        {
            if (col.IsLookup && col.Targets != null && col.Targets.Length > 0)
            {
                var shown = col.Targets.Take(5).ToArray();
                var suffix = col.Targets.Length > 5 ? string.Format(", ... (+{0} more)", col.Targets.Length - 5) : "";
                return (col.IsPolymorphic ? "polymorphic lookup: " : "lookup: ") + string.Join(", ", shown) + suffix;
            }
            if (col.IsLookup) return "lookup (no targets reported)";
            if (col.IsTypeCompanion) return "target table name for the polymorphic lookup above";
            return null;
        }

        // ---------------------------------------------------------------- guid

        private string BuildGuidScript(TierChunk chunk, int fileNumber, int totalFiles, List<string> warnings)
        {
            var sb = new StringBuilder();
            sb.Append(Header("GUID mapping", chunk, fileNumber, totalFiles, warnings));
            sb.AppendLine("GO");
            sb.AppendLine("SET ANSI_NULLS ON;");
            sb.AppendLine("SET QUOTED_IDENTIFIER ON;");
            sb.AppendLine("GO");

            foreach (var table in chunk.Tables)
            {
                EmitGuidTable(sb, table);
            }

            return sb.ToString();
        }

        private void EmitGuidTable(StringBuilder sb, TableModel table)
        {
            var fullName = string.Format("[{0}].[{1}{2}]", _settings.SchemaName, _settings.GuidPrefix, table.SchemaName);

            var lines = new List<string>();

            // 1. Unique identifier column (VARCHAR(100), matching the existing harness).
            var idCol = table.Columns.FirstOrDefault(c => c.IsPrimaryId);
            var idName = idCol != null ? idCol.Name : table.PrimaryIdAttribute;
            lines.Add(string.Format("        [{0}] VARCHAR(100) NULL", idName));

            // 2. Primary name column.
            var nameCol = table.Columns.FirstOrDefault(c => c.IsPrimaryName);
            if (nameCol != null)
            {
                lines.Add(string.Format("        [{0}] {1} NULL", nameCol.Name, nameCol.SqlType));
            }
            else if (!string.IsNullOrEmpty(table.PrimaryNameAttribute))
            {
                lines.Add(string.Format("        [{0}] NVARCHAR(100) NULL", table.PrimaryNameAttribute));
            }

            // 3. Match-key column(s) (configurable suffix, default *legacyid), only if the
            //    table actually has one in Dataverse.
            foreach (var col in table.Columns.Where(c => _settings.IsMatchKey(c.Name)))
            {
                lines.Add(string.Format("        [{0}] {1} NULL", col.Name, col.SqlType));
            }

            // 4. Lookup columns (plus polymorphic type companions), alphabetical.
            foreach (var col in table.Columns.Where(c => c.IsLookup || c.IsTypeCompanion))
            {
                lines.Add(string.Format("        [{0}] NVARCHAR(100) NULL", col.Name));
            }

            if (_settings.GuidDropRecreate)
            {
                sb.AppendLine(string.Format("DROP TABLE IF EXISTS {0};", fullName));
                sb.AppendLine("GO");
                sb.AppendLine(string.Format("CREATE TABLE {0}(", fullName));
                sb.AppendLine(string.Join("," + Environment.NewLine, lines));
                sb.AppendLine(");");
                sb.AppendLine("GO");
            }
            else
            {
                sb.AppendLine(string.Format("IF OBJECT_ID(N'{0}', N'U') IS NULL", fullName));
                sb.AppendLine("BEGIN");
                sb.AppendLine(string.Format("    CREATE TABLE {0}(", fullName));
                sb.AppendLine(string.Join("," + Environment.NewLine, lines));
                sb.AppendLine("    );");
                sb.AppendLine("END");
                sb.AppendLine("GO");
            }

            if (_settings.IndexLegacyIdColumns)
            {
                EmitLegacyIdIndexes(sb, fullName, _settings.GuidPrefix + table.SchemaName, table);
            }
        }

        // ---------------------------------------------------------------- indexes

        private void EmitLegacyIdIndexes(StringBuilder sb, string fullName, string bareName, TableModel table)
        {
            foreach (var col in table.Columns.Where(c => _settings.IsMatchKey(c.Name)))
            {
                var indexName = string.Format("IX_{0}_{1}", bareName, col.Name);
                sb.AppendLine(string.Format(
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'{0}' AND [object_id] = OBJECT_ID(N'{1}'))",
                    indexName, fullName));
                sb.AppendLine(string.Format("    CREATE NONCLUSTERED INDEX [{0}] ON {1}([{2}]);", indexName, fullName, col.Name));
                sb.AppendLine("GO");
            }
        }

        // ---------------------------------------------------------------- teardown

        private GeneratedFile BuildTeardown(List<TierChunk> chunks)
        {
            var ordered = chunks.SelectMany(c => c.Tables).ToList();
            ordered.Reverse();   // drop dependents before their targets, cosmetically

            var sb = new StringBuilder();
            sb.AppendLine("/*");
            sb.AppendLine(" * Harness teardown - drops all selected STAGING tables (reverse dependency order).");
            sb.AppendLine(" * GUID mapping table drops are included but COMMENTED OUT: they hold accumulated");
            sb.AppendLine(" * legacy-to-Dataverse mappings. Uncomment only if you really mean to lose them.");
            sb.AppendLine(string.Format(" * Generated by Dataverse Migration Scaffolder on {0:yyyy-MM-dd HH:mm}", DateTime.Now));
            sb.AppendLine(" */");
            sb.AppendLine("SET ANSI_NULLS ON;");
            sb.AppendLine("SET QUOTED_IDENTIFIER ON;");
            sb.AppendLine();
            sb.AppendLine("-- Staging tables");
            foreach (var table in ordered)
            {
                sb.AppendLine(string.Format("DROP TABLE IF EXISTS [{0}].[{1}{2}];", _settings.SchemaName, _settings.StagingPrefix, table.SchemaName));
            }
            sb.AppendLine("GO");
            sb.AppendLine();
            sb.AppendLine("-- GUID mapping tables (uncomment to drop accumulated mappings)");
            foreach (var table in ordered)
            {
                sb.AppendLine(string.Format("-- DROP TABLE IF EXISTS [{0}].[{1}{2}];", _settings.SchemaName, _settings.GuidPrefix, table.SchemaName));
            }

            var file = new GeneratedFile
            {
                FileName = "teardown.sql",
                Content = sb.ToString(),
                Description = "drops staging tables"
            };
            file.Tables.AddRange(ordered.Select(t => t.LogicalName));
            return file;
        }

        // ---------------------------------------------------------------- truncate

        private GeneratedFile BuildTruncate(List<TierChunk> chunks)
        {
            var ordered = chunks.SelectMany(c => c.Tables).ToList();
            ordered.Reverse();   // dependents before their targets, matching teardown

            var sb = new StringBuilder();
            sb.AppendLine("/*");
            sb.AppendLine(" * Harness reset - truncates all selected STAGING tables (reverse dependency order).");
            sb.AppendLine(" * GUID mapping table truncates are included but COMMENTED OUT: they hold accumulated");
            sb.AppendLine(" * legacy-to-Dataverse mappings. Uncomment only if you really mean to lose them.");
            sb.AppendLine(string.Format(" * Generated by Dataverse Migration Scaffolder on {0:yyyy-MM-dd HH:mm}", DateTime.Now));
            sb.AppendLine(" */");
            sb.AppendLine("SET ANSI_NULLS ON;");
            sb.AppendLine("SET QUOTED_IDENTIFIER ON;");
            sb.AppendLine();
            sb.AppendLine("-- Staging tables");
            foreach (var table in ordered)
            {
                sb.AppendLine(string.Format("TRUNCATE TABLE [{0}].[{1}{2}];", _settings.SchemaName, _settings.StagingPrefix, table.SchemaName));
            }
            sb.AppendLine("GO");
            sb.AppendLine();
            sb.AppendLine("-- GUID mapping tables (uncomment to empty accumulated mappings)");
            foreach (var table in ordered)
            {
                sb.AppendLine(string.Format("-- TRUNCATE TABLE [{0}].[{1}{2}];", _settings.SchemaName, _settings.GuidPrefix, table.SchemaName));
            }

            var file = new GeneratedFile
            {
                FileName = "truncate.sql",
                Content = sb.ToString(),
                Description = "truncates staging tables"
            };
            file.Tables.AddRange(ordered.Select(t => t.LogicalName));
            return file;
        }

        // ---------------------------------------------------------------- mermaid diagram

        /// <summary>
        /// Mermaid flowchart: nodes grouped into subgraphs by dependency tier, solid arrows
        /// pointing at the lookup TARGET (load the target first), dashed arrows for edges
        /// dropped to break cycles (resolve with a deferred update pass).
        /// </summary>
        private GeneratedFile BuildMermaid(List<List<TableModel>> tiers, Dictionary<string, HashSet<string>> droppedEdges)
        {
            var selected = new HashSet<string>(
                tiers.SelectMany(t => t).Select(t => t.LogicalName.ToLowerInvariant()));

            var sb = new StringBuilder();
            sb.AppendLine("%% Dataverse Migration Scaffolder - dependency diagram");
            sb.AppendLine(string.Format("%% Generated by Dataverse Migration Scaffolder on {0:yyyy-MM-dd HH:mm}", DateTime.Now));
            sb.AppendLine("%% Solid arrow: lookup dependency (points at the target - load the target first).");
            sb.AppendLine("%% Dashed arrow: dropped to break a cycle - resolve with a deferred update pass.");
            sb.AppendLine("%% Render at mermaid.live, or paste into a GitHub/Azure DevOps markdown file.");
            sb.AppendLine("flowchart TD");

            for (var tierIndex = 0; tierIndex < tiers.Count; tierIndex++)
            {
                sb.AppendLine(string.Format("    subgraph tier{0}[\"Tier {0}\"]", tierIndex));
                foreach (var table in tiers[tierIndex])
                {
                    sb.AppendLine(string.Format("        {0}[\"{1}\"]",
                        table.LogicalName, MermaidLabel(table)));
                }
                sb.AppendLine("    end");
            }

            var emitted = new HashSet<string>();
            foreach (var table in tiers.SelectMany(t => t))
            {
                HashSet<string> dropped;
                droppedEdges.TryGetValue(table.LogicalName.ToLowerInvariant(), out dropped);

                foreach (var dep in table.Dependencies.Where(d => selected.Contains(d)).OrderBy(d => d))
                {
                    var isDropped = dropped != null && dropped.Contains(dep);
                    var edge = string.Format("    {0} {1} {2}", table.LogicalName, isDropped ? "-.->" : "-->", dep);
                    if (emitted.Add(edge)) sb.AppendLine(edge);
                }
            }

            var file = new GeneratedFile
            {
                FileName = "diagram.mmd",
                Content = sb.ToString(),
                Description = "Mermaid dependency diagram"
            };
            file.Tables.AddRange(tiers.SelectMany(t => t).Select(t => t.LogicalName));
            return file;
        }

        private static string MermaidLabel(TableModel table)
        {
            var display = (table.DisplayName ?? table.LogicalName)
                .Replace("\"", "'").Replace("[", "(").Replace("]", ")");
            return display + "<br/><small>" + table.LogicalName + "</small>";
        }

        // ---------------------------------------------------------------- data dictionary

        private GeneratedFile BuildDataDictionary(List<TierChunk> chunks, Dictionary<string, HashSet<string>> droppedEdges)
        {
            // Sheets ordered by DISPLAY name; an index sheet ("~Tables") sorts first.
            var entries = new List<Tuple<TableModel, int, int>>();   // table, tier, file number
            for (var i = 0; i < chunks.Count; i++)
            {
                foreach (var table in chunks[i].Tables)
                {
                    entries.Add(Tuple.Create(table, chunks[i].TierIndex, i + 1));
                }
            }
            entries = entries.OrderBy(e => e.Item1.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            var sheets = new List<XlsxSheet>();
            var usedNames = new HashSet<string>();

            // Index sheet
            var index = new XlsxSheet
            {
                Name = XlsxWriter.SafeSheetName("~Tables", usedNames),
                ColumnWidths = new double[] { 35, 35, 35, 8, 8, 45 }
            };
            index.AddRow().AddBold("Display Name").AddBold("Logical Name").AddBold("Schema Name")
                 .AddBold("Tier").AddBold("File").AddBold("Dropped Dependencies (deferred update pass)");
            foreach (var e in entries)
            {
                HashSet<string> dropped;
                var droppedText = droppedEdges.TryGetValue(e.Item1.LogicalName.ToLowerInvariant(), out dropped)
                    ? string.Join(", ", dropped.OrderBy(d => d))
                    : "";
                index.AddRow().Add(e.Item1.DisplayName).Add(e.Item1.LogicalName).Add(e.Item1.SchemaName)
                     .Add(e.Item2.ToString()).Add(string.Format("{0:00}", e.Item3)).Add(droppedText);
            }
            sheets.Add(index);

            // One sheet per table
            foreach (var e in entries)
            {
                var table = e.Item1;
                var sheet = new XlsxSheet
                {
                    Name = XlsxWriter.SafeSheetName(table.DisplayName, usedNames),
                    ColumnWidths = new double[] { 32, 32, 30, 18, 30, 55, 16, 35, 20 }
                };

                sheet.AddRow().AddBold("Entity").Add(table.DisplayName);
                sheet.AddRow().AddBold("Plural Display Name").Add(table.PluralDisplayName);
                sheet.AddRow().AddBold("Description").AddWrap(table.Description);
                sheet.AddRow().AddBold("Schema Name").Add(table.SchemaName);
                sheet.AddRow().AddBold("Logical Name").Add(table.LogicalName);
                sheet.AddRow().AddBold("Object Type Code").Add(table.ObjectTypeCode.HasValue ? table.ObjectTypeCode.Value.ToString() : "");
                sheet.AddRow().AddBold("Is Custom Entity").Add(table.IsCustomEntity ? "TRUE" : "FALSE");
                sheet.AddRow().AddBold("Ownership Type").Add(table.OwnershipType);
                sheet.AddRow().AddBold("Introduced Version").Add(table.IntroducedVersion);
                sheet.AddRow().AddBold("Dependency Tier").Add(e.Item2.ToString());
                sheet.AddRow();   // blank separator

                sheet.AddRow().AddBold("Logical Name").AddBold("Display Name").AddBold("Attribute Type")
                     .AddBold("Lookup Target").AddBold("Description").AddBold("Custom Attribute")
                     .AddBold("Additional data").AddBold("SQL Type");

                foreach (var col in table.Columns)
                {
                    sheet.AddRow()
                         .Add(col.Name)
                         .Add(col.DisplayName)
                         .Add(col.AttributeTypeName)
                         .Add(col.Targets == null ? "" : string.Join(", ", col.Targets))
                         .AddWrap(col.Description)
                         .Add(col.IsCustomAttribute ? "True" : "False")
                         .Add(col.AdditionalInfo)
                         .Add(col.SqlType);
                }

                sheets.Add(sheet);
            }

            return new GeneratedFile
            {
                FileName = "data_dictionary.xlsx",
                Content = string.Format("Excel data dictionary: {0} tables, one sheet each (ordered by display name) plus a ~Tables index sheet." +
                                        Environment.NewLine + "Binary file - open it from the output folder.", entries.Count),
                BinaryContent = XlsxWriter.Write(sheets),
                Description = "data dictionary (xlsx)"
            };
        }
    }
}
