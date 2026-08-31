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
    /// create-if-missing per kind, guarded match-key indexes, truncate and teardown
    /// scripts, Excel data dictionary, Mermaid diagram, a machine-readable
    /// manifest.json describing the whole run, and a harness metadata seed script
    /// populating meta.Entity / meta.ColumnMap for a downstream package generator.
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
                        FileName = StagingFileName(i),
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
                        FileName = GuidFileName(i),
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

            // Built before the JSON manifest so any warnings it raises (tables with no match
            // key) are already in the list the manifest serialises.
            if (_settings.GenerateMetadataSeed)
            {
                result.Files.Add(BuildMetadataSeed(chunks, droppedEdges, result.Warnings));
            }

            if (_settings.GenerateJsonManifest)
            {
                result.Files.Add(BuildJsonManifest(chunks, droppedEdges, result.Warnings));
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
                    sb.AppendLine();
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
                    sb.AppendLine(string.Format("CREATE TABLE {0}(", fullName));
                    AppendColumnLines(sb, lines, "");
                    sb.AppendLine(");");
            }
            else
            {
                    sb.AppendLine(string.Format("IF OBJECT_ID(N'{0}', N'U') IS NULL", fullName));
                    sb.AppendLine("BEGIN");
                    sb.AppendLine(string.Format("    CREATE TABLE {0}(", fullName));
                    AppendColumnLines(sb, lines, "    ");
                    sb.AppendLine("    );");
                    sb.AppendLine("END");
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
            sb.AppendLine("SET ANSI_NULLS ON;");
            sb.AppendLine("SET QUOTED_IDENTIFIER ON;");

            foreach (var table in chunk.Tables)
            {
                    EmitGuidTable(sb, table);
                    sb.AppendLine();
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
                    sb.AppendLine(string.Format("CREATE TABLE {0}(", fullName));
                    sb.AppendLine(string.Join("," + Environment.NewLine, lines));
                    sb.AppendLine(");");
            }
            else
            {
                    sb.AppendLine(string.Format("IF OBJECT_ID(N'{0}', N'U') IS NULL", fullName));
                    sb.AppendLine("BEGIN");
                    sb.AppendLine(string.Format("    CREATE TABLE {0}(", fullName));
                    sb.AppendLine(string.Join("," + Environment.NewLine, lines));
                    sb.AppendLine("    );");
                    sb.AppendLine("END");
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

        // ---------------------------------------------------------------- json manifest

        /// <summary>
        /// Machine-readable run manifest: every table with its dependency tier, the file it
        /// was written to, its columns (SQL + Dataverse types), lookup targets, match keys,
        /// and the dependency edges dropped to break cycles. Intended for ETL pipelines that
        /// need to sequence packages or drive deferred-lookup update passes.
        /// </summary>
        private GeneratedFile BuildJsonManifest(List<TierChunk> chunks,
                                                Dictionary<string, HashSet<string>> droppedEdges,
                                                List<string> warnings)
        {
            var allTables = chunks.SelectMany(c => c.Tables).ToList();
            var inScope = new HashSet<string>(allTables.Select(t => t.LogicalName), StringComparer.OrdinalIgnoreCase);
            var version = typeof(ScriptGenerator).Assembly.GetName().Version;
            var w = new JsonWriter();

            w.StartObject();
            w.Prop("manifestVersion", 1);

            w.StartObject("generator");
            w.Prop("tool", "Dataverse Migration Scaffolder");
            w.Prop("version", version == null ? "" : version.ToString());
            w.Prop("generatedOn", DateTimeOffset.Now.ToString("o"));
            w.EndObject();

            w.StartObject("options");
            w.Prop("schema", _settings.SchemaName);
            w.Prop("stagingPrefix", _settings.StagingPrefix);
            w.Prop("guidPrefix", _settings.GuidPrefix);
            w.Prop("matchKeySuffixes", _settings.MatchKeySuffixes);
            w.Prop("batchSize", _settings.BatchSize);
            w.Prop("stagingGenerated", _settings.GenerateStaging);
            w.Prop("guidGenerated", _settings.GenerateGuid);
            w.Prop("stagingMode", _settings.StagingDropRecreate ? "dropAndRecreate" : "createIfMissing");
            w.Prop("guidMode", _settings.GuidDropRecreate ? "dropAndRecreate" : "createIfMissing");
            w.Prop("matchKeyIndexes", _settings.IndexLegacyIdColumns);
            w.StringArray("dependencyRankingExclusions",
                _settings.GetDependencyExclusions().OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            w.EndObject();

            w.Prop("tierCount", chunks.Count == 0 ? 0 : chunks.Max(c => c.TierIndex) + 1);
            w.Prop("tableCount", allTables.Count);

            // ---- files -----------------------------------------------------------
            w.StartArray("files");
            for (var i = 0; i < chunks.Count; i++)
            {
                if (_settings.GenerateStaging) WriteFileEntry(w, StagingFileName(i), "staging", chunks[i]);
                if (_settings.GenerateGuid) WriteFileEntry(w, GuidFileName(i), "guid", chunks[i]);
            }
            w.EndArray();

            // ---- tables (emitted in tier / file order) ----------------------------
            w.StartArray("tables");
            for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                var chunk = chunks[chunkIndex];
                foreach (var table in chunk.Tables)
                {
                    HashSet<string> dropped;
                    droppedEdges.TryGetValue(table.LogicalName.ToLowerInvariant(), out dropped);
                    var droppedList = dropped == null
                        ? new List<string>()
                        : dropped.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();

                    w.StartObject();
                    w.Prop("logicalName", table.LogicalName);
                    w.Prop("schemaName", table.SchemaName);
                    w.Prop("displayName", table.DisplayName);
                    w.Prop("prefix", table.Prefix);
                    w.Prop("tier", chunk.TierIndex);
                    w.Prop("tierPart", chunk.Part);
                    w.Prop("tierTotalParts", chunk.TotalParts);
                    w.Prop("fileNumber", chunkIndex + 1);
                    w.Prop("stagingFile", _settings.GenerateStaging ? StagingFileName(chunkIndex) : null);
                    w.Prop("guidFile", _settings.GenerateGuid ? GuidFileName(chunkIndex) : null);
                    w.Prop("stagingTable", string.Format("[{0}].[{1}{2}]",
                        _settings.SchemaName, _settings.StagingPrefix, table.SchemaName));
                    w.Prop("guidTable", string.Format("[{0}].[{1}{2}]",
                        _settings.SchemaName, _settings.GuidPrefix, table.SchemaName));
                    w.Prop("primaryIdAttribute", table.PrimaryIdAttribute);
                    w.Prop("primaryNameAttribute", table.PrimaryNameAttribute);
                    w.Prop("isCycleMember", droppedList.Count > 0);

                    w.StringArray("matchKeys",
                        table.Columns.Where(c => _settings.IsMatchKey(c.Name)).Select(c => c.Name));
                    w.StringArray("dependencies",
                        table.Dependencies.Where(d => inScope.Contains(d))
                                          .OrderBy(d => d, StringComparer.OrdinalIgnoreCase));
                    w.StringArray("externalDependencies",
                        table.Dependencies.Where(d => !inScope.Contains(d))
                                          .OrderBy(d => d, StringComparer.OrdinalIgnoreCase));
                    w.StringArray("droppedDependencies", droppedList);

                    w.StartArray("columns");
                    foreach (var col in table.Columns)
                    {
                        var targets = col.Targets ?? new string[0];
                        var deferred = col.IsLookup && droppedList.Count > 0 &&
                                       targets.Any(t => droppedList.Contains(t, StringComparer.OrdinalIgnoreCase));

                        w.StartObject();
                        w.Prop("name", col.Name);
                        w.Prop("displayName", col.DisplayName);
                        w.Prop("sqlType", col.SqlType);
                        w.Prop("dataverseType", col.AttributeTypeName);
                        w.Prop("isCustom", col.IsCustomAttribute);
                        w.Prop("isPrimaryId", col.IsPrimaryId);
                        w.Prop("isPrimaryName", col.IsPrimaryName);
                        w.Prop("isMatchKey", _settings.IsMatchKey(col.Name));
                        w.Prop("isLookup", col.IsLookup);
                        w.Prop("isPolymorphic", col.IsPolymorphic);
                        w.Prop("isTypeCompanion", col.IsTypeCompanion);
                        w.Prop("requiresDeferredUpdate", deferred);
                        w.StringArray("targets", targets);
                        w.StringArray("targetsInScope", targets.Where(t => inScope.Contains(t)));
                        w.EndObject();
                    }
                    w.EndArray();

                    w.EndObject();
                }
            }
            w.EndArray();

            // ---- cycles ----------------------------------------------------------
            w.StartArray("cycles");
            foreach (var kv in droppedEdges.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                w.StartObject();
                w.Prop("table", kv.Key);
                w.StringArray("droppedTargets", kv.Value.OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
                w.EndObject();
            }
            w.EndArray();

            w.StringArray("warnings", warnings);
            w.EndObject();

            var file = new GeneratedFile
            {
                FileName = "manifest.json",
                Content = w.ToString(),
                Description = "run manifest (json)"
            };
            file.Tables.AddRange(allTables.Select(t => t.LogicalName));
            return file;
        }

        private void WriteFileEntry(JsonWriter w, string fileName, string kind, TierChunk chunk)
        {
            w.StartObject();
            w.Prop("fileName", fileName);
            w.Prop("kind", kind);
            w.Prop("tier", chunk.TierIndex);
            w.Prop("tierPart", chunk.Part);
            w.Prop("tierTotalParts", chunk.TotalParts);
            w.Prop("tableCount", chunk.Tables.Count);
            w.StringArray("tables", chunk.Tables.Select(t => t.LogicalName));
            w.EndObject();
        }

        private static string StagingFileName(int chunkIndex)
        {
            return string.Format("{0:00}_create_staging.sql", chunkIndex + 1);
        }

        private static string GuidFileName(int chunkIndex)
        {
            return string.Format("{0:00}_create_guid.sql", chunkIndex + 1);
        }

        // ---------------------------------------------------------------- harness metadata

        /// <summary>SSIS type facts parsed out of a generated SQL type string.</summary>
        private class SsisTypeInfo
        {
            public string DataType = "DT_WSTR";
            public string MaxLength = "NULL";
            public string Precision = "NULL";
            public string Scale = "NULL";
        }

        /// <summary>
        /// Maps a generated SQL type onto the SSIS data type a data flow column needs.
        /// Length/precision/scale come back as SQL literals ("100", "NULL") ready to embed.
        /// </summary>
        private static SsisTypeInfo ToSsisType(string sqlType)
        {
            var info = new SsisTypeInfo();
            if (string.IsNullOrEmpty(sqlType)) return info;

            var upper = sqlType.ToUpperInvariant().Trim();
            var arg = "";
            var open = upper.IndexOf('(');
            if (open >= 0 && upper.EndsWith(")"))
            {
                arg = upper.Substring(open + 1, upper.Length - open - 2).Trim();
                upper = upper.Substring(0, open).Trim();
            }

            switch (upper)
            {
                case "NVARCHAR":
                    if (string.Equals(arg, "MAX", StringComparison.OrdinalIgnoreCase))
                    {
                        info.DataType = "DT_NTEXT";
                    }
                    else
                    {
                        info.DataType = "DT_WSTR";
                        info.MaxLength = NumberOrNull(arg);
                    }
                    break;

                case "VARCHAR":
                    if (string.Equals(arg, "MAX", StringComparison.OrdinalIgnoreCase))
                    {
                        info.DataType = "DT_TEXT";
                    }
                    else
                    {
                        info.DataType = "DT_STR";
                        info.MaxLength = NumberOrNull(arg);
                    }
                    break;

                case "INT": info.DataType = "DT_I4"; break;
                case "BIGINT": info.DataType = "DT_I8"; break;
                case "BIT": info.DataType = "DT_BOOL"; break;
                case "FLOAT": info.DataType = "DT_R8"; break;
                case "UNIQUEIDENTIFIER": info.DataType = "DT_GUID"; break;
                case "DATE": info.DataType = "DT_DBDATE"; break;

                case "DATETIME2":
                    info.DataType = "DT_DBTIMESTAMP2";
                    info.Scale = string.IsNullOrEmpty(arg) ? "7" : NumberOrNull(arg);
                    break;

                case "DECIMAL":
                case "NUMERIC":
                    info.DataType = "DT_NUMERIC";
                    var parts = arg.Split(',');
                    info.Precision = parts.Length > 0 ? NumberOrNull(parts[0]) : "NULL";
                    info.Scale = parts.Length > 1 ? NumberOrNull(parts[1]) : "0";
                    break;

                case "MONEY":
                    info.DataType = "DT_CY";
                    break;
            }

            return info;
        }

        private static string NumberOrNull(string text)
        {
            int value;
            return int.TryParse(text == null ? "" : text.Trim(), out value) ? value.ToString() : "NULL";
        }

        /// <summary>SQL string literal, or NULL when there is nothing to write.</summary>
        private static string Q(string value)
        {
            if (string.IsNullOrEmpty(value)) return "NULL";
            return "N'" + value.Replace("'", "''") + "'";
        }

        private static string Bit(bool value)
        {
            return value ? "1" : "0";
        }

        /// <summary>
        /// Columns Dataverse calculates and refuses to accept on write. They stay in the
        /// metadata (staging still carries them, and they are useful for reconciliation)
        /// but are flagged so no generated data flow ever maps them to a destination.
        /// </summary>
        private static bool IsPlatformCalculated(SqlColumn col)
        {
            if (col.Name == null) return false;
            return col.Name.EndsWith("_base", StringComparison.OrdinalIgnoreCase)
                || col.Name.Equals("exchangerate", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Emits meta_seed.sql: the harness metadata tables plus the rows describing every
        /// generated table and column. This is the handoff between the scaffolder and the
        /// SSIS package generator, which reads meta.Entity / meta.ColumnMap rather than
        /// spreadsheets.
        ///
        /// Cycle members get a second row with PassNo = 2 carrying only the lookups that
        /// were deferred to break the cycle, which is the deferred UPDATE pass the warnings
        /// have always described in prose.
        ///
        /// Rerunnable by design. Structural facts are refreshed on every run; the values a
        /// human tunes are written once on insert and never overwritten.
        /// </summary>
        private GeneratedFile BuildMetadataSeed(List<TierChunk> chunks,
                                                Dictionary<string, HashSet<string>> droppedEdges,
                                                List<string> warnings)
        {
            var allTables = chunks.SelectMany(c => c.Tables).ToList();
            var entityRows = new List<string>();
            var columnRows = new List<string>();
            var withoutMatchKey = new List<string>();
            var deferredPasses = 0;

            foreach (var chunk in chunks)
            {
                foreach (var table in chunk.Tables)
                {
                    var matchKeys = table.Columns.Where(c => _settings.IsMatchKey(c.Name)).ToList();
                    var legacyField = matchKeys.Count > 0 ? matchKeys[0].Name : null;
                    var stagingTable = string.Format("[{0}].[{1}{2}]",
                        _settings.SchemaName, _settings.StagingPrefix, table.SchemaName);
                    var guidTable = string.Format("[{0}].[{1}{2}]",
                        _settings.SchemaName, _settings.GuidPrefix, table.SchemaName);

                    // Without a match key there is no create-versus-update decision and no id
                    // mapping, so the entity is seeded disabled rather than silently broken.
                    if (legacyField == null) withoutMatchKey.Add(table.LogicalName);

                    entityRows.Add(string.Format("    ({0}, 1, {1}, {2}, {3}, {4}, {5}, {6})",
                        chunk.TierIndex,
                        Q(table.LogicalName),
                        Q(stagingTable),
                        Q(guidTable),
                        Q(table.PrimaryIdAttribute),
                        Q(legacyField),
                        Bit(legacyField != null)));

                    var sortOrder = 0;
                    foreach (var col in table.Columns)
                    {
                        sortOrder++;
                        var ssis = ToSsisType(col.SqlType);
                        var calculated = IsPlatformCalculated(col);

                        // The primary id is never written on create (Dataverse assigns it) and
                        // is the record key on update - exactly the existing harness split.
                        var onCreate = !calculated && !col.IsPrimaryId;
                        var onUpdate = !calculated;

                        columnRows.Add(ColumnRow(table.LogicalName, 1, col, ssis, onCreate, onUpdate, sortOrder));
                    }

                    // ---- deferred pass for cycle members ----------------------------
                    HashSet<string> dropped;
                    droppedEdges.TryGetValue(table.LogicalName.ToLowerInvariant(), out dropped);
                    if (dropped == null || dropped.Count == 0) continue;

                    var deferredCols = table.Columns
                        .Where(c => c.IsLookup && c.Targets != null &&
                                    c.Targets.Any(t => dropped.Contains(t.ToLowerInvariant())))
                        .ToList();
                    if (deferredCols.Count == 0) continue;

                    deferredPasses++;
                    entityRows.Add(string.Format("    ({0}, 2, {1}, {2}, {3}, {4}, {5}, {6})",
                        chunk.TierIndex,
                        Q(table.LogicalName),
                        Q(stagingTable),
                        Q(guidTable),
                        Q(table.PrimaryIdAttribute),
                        Q(legacyField),
                        Bit(legacyField != null)));

                    var pass2Order = 0;
                    var idCol = table.Columns.FirstOrDefault(c => c.IsPrimaryId);
                    if (idCol != null)
                    {
                        pass2Order++;
                        columnRows.Add(ColumnRow(table.LogicalName, 2, idCol, ToSsisType(idCol.SqlType),
                            false, true, pass2Order));
                    }
                    foreach (var key in matchKeys)
                    {
                        pass2Order++;
                        columnRows.Add(ColumnRow(table.LogicalName, 2, key, ToSsisType(key.SqlType),
                            false, false, pass2Order));
                    }
                    foreach (var col in deferredCols)
                    {
                        pass2Order++;
                        columnRows.Add(ColumnRow(table.LogicalName, 2, col, ToSsisType(col.SqlType),
                            false, true, pass2Order));
                    }
                }
            }

            if (withoutMatchKey.Count > 0)
            {
                warnings.Add(string.Format(
                    "No match-key column ({0}) on {1} table(s) - seeded into meta.Entity with IsEnabled = 0 " +
                    "because create-versus-update cannot be decided without one: {2}",
                    _settings.MatchKeySuffixes,
                    withoutMatchKey.Count,
                    string.Join(", ", withoutMatchKey.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))));
            }

            var sb = new StringBuilder();
            AppendMetadataHeader(sb, allTables.Count, chunks, entityRows.Count, columnRows.Count, deferredPasses);
            AppendMetadataDdl(sb);
            AppendMetadataStaging(sb, entityRows, columnRows);
            AppendMetadataMerge(sb);

            var file = new GeneratedFile
            {
                FileName = "meta_seed.sql",
                Content = sb.ToString(),
                Description = string.Format("harness metadata: {0} entity rows, {1} column rows",
                    entityRows.Count, columnRows.Count)
            };
            file.Tables.AddRange(allTables.Select(t => t.LogicalName));
            return file;
        }

        private string ColumnRow(string logicalName, int passNo, SqlColumn col, SsisTypeInfo ssis,
                                 bool onCreate, bool onUpdate, int sortOrder)
        {
            // Polymorphic lookups have no single target; their companion "<name>type" column
            // carries the discriminator, so the target is left NULL rather than truncated.
            var targets = col.Targets ?? new string[0];
            var target = targets.Length == 1 ? targets[0] : null;

            return string.Format("    ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10}, {11}, {12})",
                Q(logicalName),
                passNo,
                Q(col.Name),
                Q(col.Name),
                Q(ssis.DataType),
                ssis.MaxLength,
                ssis.Precision,
                ssis.Scale,
                Bit(onCreate),
                Bit(onUpdate),
                Bit(col.IsLookup),
                Q(target),
                sortOrder);
        }

        private void AppendMetadataHeader(StringBuilder sb, int tableCount, List<TierChunk> chunks,
                                          int entityRowCount, int columnRowCount, int deferredPasses)
        {
            var tierCount = chunks.Count == 0 ? 0 : chunks.Max(c => c.TierIndex) + 1;

            sb.AppendLine("/*");
            sb.AppendLine(" * Harness metadata seed - meta.Entity and meta.ColumnMap");
            sb.AppendLine(string.Format(" * Generated by Dataverse Migration Scaffolder on {0:yyyy-MM-dd HH:mm}", DateTime.Now));
            sb.AppendLine(" *");
            sb.AppendLine(string.Format(" * {0} tables across {1} dependency tier(s).", tableCount, tierCount));
            sb.AppendLine(string.Format(" * {0} entity row(s) including {1} deferred pass(es) (PassNo = 2).",
                entityRowCount, deferredPasses));
            sb.AppendLine(string.Format(" * {0} column mapping row(s).", columnRowCount));
            sb.AppendLine(" *");
            sb.AppendLine(" * Rerunnable. Structural facts are refreshed on every run:");
            sb.AppendLine(" *   meta.Entity      Wave, StagingTable, GuidTable, PrimaryIdField, LegacyIdField");
            sb.AppendLine(" *   meta.ColumnMap   SsisDataType, MaxLength, NumericPrecision, NumericScale,");
            sb.AppendLine(" *                    IsLookup, LookupTargetEntity, SortOrder");
            sb.AppendLine(" *");
            sb.AppendLine(" * Values you tune by hand are written once on insert and never overwritten:");
            sb.AppendLine(" *   meta.Entity      WriteMode, BatchSize, ThreadCount, IsEnabled");
            sb.AppendLine(" *   meta.ColumnMap   IncludeOnCreate, IncludeOnUpdate");
            sb.AppendLine(" *");
            sb.AppendLine(" * Attributes that no longer exist in Dataverse are deleted from meta.ColumnMap.");
            sb.AppendLine(" * Entities absent from this run are left alone, so scoping the scaffolder to one");
            sb.AppendLine(" * solution never deletes another solution's metadata.");
            sb.AppendLine(" */");
            sb.AppendLine("SET ANSI_NULLS ON;");
            sb.AppendLine("SET QUOTED_IDENTIFIER ON;");
            sb.AppendLine("SET NOCOUNT ON;");
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        private void AppendMetadataDdl(StringBuilder sb)
        {
            sb.AppendLine("IF SCHEMA_ID(N'meta') IS NULL");
            sb.AppendLine("    EXEC (N'CREATE SCHEMA meta;');");
            sb.AppendLine("GO");
            sb.AppendLine();
            sb.AppendLine("IF OBJECT_ID(N'[meta].[Entity]', N'U') IS NULL");
            sb.AppendLine("BEGIN");
            sb.AppendLine("    CREATE TABLE meta.Entity (");
            sb.AppendLine("        EntityId       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_meta_Entity PRIMARY KEY,");
            sb.AppendLine("        Wave           SMALLINT NOT NULL,");
            sb.AppendLine("        PassNo         TINYINT  NOT NULL CONSTRAINT DF_meta_Entity_PassNo DEFAULT (1),");
            sb.AppendLine("        LogicalName    NVARCHAR(128) NOT NULL,");
            sb.AppendLine("        StagingTable   NVARCHAR(256) NOT NULL,");
            sb.AppendLine("        GuidTable      NVARCHAR(256) NOT NULL,");
            sb.AppendLine("        PrimaryIdField NVARCHAR(128) NOT NULL,");
            sb.AppendLine("        LegacyIdField  NVARCHAR(128) NULL,");
            sb.AppendLine("        WriteMode      VARCHAR(20) NOT NULL");
            sb.AppendLine("                       CONSTRAINT DF_meta_Entity_WriteMode DEFAULT ('CreateUpdate'),");
            sb.AppendLine("        BatchSize      INT NOT NULL CONSTRAINT DF_meta_Entity_BatchSize   DEFAULT (100),");
            sb.AppendLine("        ThreadCount    INT NOT NULL CONSTRAINT DF_meta_Entity_ThreadCount DEFAULT (20),");
            sb.AppendLine("        IsEnabled      BIT NOT NULL CONSTRAINT DF_meta_Entity_IsEnabled   DEFAULT (1),");
            sb.AppendLine("        CONSTRAINT UQ_meta_Entity_Name_Pass UNIQUE (LogicalName, PassNo),");
            sb.AppendLine("        CONSTRAINT CK_meta_Entity_WriteMode CHECK (WriteMode IN ('CreateUpdate','Upsert'))");
            sb.AppendLine("    );");
            sb.AppendLine("END");
            sb.AppendLine("GO");
            sb.AppendLine();
            sb.AppendLine("IF OBJECT_ID(N'[meta].[ColumnMap]', N'U') IS NULL");
            sb.AppendLine("BEGIN");
            sb.AppendLine("    CREATE TABLE meta.ColumnMap (");
            sb.AppendLine("        ColumnMapId        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_meta_ColumnMap PRIMARY KEY,");
            sb.AppendLine("        EntityId           INT NOT NULL");
            sb.AppendLine("                           CONSTRAINT FK_meta_ColumnMap_Entity REFERENCES meta.Entity (EntityId),");
            sb.AppendLine("        StagingColumn      NVARCHAR(128) NOT NULL,");
            sb.AppendLine("        TargetAttribute    NVARCHAR(128) NOT NULL,");
            sb.AppendLine("        SsisDataType       VARCHAR(20)   NOT NULL,");
            sb.AppendLine("        MaxLength          INT     NULL,");
            sb.AppendLine("        NumericPrecision   TINYINT NULL,");
            sb.AppendLine("        NumericScale       TINYINT NULL,");
            sb.AppendLine("        IncludeOnCreate    BIT NOT NULL CONSTRAINT DF_meta_ColumnMap_IncCreate DEFAULT (1),");
            sb.AppendLine("        IncludeOnUpdate    BIT NOT NULL CONSTRAINT DF_meta_ColumnMap_IncUpdate DEFAULT (1),");
            sb.AppendLine("        IsLookup           BIT NOT NULL CONSTRAINT DF_meta_ColumnMap_IsLookup  DEFAULT (0),");
            sb.AppendLine("        LookupTargetEntity NVARCHAR(128) NULL,");
            sb.AppendLine("        SortOrder          INT NOT NULL CONSTRAINT DF_meta_ColumnMap_SortOrder DEFAULT (0),");
            sb.AppendLine("        CONSTRAINT UQ_meta_ColumnMap UNIQUE (EntityId, TargetAttribute)");
            sb.AppendLine("    );");
            sb.AppendLine("END");
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        /// <summary>
        /// Writes the incoming rows into temp tables. INSERT ... VALUES caps at 1000 rows per
        /// statement, so the rows are emitted in batches.
        /// </summary>
        private void AppendMetadataStaging(StringBuilder sb, List<string> entityRows, List<string> columnRows)
        {
            const int batch = 500;

            sb.AppendLine("CREATE TABLE #Entity (");
            sb.AppendLine("    Wave           SMALLINT NOT NULL,");
            sb.AppendLine("    PassNo         TINYINT  NOT NULL,");
            sb.AppendLine("    LogicalName    NVARCHAR(128) NOT NULL,");
            sb.AppendLine("    StagingTable   NVARCHAR(256) NOT NULL,");
            sb.AppendLine("    GuidTable      NVARCHAR(256) NOT NULL,");
            sb.AppendLine("    PrimaryIdField NVARCHAR(128) NOT NULL,");
            sb.AppendLine("    LegacyIdField  NVARCHAR(128) NULL,");
            sb.AppendLine("    IsEnabled      BIT NOT NULL,");
            sb.AppendLine("    PRIMARY KEY (LogicalName, PassNo)");
            sb.AppendLine(");");
            sb.AppendLine();
            sb.AppendLine("CREATE TABLE #ColumnMap (");
            sb.AppendLine("    LogicalName        NVARCHAR(128) NOT NULL,");
            sb.AppendLine("    PassNo             TINYINT  NOT NULL,");
            sb.AppendLine("    StagingColumn      NVARCHAR(128) NOT NULL,");
            sb.AppendLine("    TargetAttribute    NVARCHAR(128) NOT NULL,");
            sb.AppendLine("    SsisDataType       VARCHAR(20)   NOT NULL,");
            sb.AppendLine("    MaxLength          INT     NULL,");
            sb.AppendLine("    NumericPrecision   TINYINT NULL,");
            sb.AppendLine("    NumericScale       TINYINT NULL,");
            sb.AppendLine("    IncludeOnCreate    BIT NOT NULL,");
            sb.AppendLine("    IncludeOnUpdate    BIT NOT NULL,");
            sb.AppendLine("    IsLookup           BIT NOT NULL,");
            sb.AppendLine("    LookupTargetEntity NVARCHAR(128) NULL,");
            sb.AppendLine("    SortOrder          INT NOT NULL,");
            sb.AppendLine("    PRIMARY KEY (LogicalName, PassNo, TargetAttribute)");
            sb.AppendLine(");");
            sb.AppendLine();

            AppendBatchedInserts(sb, entityRows, batch,
                "INSERT INTO #Entity (Wave, PassNo, LogicalName, StagingTable, GuidTable, PrimaryIdField, LegacyIdField, IsEnabled) VALUES");

            AppendBatchedInserts(sb, columnRows, batch,
                "INSERT INTO #ColumnMap (LogicalName, PassNo, StagingColumn, TargetAttribute, SsisDataType, MaxLength, NumericPrecision, NumericScale, IncludeOnCreate, IncludeOnUpdate, IsLookup, LookupTargetEntity, SortOrder) VALUES");
        }

        private static void AppendBatchedInserts(StringBuilder sb, List<string> rows, int batch, string insertHeader)
        {
            for (var offset = 0; offset < rows.Count; offset += batch)
            {
                var slice = rows.Skip(offset).Take(batch).ToList();
                sb.AppendLine(insertHeader);
                sb.AppendLine(string.Join("," + Environment.NewLine, slice) + ";");
                sb.AppendLine();
            }
        }

        private void AppendMetadataMerge(StringBuilder sb)
        {
            sb.AppendLine("MERGE meta.Entity AS tgt");
            sb.AppendLine("USING #Entity AS src");
            sb.AppendLine("   ON tgt.LogicalName = src.LogicalName");
            sb.AppendLine("  AND tgt.PassNo      = src.PassNo");
            sb.AppendLine("WHEN MATCHED THEN UPDATE SET");
            sb.AppendLine("        tgt.Wave           = src.Wave,");
            sb.AppendLine("        tgt.StagingTable   = src.StagingTable,");
            sb.AppendLine("        tgt.GuidTable      = src.GuidTable,");
            sb.AppendLine("        tgt.PrimaryIdField = src.PrimaryIdField,");
            sb.AppendLine("        tgt.LegacyIdField  = src.LegacyIdField");
            sb.AppendLine("WHEN NOT MATCHED BY TARGET THEN");
            sb.AppendLine("    INSERT (Wave, PassNo, LogicalName, StagingTable, GuidTable, PrimaryIdField, LegacyIdField, IsEnabled)");
            sb.AppendLine("    VALUES (src.Wave, src.PassNo, src.LogicalName, src.StagingTable, src.GuidTable,");
            sb.AppendLine("            src.PrimaryIdField, src.LegacyIdField, src.IsEnabled);");
            sb.AppendLine();
            sb.AppendLine("MERGE meta.ColumnMap AS tgt");
            sb.AppendLine("USING (");
            sb.AppendLine("    SELECT  e.EntityId,");
            sb.AppendLine("            c.StagingColumn,");
            sb.AppendLine("            c.TargetAttribute,");
            sb.AppendLine("            c.SsisDataType,");
            sb.AppendLine("            c.MaxLength,");
            sb.AppendLine("            c.NumericPrecision,");
            sb.AppendLine("            c.NumericScale,");
            sb.AppendLine("            c.IncludeOnCreate,");
            sb.AppendLine("            c.IncludeOnUpdate,");
            sb.AppendLine("            c.IsLookup,");
            sb.AppendLine("            c.LookupTargetEntity,");
            sb.AppendLine("            c.SortOrder");
            sb.AppendLine("    FROM    #ColumnMap AS c");
            sb.AppendLine("    JOIN    meta.Entity AS e");
            sb.AppendLine("            ON e.LogicalName = c.LogicalName AND e.PassNo = c.PassNo");
            sb.AppendLine(") AS src");
            sb.AppendLine("   ON tgt.EntityId        = src.EntityId");
            sb.AppendLine("  AND tgt.TargetAttribute = src.TargetAttribute");
            sb.AppendLine("WHEN MATCHED THEN UPDATE SET");
            sb.AppendLine("        tgt.StagingColumn      = src.StagingColumn,");
            sb.AppendLine("        tgt.SsisDataType       = src.SsisDataType,");
            sb.AppendLine("        tgt.MaxLength          = src.MaxLength,");
            sb.AppendLine("        tgt.NumericPrecision   = src.NumericPrecision,");
            sb.AppendLine("        tgt.NumericScale       = src.NumericScale,");
            sb.AppendLine("        tgt.IsLookup           = src.IsLookup,");
            sb.AppendLine("        tgt.LookupTargetEntity = src.LookupTargetEntity,");
            sb.AppendLine("        tgt.SortOrder          = src.SortOrder");
            sb.AppendLine("WHEN NOT MATCHED BY TARGET THEN");
            sb.AppendLine("    INSERT (EntityId, StagingColumn, TargetAttribute, SsisDataType, MaxLength,");
            sb.AppendLine("            NumericPrecision, NumericScale, IncludeOnCreate, IncludeOnUpdate,");
            sb.AppendLine("            IsLookup, LookupTargetEntity, SortOrder)");
            sb.AppendLine("    VALUES (src.EntityId, src.StagingColumn, src.TargetAttribute, src.SsisDataType, src.MaxLength,");
            sb.AppendLine("            src.NumericPrecision, src.NumericScale, src.IncludeOnCreate, src.IncludeOnUpdate,");
            sb.AppendLine("            src.IsLookup, src.LookupTargetEntity, src.SortOrder)");
            sb.AppendLine("WHEN NOT MATCHED BY SOURCE");
            sb.AppendLine("     AND tgt.EntityId IN (SELECT e.EntityId");
            sb.AppendLine("                          FROM   meta.Entity AS e");
            sb.AppendLine("                          JOIN   #Entity AS x");
            sb.AppendLine("                                 ON x.LogicalName = e.LogicalName AND x.PassNo = e.PassNo)");
            sb.AppendLine("     THEN DELETE;");
            sb.AppendLine();
            sb.AppendLine("DROP TABLE #ColumnMap;");
            sb.AppendLine("DROP TABLE #Entity;");
            sb.AppendLine("GO");
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
