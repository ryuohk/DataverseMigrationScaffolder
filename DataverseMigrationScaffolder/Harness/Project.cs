using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;

namespace DataverseMigrationScaffolder.HarnessGen
{
    internal sealed class Template
    {
        public string DtprojPath;
        public XmlDocument Doc;
        public string ReferencePackage;   // file name, e.g. "01b - Harness.dtsx"
        public byte[] PackageXml;
        public string Directory { get { return Path.GetDirectoryName(Path.GetFullPath(DtprojPath)); } }
        public string Stem { get { return Path.GetFileNameWithoutExtension(DtprojPath); } }
    }

    /// <summary>What a generation produced (also written to harnessgen-report.json).</summary>
    public sealed class GenerationResult
    {
        public string OutputDir { get; internal set; }
        public string ProjectFile { get; internal set; }
        public string ProjectName { get; internal set; }
        internal JObj Orchestration;
        internal List<JObj> Packages = new List<JObj>();
        internal List<JObj> Deferred = new List<JObj>();
        internal JObj Summary;
        internal JObj Setup;
        internal List<JObj> Membership = new List<JObj>();
        public List<string> Issues { get; internal set; } = new List<string>();
        /// <summary>The reference's protection level when the project was switched to DontSaveSensitive.</summary>
        public string ProtectionChangedFrom { get; internal set; }

        /// <summary>The completion summary shown after generation.</summary>
        public List<string> SummaryLines() { return SummaryText.Lines(this); }
    }

    /// <summary>Options for one generation (the command-line arguments of the original tool).
    /// The scaffolder's outputs come either from files (ManifestPath, with the scripts and an
    /// optional meta_seed.sql next to it) or from memory (ManifestText, Scripts, MetaSeedText).</summary>
    public sealed class GenerationOptions
    {
        public string ManifestPath;
        /// <summary>In-memory manifest.json text; when set, ManifestPath is not read.</summary>
        public string ManifestText;
        /// <summary>The manifest's name in the report and messages (default: the file name of ManifestPath).</summary>
        public string ManifestName;
        /// <summary>In-memory scaffolder scripts by file name (NN_create_staging.sql, NN_create_guid.sql).</summary>
        public Dictionary<string, string> Scripts;
        /// <summary>In-memory meta_seed.sql text (optional).</summary>
        public string MetaSeedText;
        public string DtprojPath;
        public string OutputDir;
        public string ReferenceTable;
        public string ReferencePackage;
        public List<string> Tables;
        public bool KeepReference;
        public string ProjectName;
        public string MetaSeed;
        public bool AllowMissingDependencies;
        /// <summary>"reference" (default: keep the reference's protection level and encrypted values)
        /// or "DontSaveSensitive" (store no passwords or secrets).</summary>
        public string Protection;
        /// <summary>Written to the report's "generator" field.</summary>
        public string Generator = "harnessgen 0.1.0";
    }

    /// <summary>Load the reference .dtproj and assemble the generated SSIS project.</summary>
    public static class HarnessProject
    {
        public const string ReportName = "harnessgen-report.json";
        public const string QueriesDir = "Queries";   // generated Stage <Table> .sql files
        private const string MetaSeedName = "meta_seed.sql";
        private const string DefaultProjectSuffix = "_Generated";
        public const string ProtectionReference = "reference";
        public static readonly string[] ProtectionChoices = { ProtectionReference, Support.DontSaveSensitive };
        private static readonly Regex SafeProjectName = Py.Start(@"^[A-Za-z0-9_](?:[A-Za-z0-9_ .\-]*[A-Za-z0-9_\-])?$");
        private const string BuildNote = "Checked by reading the generated XML only. The project has not been opened or built in "
                                         + "Visual Studio with SSIS Projects and KingswaySoft, and no migration has been run.";

        internal static Template LoadTemplate(string dtprojPath, string referencePackage = null)
        {
            if (!File.Exists(dtprojPath)) throw new GeneratorException("Reference .dtproj not found: " + dtprojPath);
            if (Path.GetExtension(dtprojPath).ToLowerInvariant() != ".dtproj")
                throw new GeneratorException("Reference project must be a .dtproj file: " + dtprojPath);
            XmlDocument doc;
            try { doc = Xml.ParseFile(dtprojPath); }
            catch (XmlException ex) { throw new GeneratorException("Reference .dtproj is not well-formed XML (" + dtprojPath + "): " + ex.Message, ex); }

            var listed = PackageEntries(doc).Select(p => Xml.GetNs(p, Xml.SsisNs, "Name")).ToList();
            if (listed.Count == 0)
                throw new GeneratorException("Reference .dtproj lists no packages under SSIS:Packages (" + dtprojPath + "); "
                                             + "only project-deployment-model projects are supported");
            if (referencePackage == null)
            {
                if (listed.Count != 1)
                    throw new GeneratorException("Reference .dtproj lists " + listed.Count + " packages (" + string.Join(", ", listed) + "); "
                                                 + "choose the template package");
                referencePackage = listed[0];
            }
            else
            {
                var match = listed.Where(n => n.ToLowerInvariant() == referencePackage.ToLowerInvariant()).ToList();
                if (match.Count == 0)
                    throw new GeneratorException("Reference package " + Py.Repr(referencePackage) + " is not listed in " + dtprojPath
                                                 + " (listed: " + string.Join(", ", listed) + ")");
                referencePackage = match[0];
            }
            var packagePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dtprojPath)), referencePackage);
            if (!File.Exists(packagePath)) throw new GeneratorException("Reference package listed in .dtproj does not exist: " + packagePath);
            return new Template { DtprojPath = dtprojPath, Doc = doc, ReferencePackage = referencePackage, PackageXml = File.ReadAllBytes(packagePath) };
        }

        private static List<Table> SelectTables(Manifest manifest, List<string> only)
        {
            List<Table> tables;
            if (only != null && only.Count > 0)
            {
                var missing = only.Where(n => manifest.Table(n) == null).ToList();
                if (missing.Count > 0) throw new GeneratorException("Selected tables not found in manifest: " + string.Join(", ", missing));
                var wanted = new HashSet<string>(only.Select(n => n.ToLowerInvariant()), StringComparer.Ordinal);
                tables = manifest.Tables.Where(t => wanted.Contains(t.LogicalName.ToLowerInvariant())).ToList();
            }
            else tables = manifest.Tables.ToList();
            return tables.OrderBy(t => t.Tier).ThenBy(t => t.LogicalName.ToLowerInvariant(), StringComparer.Ordinal).ToList();
        }

        private static string Full(string path) { return Path.GetFullPath(path).TrimEnd('\\', '/'); }

        /// <summary>Create the output folder. A non-empty folder is only replaced if an earlier run created it.</summary>
        private static void PrepareOutputDir(string outputDir, string templateDir)
        {
            var output = Full(outputDir);
            var template = Full(templateDir);
            if (string.Equals(output, template, StringComparison.OrdinalIgnoreCase)
                || output.StartsWith(template + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new GeneratorException("Output directory must not be inside the reference project folder: " + output);
            if (File.Exists(output)) throw new GeneratorException("Output path exists and is not a directory: " + output);
            if (System.IO.Directory.Exists(output) && System.IO.Directory.EnumerateFileSystemEntries(output).Any())
            {
                if (!File.Exists(Path.Combine(output, ReportName)))
                    throw new GeneratorException("Output directory is not empty and was not created by harnessgen (no " + ReportName + "): " + output);
                System.IO.Directory.Delete(output, true);
            }
            System.IO.Directory.CreateDirectory(output);
        }

        /// <summary>Why a table gets no Migrate flow: the flow decides create versus update and records
        /// created ids in the GUID table by the table's legacy key, so it needs exactly one.</summary>
        internal static string SkipReason(Table table)
        {
            if (table.MatchKeys.Count == 0) return "no legacy match key (legacyid), so created records cannot be recorded in the GUID table";
            if (table.MatchKeys.Count > 1) return "composite match key " + Py.Repr(table.MatchKeys) + " is not supported";
            if (!table.PackageColumns.Any(c => c.Name.ToLowerInvariant() == table.MatchKeys[0].ToLowerInvariant()))
                return "match key " + Py.Repr(table.MatchKeys[0]) + " is not a staging column";
            return null;
        }

        /// <summary>The project connection manager the reference reads its staging table through.</summary>
        private static ConnectionManagerInfo StagingConnection(byte[] xml, ProjectSupport support, string label)
        {
            var doc = Xml.Parse(xml);
            foreach (var component in Xml.ByTag(doc, "component"))
            {
                if (!component.GetAttribute("componentClassID").ToLowerInvariant().Contains("oledbsource")) continue;
                foreach (var connection in Xml.ByTag(component, "connection"))
                {
                    var reference = connection.GetAttribute("connectionManagerRefId");
                    const string prefix = "Project.ConnectionManagers[";
                    if (reference.StartsWith(prefix, StringComparison.Ordinal) && reference.EndsWith("]", StringComparison.Ordinal))
                    {
                        var manager = support.Connection(reference.Substring(prefix.Length, reference.Length - prefix.Length - 1));
                        if (manager != null) return manager;
                    }
                }
            }
            throw new GeneratorException("Reference package " + label + ": the staging source does not use a project connection "
                                         + "manager, so the generated SQL tasks cannot connect to the staging database");
        }

        private static string ReadScript(GenerationOptions options, string directory, string name)
        {
            if (options.ManifestText != null)
            {
                string text;
                if (string.IsNullOrEmpty(name) || options.Scripts == null || !options.Scripts.TryGetValue(name, out text))
                    throw new GeneratorException("Scaffolder script " + Py.Repr(name) + " was not generated with the manifest; "
                                                 + "generate the staging and GUID scripts with the manifest");
                return Metadata.NormalizeText(text);
            }
            var path = Path.Combine(directory, name ?? "");
            if (string.IsNullOrEmpty(name) || !File.Exists(path))
                throw new GeneratorException("Scaffolder script " + Py.Repr(name) + " is not next to the manifest (" + directory + "); "
                                             + "generate the staging and GUID scripts with the manifest");
            return Metadata.ReadText(path);
        }

        /// <summary>Generate the SSIS project. Throws GeneratorException for input the user must fix.</summary>
        public static GenerationResult Generate(GenerationOptions options)
        {
            var protection = string.IsNullOrEmpty(options.Protection) ? ProtectionReference : options.Protection;
            if (!ProtectionChoices.Contains(protection))
                throw new GeneratorException("Protection " + Py.Repr(protection) + " is not supported; use " + string.Join(" or ", ProtectionChoices));
            var clear = protection == Support.DontSaveSensitive;
            var inMemory = options.ManifestText != null;
            var manifestName = options.ManifestName ?? (inMemory ? "manifest.json" : Path.GetFileName(options.ManifestPath));
            var manifest = inMemory ? Metadata.LoadManifestText(options.ManifestText, manifestName) : Metadata.LoadManifest(options.ManifestPath);
            if (manifest.Groups.Count == 0)
                throw new GeneratorException(manifest.Path + " has no files[] grouping; regenerate it with Dataverse Migration "
                                             + "Scaffolder 1.2026.7.7 or later so staging and harness packages can follow its files");
            var manifestDir = inMemory ? null : Path.GetDirectoryName(Path.GetFullPath(manifest.Path));
            var template = LoadTemplate(options.DtprojPath, options.ReferencePackage);
            var clearedReference = false;
            if (clear)
            {
                // Every generated package is cloned from the reference package, so clearing it once
                // clears them all.
                var cleared = WithoutSensitiveValues(template);
                template = cleared.Item1;
                clearedReference = cleared.Item2;
            }
            // A "Stage <Table>" Execute SQL task in the reference is the staging template; the rest
            // of the generator works on the package without it.
            var split = Stage.SplitReference(template.PackageXml, template.ReferencePackage, template.Directory);
            var flowXml = split.Item1;
            var stageParts = split.Item2;
            var inferred = Reference.Infer(flowXml, template.ReferencePackage, manifest.StagingPrefix, manifest.Table(options.ReferenceTable ?? ""));
            var hint = (string.IsNullOrEmpty(options.ReferenceTable) ? manifest.Table(inferred.Item1.LogicalName) : null)
                       ?? TemplateHint(template.Directory, inferred.Item1.LogicalName);
            if (hint != null)
                // The manifest's entry for the table the package migrates (or the template's own
                // template.json, when that table is not migrated) supplies its primary name and match
                // key; the package alone cannot always tell them apart.
                inferred = Reference.Infer(flowXml, template.ReferencePackage, manifest.StagingPrefix, hint);
            var reference = inferred.Item1;
            var taskName = inferred.Item2;
            if (!string.IsNullOrEmpty(options.ReferenceTable) && options.ReferenceTable.ToLowerInvariant() != reference.LogicalName.ToLowerInvariant())
                throw new GeneratorException("Reference package " + template.ReferencePackage + " migrates " + Py.Repr(reference.LogicalName)
                                             + ", not " + Py.Repr(options.ReferenceTable));
            var projectName = ResolveProjectName(template, options.ProjectName);
            var support = Support.Load(template.Directory, template.Doc, template.PackageXml, template.ReferencePackage,
                                       MetadataProperty(template.Doc, template.ReferencePackage, "ProtectionLevel"));
            if (clear)
            {
                if (support.ProtectionLevel != Support.DontSaveSensitive) support.ConvertedFrom = support.ProtectionLevel;
                support.ProtectionLevel = support.PackageProtectionLevel = Support.DontSaveSensitive;
                support.EncryptedFiles = new List<string>();
            }
            var stagingConnection = StagingConnection(flowXml, support, template.ReferencePackage);
            var prefixes = Stage.PublisherPrefixes(manifest.Tables);
            // The template's sample table may use another publisher prefix than the migrated tables
            // (the built-in template uses new_), so its own prefix counts when reading its SQL.
            var templatePrefixes = new HashSet<string>(prefixes, StringComparer.Ordinal);
            templatePrefixes.UnionWith(Stage.PublisherPrefixes(new[] { reference }));
            var stageTemplate = stageParts != null ? Stage.ParseTemplate(stageParts, reference, templatePrefixes, template.ReferencePackage) : null;
            var byName = new Dictionary<string, Table>(StringComparer.Ordinal);
            foreach (var t in manifest.Tables) byName[t.LogicalName.ToLowerInvariant()] = t;

            var selected = SelectTables(manifest, options.Tables);
            var units = Grouping.SelectGroups(manifest.Groups, selected);
            var assumedLoaded = Orchestration.CheckDependencies(manifest, selected, units, options.AllowMissingDependencies);
            var skipped = new List<KeyValuePair<string, string>>();
            foreach (var t in selected)
            {
                var reason = SkipReason(t);
                if (reason != null) skipped.Add(new KeyValuePair<string, string>(t.LogicalName, reason));
            }
            var skippedNames = new HashSet<string>(skipped.Select(s => s.Key), StringComparer.Ordinal);
            var migrated = new HashSet<string>(selected.Select(t => t.LogicalName).Where(n => !skippedNames.Contains(n)), StringComparer.Ordinal);
            string seedFile;
            Dictionary<string, List<Dictionary<string, object>>> seedRows = null;
            if (inMemory)
            {
                seedFile = options.MetaSeedText != null ? MetaSeedName : null;
                if (seedFile != null) seedRows = MetaSeed.LoadText(options.MetaSeedText, MetaSeedName);
            }
            else
            {
                var seedPath = !string.IsNullOrEmpty(options.MetaSeed) ? options.MetaSeed : Path.Combine(manifestDir, MetaSeedName);
                seedFile = !string.IsNullOrEmpty(options.MetaSeed) || File.Exists(seedPath) ? seedPath : null;
                if (seedFile != null) seedRows = MetaSeed.Load(seedFile);
            }
            var allPasses = Deferred.Plan(manifest, selected, seedRows);
            var skippedPasses = allPasses.Where(p => !migrated.Contains(p.Table.LogicalName)).ToList();
            var passes = allPasses.Where(p => migrated.Contains(p.Table.LogicalName)).ToList();

            // Render everything before touching the output folder so bad input never leaves a
            // half-written project behind.
            var seed = template.Stem;
            var label = template.ReferencePackage;
            var xml = flowXml;
            var full = new Dictionary<int, Group>();
            foreach (var g in manifest.Groups) full[g.FileNumber] = g;
            var guidSql = new Dictionary<int, string>();
            var dbColumns = new Dictionary<string, List<Tuple<string, string>>>(StringComparer.Ordinal);
            foreach (var unit in units)
            {
                var dropped = full[unit.FileNumber].Tables.Where(t => !unit.Tables.Contains(t)).ToList();
                guidSql[unit.FileNumber] = Harness.FilterSql(ReadScript(options, manifestDir, unit.GuidFile), unit.Tables, dropped);
                foreach (var name in new[] { unit.GuidFile, unit.StagingFile })
                    foreach (var kv in Harness.ScriptTables(ReadScript(options, manifestDir, name))) dbColumns[kv.Key] = kv.Value;
            }

            var setupTasks = new List<SqlTask>
            {
                new SqlTask("Create UpdateTime Table", Harness.UpdateTimeSql),
                // Stage SQL joins the GUID tables, so they must exist before any staging package runs;
                // every harness package also creates its own group's (both only create missing tables).
                new SqlTask("Create GUID Tables", string.Join("\n", units.Select(u => guidSql[u.FileNumber]))),
            };
            var errorSql = Harness.ErrorTableSql(xml);
            if (errorSql != null) setupTasks.Insert(0, new SqlTask("Create Error Table", errorSql));
            var setup = Harness.RenderSqlPackage(xml, label, reference, Harness.SetupName, seed, setupTasks, stagingConnection.Dtsid);

            var output = options.OutputDir;
            var queries = Path.Combine(Path.GetFullPath(output), QueriesDir);
            var stageFiles = new List<KeyValuePair<string, string>>();
            var stageNotes = new Dictionary<string, JObj>(StringComparer.Ordinal);
            var stageNoteOrder = new List<string>();
            var pairs = new List<Tuple<Group, RenderedPackage, RenderedPackage>>();   // (group, staging package, harness package or null)
            foreach (var unit in units)
            {
                var dropped = full[unit.FileNumber].Tables.Where(t => !unit.Tables.Contains(t)).ToList();
                var loaded = unit.Tables.Where(t => migrated.Contains(t.LogicalName)).ToList();
                var stageItems = new List<StageItem>();
                if (stageTemplate != null)
                {
                    var unitLabels = Harness.TaskLabels(loaded);
                    foreach (var table in loaded)
                    {
                        var staged = Stage.StageSqlFor(stageTemplate, reference, table, byName, prefixes);
                        if (stageFiles.Any(f => f.Key.ToLowerInvariant() == staged.FileName.ToLowerInvariant()))
                            throw new GeneratorException("Two tables would write the staging SQL file " + staged.FileName);
                        stageFiles.Add(new KeyValuePair<string, string>(staged.FileName, staged.Sql));
                        if (!stageNotes.ContainsKey(table.LogicalName)) stageNoteOrder.Add(table.LogicalName);
                        stageNotes[table.LogicalName] = new JObj()
                            .Add("task", Stage.TaskPrefix + unitLabels[table.LogicalName])
                            .Add("file", QueriesDir + "/" + staged.FileName)
                            .Add("unresolvedLookups", staged.Unresolved.Cast<object>().ToList())
                            .Add("assumptions", staged.Assumptions.Cast<object>().ToList());
                        stageItems.Add(new StageItem
                        {
                            TaskName = Stage.TaskPrefix + unitLabels[table.LogicalName], ManagerName = staged.FileName,
                            SqlPath = Path.Combine(queries, staged.FileName),
                        });
                    }
                }
                var scripts = new List<SqlTask> { new SqlTask("Create Staging Tables", Harness.FilterSql(ReadScript(options, manifestDir, unit.StagingFile), unit.Tables, dropped)) };
                var staging = Harness.RenderSqlPackage(xml, label, reference, Harness.StagingName(unit.FileNumber), seed, scripts,
                                                       stagingConnection.Dtsid, stageTemplate, stageItems);
                var harness = loaded.Count > 0
                    ? Harness.RenderHarness(xml, label, reference, taskName, loaded, Harness.HarnessName(unit.FileNumber), seed, dbColumns,
                                            new SqlTask("Create GUID Tables", guidSql[unit.FileNumber]), stagingConnection.Dtsid)
                    : null;
                pairs.Add(Tuple.Create(unit, staging, harness));
            }
            RenderedPackage deferred = null;
            var deferredNotes = new List<KeyValuePair<string, List<string>>>();
            if (passes.Count > 0)
            {
                var number = manifest.Groups.Max(g => g.FileNumber) + 1;
                Func<DeferredPass, Table, string> sourceSql = null;
                if (stageTemplate != null)
                    sourceSql = (p, view) =>
                    {
                        var r = Stage.DeferredSourceSql(stageTemplate, p.Table, view.Columns,
                                                        new HashSet<string>(p.Columns.Select(c => c.Name.ToLowerInvariant()), StringComparer.Ordinal), byName, prefixes);
                        var i = deferredNotes.FindIndex(kv => kv.Key == p.Table.LogicalName);
                        var entry = new KeyValuePair<string, List<string>>(p.Table.LogicalName, r.Item2);
                        if (i >= 0) deferredNotes[i] = entry; else deferredNotes.Add(entry);
                        return r.Item1;
                    };
                deferred = Harness.RenderDeferred(xml, label, reference, passes, Harness.DeferredName(number), seed, dbColumns, sourceSql);
            }

            var harnessNames = pairs.Where(p => p.Item3 != null).Select(p => p.Item3.PackageName).ToList();
            // With a staging template the staging packages load staging from the legacy database, so
            // each group runs staging then harness. Without one, staging is loaded outside the
            // project and running a staging package would empty it, so only harnesses are run.
            var chains = pairs.Select(p => (stageTemplate != null ? new[] { p.Item2.PackageName } : new string[0])
                                           .Concat(p.Item3 != null ? new[] { p.Item3.PackageName } : new string[0]).ToList()).ToList();
            var run = pairs.Zip(chains, (p, c) => Tuple.Create(p.Item1, c)).Where(x => x.Item2.Count > 0).ToList();
            var stages = new List<RunStage> { new RunStage(Orchestration.SetupStage, new List<List<string>> { new List<string> { Harness.SetupName } }) };
            stages.AddRange(Orchestration.ExecutionStages(run.Select(x => x.Item1).ToList(), run.Select(x => x.Item2).ToList(),
                                                          deferred != null ? new List<string> { deferred.PackageName } : new List<string>()));
            var orchestrator = Orchestration.Render(xml, label, reference, stages, Orchestration.OrchestratorName, seed);
            var called = new HashSet<string>(stages.SelectMany(s => s.Packages).Select(n => n + ".dtsx"), StringComparer.Ordinal);
            if (!new HashSet<string>(Orchestration.ReferencedPackages(orchestrator.Xml), StringComparer.Ordinal).SetEquals(called))
                throw new GeneratorException("Orchestrator does not reference exactly the generated packages");

            var packages = new List<RenderedPackage> { setup };
            foreach (var p in pairs)
            {
                packages.Add(p.Item2);
                if (p.Item3 != null) packages.Add(p.Item3);
            }
            if (deferred != null) packages.Add(deferred);
            packages.Add(orchestrator);
            if (options.KeepReference && packages.Any(p => (p.PackageName + ".dtsx").ToLowerInvariant() == template.ReferencePackage.ToLowerInvariant()))
                throw new GeneratorException("Keep reference: the reference package " + template.ReferencePackage + " has the same name "
                                             + "as a generated package; rename it in the reference project to keep it");
            var connections = new Dictionary<string, JObj>(StringComparer.Ordinal);
            foreach (var p in packages) connections[p.PackageName] = Support.VerifyPackage(p.Xml, support, p.PackageName);
            if (options.KeepReference) Support.VerifyPackage(template.PackageXml, support, template.ReferencePackage);
            var dtproj = RenderDtproj(template, packages, options.KeepReference, projectName, clear);
            var database = RenderDatabase(template, projectName);
            var carried = SupportFiles(template, support, options.KeepReference);
            var rewritten = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (clear)
            {
                if (clearedReference) support.ClearedFiles.Add(template.ReferencePackage + " (the template of every generated package)");
                if (options.KeepReference) rewritten[template.ReferencePackage] = template.PackageXml;
                foreach (var name in carried)
                {
                    if (rewritten.ContainsKey(name)) continue;
                    var doc = Xml.ParseFile(Path.Combine(template.Directory, name));
                    if (Support.StripSensitive(doc) > 0)
                    {
                        rewritten[name] = Xml.ToXml(doc);
                        support.ClearedFiles.Add(name);
                    }
                }
            }

            var files = packages.Select(p => new KeyValuePair<string, byte[]>(p.PackageName + ".dtsx", p.Xml)).ToList();
            if (options.KeepReference) files.Add(new KeyValuePair<string, byte[]>(template.ReferencePackage, template.PackageXml));
            var labels = new Dictionary<int, Dictionary<string, string>>();
            foreach (var p in pairs) labels[p.Item1.FileNumber] = Harness.TaskLabels(p.Item1.Tables.Where(t => migrated.Contains(t.LogicalName)));
            var scopes = pairs.Where(p => p.Item3 != null)
                .Select(p => new KeyValuePair<string, List<Tuple<string, Table>>>(p.Item3.PackageName + ".dtsx",
                    p.Item1.Tables.Where(t => migrated.Contains(t.LogicalName))
                     .Select(t => Tuple.Create("Migrate " + labels[p.Item1.FileNumber][t.LogicalName], t)).ToList())).ToList();
            if (deferred != null)
            {
                var deferredLabels = Harness.TaskLabels(passes.Select(p => p.Table));
                scopes.Add(new KeyValuePair<string, List<Tuple<string, Table>>>(deferred.PackageName + ".dtsx",
                    passes.Select(p => Tuple.Create("Update " + deferredLabels[p.Table.LogicalName] + " Lookups", Harness.DeferredView(p.Table, p.UpdateColumns))).ToList()));
            }
            var checks = Audit.AuditProject(dtproj.Item1, files, support.Connections.Select(c => c.File).ToList(), scopes, reference,
                                            Orchestration.OrchestratorName + ".dtsx", called,
                                            new HashSet<string>(packages.Select(p => p.PackageName + ".dtsx"), StringComparer.Ordinal));

            PrepareOutputDir(output, template.Directory);
            foreach (var pkg in packages) File.WriteAllBytes(Path.Combine(output, pkg.PackageName + ".dtsx"), pkg.Xml);
            if (stageFiles.Count > 0)
            {
                System.IO.Directory.CreateDirectory(queries);
                foreach (var f in stageFiles) Metadata.WriteText(Path.Combine(queries, f.Key), f.Value);
            }
            foreach (var name in carried)
            {
                if (File.Exists(Path.Combine(output, name)))
                    throw new GeneratorException("Template file " + name + " would overwrite a generated file of the same name");
                if (rewritten.ContainsKey(name)) File.WriteAllBytes(Path.Combine(output, name), rewritten[name]);
                else
                    // Verbatim copies keep connection settings, expressions, bindings and encrypted values intact.
                    File.Copy(Path.Combine(template.Directory, name), Path.Combine(output, name));
            }
            if (database != null) File.WriteAllBytes(Path.Combine(output, database.Item1), database.Item2);
            var projectFile = Path.Combine(output, projectName + ".dtproj");
            File.WriteAllBytes(projectFile, dtproj.Item1);

            var orchestration = new JObj()
                .Add("package", Orchestration.OrchestratorName).Add("file", Orchestration.OrchestratorName + ".dtsx").Add("dtsid", orchestrator.Dtsid)
                .Add("stages", stages.Select((s, i) => (object)new JObj()
                    .Add("name", s.Name)
                    .Add("packages", s.Packages.Select(n => (object)(n + ".dtsx")).ToList())
                    .Add("chains", s.Chains.Select(c => (object)c.Select(n => (object)(n + ".dtsx")).ToList()).ToList())
                    .Add("runsAfter", i > 0 ? stages[i - 1].Name : null)).ToList())
                .Add("assumedLoaded", assumedLoaded.Cast<object>().ToList())
                .Add("metaSeed", seedFile != null ? Path.GetFileName(seedFile) : null)
                .Add("connections", connections[Orchestration.OrchestratorName])
                .Add("note", stageTemplate != null
                    ? "Each group runs its staging package (recreate and load staging from the legacy database) and then its harness package."
                    : "Staging packages (NNa) are not run: they drop and recreate the staging tables, which must be loaded from the legacy "
                      + "source between running them and running the harness.");
            var result = new GenerationResult { OutputDir = output, ProjectFile = projectFile, ProjectName = projectName, Orchestration = orchestration,
                                                ProtectionChangedFrom = support.ConvertedFrom };
            result.Setup = new JObj().Add("package", Harness.SetupName).Add("file", Harness.SetupName + ".dtsx").Add("dtsid", setup.Dtsid)
                                     .Add("tasks", setupTasks.Select(t => (object)t.Name).ToList()).Add("connections", connections[Harness.SetupName]);
            var skippedMap = skipped.ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
            foreach (var p in pairs)
            {
                var unit = p.Item1;
                var staging = p.Item2;
                var harness = p.Item3;
                result.Packages.Add(new JObj()
                    .Add("fileNumber", unit.FileNumber).Add("tier", unit.Tier).Add("tierPart", unit.TierPart)
                    .Add("tierTotalParts", unit.TierTotalParts).Add("stagingFile", unit.StagingFile).Add("guidFile", unit.GuidFile)
                    .Add("stagingPackage", staging.PackageName + ".dtsx").Add("stagingDtsid", staging.Dtsid)
                    .Add("package", harness != null ? harness.PackageName : null)
                    .Add("file", harness != null ? harness.PackageName + ".dtsx" : null)
                    .Add("dtsid", harness != null ? harness.Dtsid : null)
                    .Add("warnings", harness != null ? harness.Warnings.Cast<object>().ToList() : new List<object>())
                    .Add("tables", unit.Tables.Select(t =>
                    {
                        string reason;
                        skippedMap.TryGetValue(t.LogicalName, out reason);
                        JObj note;
                        stageNotes.TryGetValue(t.LogicalName, out note);
                        return (object)new JObj()
                            .Add("table", t.LogicalName).Add("stagingTable", t.StagingTable).Add("guidTable", t.GuidTable)
                            .Add("task", migrated.Contains(t.LogicalName) ? "Migrate " + labels[unit.FileNumber][t.LogicalName] : null)
                            .Add("matchKey", migrated.Contains(t.LogicalName) ? t.MatchKeys[0] : null)
                            .Add("columns", t.PackageColumns.Select(c => (object)c.Name).ToList())
                            .Add("skippedColumns", t.SkippedColumns.Select(c => (object)c.Name).ToList())
                            .Add("stage", note)
                            .Add("skipped", reason);
                    }).ToList())
                    .Add("connections", harness != null ? connections[harness.PackageName] : connections[staging.PackageName]));
            }
            if (deferred != null)
            {
                var passLabels = Harness.TaskLabels(passes.Select(q => q.Table));
                foreach (var p in passes)
                {
                    var entry = Deferred.ReportEntry(p, deferred.PackageName);
                    var notes = deferredNotes.Where(kv => kv.Key == p.Table.LogicalName).Select(kv => kv.Value).FirstOrDefault() ?? new List<string>();
                    entry.Add("task", "Update " + passLabels[p.Table.LogicalName] + " Lookups")
                         .Add("dtsid", deferred.Dtsid).Add("connections", connections[deferred.PackageName])
                         .Add("unresolvedLookups", notes.Cast<object>().ToList())
                         .Add("warnings", p.MetaEnabled == false
                             ? new List<object> { "meta_seed.sql marks " + p.Table.LogicalName + " PassNo 2 disabled (IsEnabled = 0)" }
                             : new List<object>());
                    result.Deferred.Add(entry);
                }
            }

            result.Summary = new JObj()
                .Add("harnessPackages", harnessNames.Count).Add("stagingPackages", pairs.Count).Add("tables", selected.Count)
                .Add("migratedTables", migrated.Count).Add("skippedTables", skipped.Count).Add("deferredPasses", passes.Count)
                .Add("entryPoint", (string)orchestration.Get("file"))
                .Add("projectPackages", Py.SortedLower(files.Select(f => f.Key)).Cast<object>().ToList())
                .Add("projectConnectionManagers", Py.SortedLower(support.Connections.Select(c => c.File)).Cast<object>().ToList());
            result.Membership = result.Packages.Select(p => new JObj()
                .Add("package", p.Get("file")).Add("stagingPackage", p.Get("stagingPackage")).Add("tier", p.Get("tier"))
                .Add("tables", ((List<object>)p.Get("tables")).Cast<JObj>().Where(t => t.Get("skipped") == null).Select(t => t.Get("table")).ToList())).ToList();
            var issues = new List<string>();
            issues.AddRange(skipped.Select(s => s.Key + " skipped: " + s.Value));
            issues.AddRange(skippedPasses.Select(p => p.Table.LogicalName + ": deferred lookups " + string.Join(", ", p.Columns.Select(c => c.Name))
                                                      + " not generated because the table itself is skipped"));
            foreach (var t in selected.Where(t => migrated.Contains(t.LogicalName)))
                foreach (var d in t.Dependencies)
                    if (skippedNames.Contains(d) && d != t.LogicalName)
                        issues.Add(t.LogicalName + " depends on skipped " + d + "; its lookups to " + d + " have no migrated records");
            foreach (var p in result.Packages.Where(p => p.Get("file") != null))
                issues.AddRange(((List<object>)p.Get("warnings")).Select(w => p.Get("file") + ": " + w));
            foreach (var d in result.Deferred) issues.AddRange(((List<object>)d.Get("warnings")).Select(w => d.Get("file") + ": " + w));
            issues.AddRange(assumedLoaded.Select(m => m.Get("table") + " depends on unselected " + m.Get("dependency") + "; assumed already loaded"));
            foreach (var n in stageNoteOrder) issues.AddRange(((List<object>)stageNotes[n].Get("unresolvedLookups")).Select(x => stageNotes[n].Get("file") + ": " + x));
            foreach (var n in stageNoteOrder) issues.AddRange(((List<object>)stageNotes[n].Get("assumptions")).Select(x => stageNotes[n].Get("file") + ": " + x));
            if (deferred != null)
                foreach (var kv in deferredNotes) issues.AddRange(kv.Value.Select(x => deferred.PackageName + ".dtsx (" + kv.Key + "): " + x));
            issues.AddRange(Support.CredentialNotes(support).Where(n => n.Contains("differs from the project's")));
            result.Issues = issues;

            var listed = PackageEntries(template.Doc).Select(p => Xml.GetNs(p, Xml.SsisNs, "Name")).ToList();
            var databasePath = DatabasePath(template);
            var report = new JObj()
                .Add("generator", options.Generator)
                .Add("manifest", manifestName)
                .Add("referenceProject", Path.GetFileName(template.DtprojPath))
                .Add("referencePackage", template.ReferencePackage)
                .Add("reference", new JObj().Add("table", reference.LogicalName).Add("task", taskName).Add("stagingTable", reference.StagingTable)
                                            .Add("guidTable", reference.GuidTable).Add("recordId", reference.PrimaryId)
                                            .Add("matchKey", reference.MatchKeys[0]).Add("columns", reference.Columns.Select(c => (object)c.Name).ToList()))
                .Add("summary", result.Summary)
                .Add("groupMembership", result.Membership.Cast<object>().ToList())
                .Add("unresolvedIssues", result.Issues.Cast<object>().ToList())
                .Add("validation", new JObj().Add("outputChecks", checks.Cast<object>().ToList()).Add("buildValidated", false).Add("note", BuildNote))
                .Add("project", new JObj()
                    .Add("name", projectName)
                    .Add("file", Path.GetFileName(projectFile))
                    .Add("id", ProjectId(template, projectName))
                    .Add("keptReferencePackage", options.KeepReference)
                    .Add("omittedReferencePackages", listed.Where(n => n.ToLowerInvariant() != template.ReferencePackage.ToLowerInvariant() || !options.KeepReference).Cast<object>().ToList())
                    .Add("carriedFiles", Py.SortedLower(carried.Concat(database != null ? new[] { database.Item1 } : new string[0])).Cast<object>().ToList())
                    .Add("omittedReferenceFiles", Py.SortedLower(System.IO.Directory.GetFiles(template.Directory).Select(Path.GetFileName)
                        .Where(n => !carried.Contains(n) && n != Path.GetFileName(template.DtprojPath) && n != databasePath)).Cast<object>().ToList())
                    .Add("configurationSettings", dtproj.Item2))
                .Add("connectionManagers", support.Connections.Select(c => (object)new JObj()
                    .Add("file", c.File).Add("name", c.Name).Add("dtsid", c.Dtsid).Add("creationName", c.CreationName)
                    .Add("expressionProperties", c.ExpressionProperties.Cast<object>().ToList()).Add("encryptedValues", c.Encrypted && !support.ClearedFiles.Contains(c.File))).ToList())
                .Add("protection", ProtectionReport(support))
                .Add("setup", result.Setup)
                .Add("groups", result.Packages.Cast<object>().ToList())
                .Add("orchestration", orchestration)
                .Add("deferredUpdates", result.Deferred.Cast<object>().ToList());
            Metadata.WriteText(Path.Combine(output, ReportName), Json.Dumps(report) + "\n");

            var expected = new HashSet<string>(files.Select(f => f.Key).Concat(carried).Concat(new[] { Path.GetFileName(projectFile), ReportName }), StringComparer.OrdinalIgnoreCase);
            if (database != null) expected.Add(database.Item1);
            if (stageFiles.Count > 0) expected.Add(QueriesDir);
            var written = new HashSet<string>(System.IO.Directory.GetFileSystemEntries(output).Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            if (!written.SetEquals(expected))
            {
                var diff = Py.Sorted(written.Where(w => !expected.Contains(w)).Concat(expected.Where(e => !written.Contains(e))));
                throw new GeneratorException("Output folder " + output + " holds unexpected files: " + (diff.Count > 0 ? string.Join(", ", diff) : "(none)"));
            }
            return result;
        }

        public const string TemplateHintName = "template.json";

        /// <summary>The reference table's primary name and match key from template.json next to the
        /// .dtproj, for a template whose sample table is not in the manifest (the built-in one).</summary>
        internal static Table TemplateHint(string directory, string logicalName)
        {
            var path = Path.Combine(directory, TemplateHintName);
            if (!File.Exists(path)) return null;
            JObj data;
            try { data = Json.Parse(Metadata.ReadText(path)) as JObj; }
            catch (Exception ex) when (!(ex is GeneratorException)) { throw new GeneratorException(path + " is not valid JSON: " + ex.Message, ex); }
            if (data == null) throw new GeneratorException(path + " is not valid JSON: expected an object");
            if (((data.Get("table") as string) ?? "").ToLowerInvariant() != logicalName.ToLowerInvariant()) return null;
            var primaryName = data.Get("primaryName") as string;
            var matchKey = data.Get("matchKey") as string;
            return new Table
            {
                LogicalName = logicalName, SchemaName = "", DisplayName = "", Tier = 0, StagingTable = "", GuidTable = "", PrimaryId = "",
                PrimaryName = string.IsNullOrEmpty(primaryName) ? null : primaryName,
                MatchKeys = string.IsNullOrEmpty(matchKey) ? new List<string>() : new List<string> { matchKey },
            };
        }

        private static List<XmlElement> PackageEntries(XmlDocument doc)
        {
            return Xml.ByTagNs(doc, Xml.SsisNs, "Package").Where(el => el.ParentNode != null && el.ParentNode.LocalName == "Packages").ToList();
        }

        /// <summary>Why name cannot be used for the generated project, or null.</summary>
        public static string ProjectNameProblem(string name, params string[] referenceNames)
        {
            if (!SafeProjectName.IsMatch(name ?? ""))
                return "Project name " + Py.Repr(name) + " may only contain letters, digits, spaces, '_', '-' and '.', and must not end with a space or '.'";
            if (referenceNames.Where(n => !string.IsNullOrEmpty(n)).Any(n => n.Trim().ToLowerInvariant() == name.ToLowerInvariant()))
                return "Project name " + Py.Repr(name) + " must differ from the reference project's name";
            return null;
        }

        private static string ResolveProjectName(Template template, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) name = template.Stem + DefaultProjectSuffix;
            var problem = ProjectNameProblem(name, template.Stem, ProjectPropertyText(template.Doc, "Name") ?? "");
            if (problem != null) throw new GeneratorException(problem);
            return name;
        }

        private static string ProjectId(Template template, string projectName)
        {
            return Package.DeterministicGuid(template.Stem, projectName, "project");
        }

        private static XmlElement ProjectProperty(XmlDocument doc, string key)
        {
            foreach (var prop in Xml.ByTagNs(doc, Xml.SsisNs, "Property"))
            {
                var owner = prop.ParentNode != null ? prop.ParentNode.ParentNode : null;
                if (owner != null && owner.NamespaceURI == Xml.SsisNs && owner.LocalName == "Project" && Xml.GetNs(prop, Xml.SsisNs, "Name") == key) return prop;
            }
            return null;
        }

        private static string ProjectPropertyText(XmlDocument doc, string key)
        {
            var prop = ProjectProperty(doc, key);
            return prop != null && prop.FirstChild != null ? Xml.Data(prop.FirstChild) : null;
        }

        private static string MetadataProperty(XmlDocument doc, string packageFile, string key)
        {
            foreach (var meta in Xml.ByTagNs(doc, Xml.SsisNs, "PackageMetaData"))
            {
                if (Xml.GetNs(meta, Xml.SsisNs, "Name").ToLowerInvariant() != packageFile.ToLowerInvariant()) continue;
                foreach (var prop in Xml.ByTagNs(meta, Xml.SsisNs, "Property"))
                    if (prop.ParentNode.ParentNode == meta && Xml.GetNs(prop, Xml.SsisNs, "Name") == key)
                        return prop.FirstChild != null ? Xml.Data(prop.FirstChild) : "";
            }
            return null;
        }

        private static string DatabasePath(Template template)
        {
            foreach (var database in Xml.ByTag(template.Doc.DocumentElement, "Database"))
            {
                if (database.ParentNode != template.Doc.DocumentElement) continue;
                var paths = Xml.ByTag(database, "FullPath");
                if (paths.Count == 0) paths = Xml.ByTag(database, "Name");
                if (paths.Count > 0 && paths[0].FirstChild != null) return Xml.Data(paths[0].FirstChild).Trim();
            }
            return null;
        }

        /// <summary>The project's .database file, renamed and re-identified for the new project.</summary>
        private static Tuple<string, byte[]> RenderDatabase(Template template, string projectName)
        {
            var path = DatabasePath(template);
            if (path == null) return null;
            var source = Support.LocalFile(template.Directory, path, "Project database file");
            XmlDocument doc;
            try { doc = Xml.ParseFile(source); }
            catch (XmlException ex) { throw new GeneratorException("Project database file is not well-formed XML (" + Path.GetFileName(source) + "): " + ex.Message, ex); }
            foreach (var child in Xml.Children(doc.DocumentElement))
                if (child.LocalName == "ID" || child.LocalName == "Name") Xml.SetText(child, projectName);
            return Tuple.Create(projectName + ".database", Xml.ToXml(doc));
        }

        /// <summary>Files carried verbatim: project connection managers, Project.params, and the reference
        /// package only when it is explicitly kept. Legacy packages are not carried.</summary>
        private static JObj ProtectionReport(ProjectSupport support)
        {
            var report = new JObj().Add("projectProtectionLevel", support.ProtectionLevel)
                                   .Add("packageProtectionLevel", support.PackageProtectionLevel)
                                   .Add("credentialNotes", Support.CredentialNotes(support).Cast<object>().ToList());
            if (support.ConvertedFrom != null) report.Add("changedFrom", support.ConvertedFrom);
            return report;
        }

        /// <summary>The template with its package switched to DontSaveSensitive (level 0) and its stored
        /// sensitive values removed; true when there were any.</summary>
        private static Tuple<Template, bool> WithoutSensitiveValues(Template template)
        {
            var doc = Xml.Parse(template.PackageXml);
            var removed = Support.StripSensitive(doc);
            Xml.SetNs(doc.DocumentElement, "DTS", "ProtectionLevel", Xml.DtsNs, "0");
            var copy = new Template { DtprojPath = template.DtprojPath, Doc = template.Doc, ReferencePackage = template.ReferencePackage,
                                      PackageXml = Xml.ToXml(doc) };
            return Tuple.Create(copy, removed > 0);
        }

        private static List<string> SupportFiles(Template template, ProjectSupport support, bool keepReference)
        {
            var names = support.Connections.Select(c => c.File).ToList();
            if (support.ParamsFile != null) names.Add(support.ParamsFile);
            if (keepReference) names.Add(template.ReferencePackage);
            return Py.SortedLower(names.Distinct(StringComparer.Ordinal));
        }

        private static Tuple<byte[], JObj> RenderDtproj(Template template, List<RenderedPackage> packages, bool keepReference, string projectName,
                                                       bool clear = false)
        {
            var doc = (XmlDocument)template.Doc.CloneNode(true);
            var refName = template.ReferencePackage.ToLowerInvariant();

            foreach (var kv in new[] { Tuple.Create("ID", ProjectId(template, projectName)), Tuple.Create("Name", projectName) })
            {
                var prop = ProjectProperty(doc, kv.Item1);
                if (prop != null) Xml.SetText(prop, kv.Item2);
            }
            foreach (var database in Xml.ByTag(doc.DocumentElement, "Database"))
                if (database.ParentNode == doc.DocumentElement)
                    foreach (var child in Xml.Children(database))
                        if (child.LocalName == "Name" || child.LocalName == "FullPath") Xml.SetText(child, projectName + ".database");
            // <State> caches the reference project's serialized identity and package list.
            foreach (var state in Xml.ByTag(doc.DocumentElement, "State")) RemoveWithIndent(state);

            // Only the generated packages (plus the reference, when kept) belong to the new project.
            foreach (var entryEl in PackageEntries(doc))
                if (Xml.GetNs(entryEl, Xml.SsisNs, "Name").ToLowerInvariant() != refName) RemoveWithIndent(entryEl);
            foreach (var metaEl in Xml.ByTagNs(doc, Xml.SsisNs, "PackageMetaData"))
                if (Xml.GetNs(metaEl, Xml.SsisNs, "Name").ToLowerInvariant() != refName) RemoveWithIndent(metaEl);

            var entry = PackageEntries(doc).First(e => Xml.GetNs(e, Xml.SsisNs, "Name").ToLowerInvariant() == refName);
            InsertClones(doc, entry, packages, (clone, pkg) => Xml.SetNs(clone, entry.Prefix, "Name", Xml.SsisNs, pkg.PackageName + ".dtsx"));
            if (!keepReference) RemoveWithIndent(entry);

            var meta = Xml.ByTagNs(doc, Xml.SsisNs, "PackageMetaData").FirstOrDefault(m => Xml.GetNs(m, Xml.SsisNs, "Name").ToLowerInvariant() == refName);
            if (meta != null)
            {
                InsertClones(doc, meta, packages, (clone, pkg) =>
                {
                    Xml.SetNs(clone, meta.Prefix, "Name", Xml.SsisNs, pkg.PackageName + ".dtsx");
                    var parameterIds = Support.PackageParameterIds(pkg.Xml);
                    // "CM.<manager>.<property>" entries describe package connection managers; keep
                    // only those of managers the generated package still has.
                    var managers = Support.PackageConnectionNames(pkg.Xml);
                    foreach (var param in Xml.ByTagNs(clone, Xml.SsisNs, "Parameter"))
                    {
                        var name = Xml.GetNs(param, Xml.SsisNs, "Name");
                        if (!name.StartsWith("CM.", StringComparison.Ordinal)) continue;
                        var rest = name.Substring(3);
                        var dot = rest.LastIndexOf('.');
                        if (!managers.Contains(dot >= 0 ? rest.Substring(0, dot) : rest)) RemoveWithIndent(param);
                    }
                    foreach (var prop in Xml.ByTagNs(clone, Xml.SsisNs, "Property"))
                    {
                        var key = Xml.GetNs(prop, Xml.SsisNs, "Name");
                        var owner = (XmlElement)prop.ParentNode.ParentNode;
                        string value = null;
                        if (owner == clone)
                            value = key == "ID" ? pkg.Dtsid : key == "Name" ? pkg.PackageName : key == "VersionGUID" ? pkg.VersionGuid : null;
                        else if (owner.LocalName == "Parameter" && key == "ID" && !Xml.GetNs(owner, Xml.SsisNs, "Name").StartsWith("CM.", StringComparison.Ordinal))
                        {
                            var name = Xml.GetNs(owner, Xml.SsisNs, "Name");
                            value = parameterIds.Where(kv => kv.Key == name).Select(kv => kv.Value).FirstOrDefault();
                            if (string.IsNullOrEmpty(value))
                                throw new GeneratorException("Generated package " + pkg.PackageName + " has no package parameter " + Py.Repr(name)
                                                             + " listed in the reference project metadata");
                        }
                        if (value != null) Xml.SetText(prop, value);
                    }
                });
                if (!keepReference) RemoveWithIndent(meta);
            }
            var settings = RewriteConfigurationSettings(template, doc, packages, keepReference);
            if (clear)
            {
                foreach (var project in Xml.ByTagNs(doc, Xml.SsisNs, "Project"))
                    Xml.SetNs(project, project.Prefix, "ProtectionLevel", Xml.SsisNs, Support.DontSaveSensitive);
                foreach (var metaEl in Xml.ByTagNs(doc, Xml.SsisNs, "PackageMetaData"))
                    foreach (var prop in Xml.ByTagNs(metaEl, Xml.SsisNs, "Property"))
                        if (prop.ParentNode.ParentNode == metaEl && Xml.GetNs(prop, Xml.SsisNs, "Name") == "ProtectionLevel") Xml.SetText(prop, "0");
                Support.StripSensitive(doc);
            }
            return Tuple.Create(Xml.ToXml(doc), settings);
        }

        private static string ChildText(XmlElement el, string local)
        {
            var child = Xml.Children(el, local).FirstOrDefault();
            return child == null ? "" : string.Concat(child.ChildNodes.Cast<XmlNode>().Where(Xml.IsTextNode).Select(Xml.Data)).Trim();
        }

        private static void SetChildText(XmlElement el, string local, string value)
        {
            foreach (var child in Xml.Children(el, local)) Xml.SetText(child, value);
        }

        /// <summary>Keep project-level configuration values, re-point the reference package's values at each
        /// generated package, and drop values that belong to legacy packages or to the reference project's
        /// deployment target. Values are copied unchanged, never reported.</summary>
        private static JObj RewriteConfigurationSettings(Template template, XmlDocument doc, List<RenderedPackage> packages, bool keepReference)
        {
            var listed = PackageEntries(template.Doc).Select(p => Xml.GetNs(p, Xml.SsisNs, "Name")).ToList();
            var refFile = template.ReferencePackage;
            var refById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in Support.PackageParameterIds(template.PackageXml))
                if (!string.IsNullOrEmpty(kv.Value)) refById[kv.Value.ToUpperInvariant()] = kv.Key;
            var counts = new JObj().Add("projectLevel", 0).Add("generatedPackageSettings", 0).Add("droppedLegacyPackageSettings", 0).Add("droppedLastDeploymentPath", 0);
            Action<string, int> add = (k, n) => counts[k] = Convert.ToInt32(counts.Get(k)) + n;
            foreach (var setting in Xml.ByTag(doc.DocumentElement, "ConfigurationSetting"))
            {
                var name = ChildText(setting, "Name");
                var ident = ChildText(setting, "Id");
                if (name == "LastDeploymentPath" || ident == "LastDeploymentPath")
                {
                    // It targets the reference project in the catalog; deploying there would replace it.
                    RemoveWithIndent(setting);
                    add("droppedLastDeploymentPath", 1);
                    continue;
                }
                var owner = listed.FirstOrDefault(f => name.ToLowerInvariant().StartsWith(f.ToLowerInvariant() + ":", StringComparison.Ordinal));
                if (owner == null && refById.ContainsKey(ident.ToUpperInvariant())) owner = refFile;
                if (owner == null) { add("projectLevel", 1); continue; }
                if (owner.ToLowerInvariant() != refFile.ToLowerInvariant())
                {
                    RemoveWithIndent(setting);
                    add("droppedLegacyPackageSettings", 1);
                    continue;
                }
                var suffix = name.ToLowerInvariant().StartsWith(refFile.ToLowerInvariant() + ":", StringComparison.Ordinal) ? name.Substring(refFile.Length + 1) : name;
                string mapped;
                var parameter = refById.TryGetValue(ident.ToUpperInvariant(), out mapped) && !string.IsNullOrEmpty(mapped) ? mapped : suffix.Split(':').Last();
                InsertClones(doc, setting, packages, (clone, pkg) =>
                {
                    SetChildText(clone, "Name", pkg.PackageName + ".dtsx:" + suffix);
                    if (refById.ContainsKey(ident.ToUpperInvariant()))
                    {
                        var newId = Support.PackageParameterIds(pkg.Xml).Where(kv => kv.Key == parameter).Select(kv => kv.Value).FirstOrDefault();
                        if (string.IsNullOrEmpty(newId))
                            throw new GeneratorException("Generated package " + pkg.PackageName + " has no package parameter " + Py.Repr(parameter)
                                                         + " for a reference configuration value");
                        SetChildText(clone, "Id", newId);
                    }
                });
                add("generatedPackageSettings", packages.Count);
                if (!keepReference) RemoveWithIndent(setting);
            }
            return counts;
        }

        private static void InsertClones(XmlDocument doc, XmlElement anchor, List<RenderedPackage> packages, Action<XmlElement, RenderedPackage> fill)
        {
            var parent = anchor.ParentNode;
            var prev = anchor.PreviousSibling;
            var indent = prev != null && Xml.IsTextNode(prev) && Xml.Data(prev).Trim().Length == 0 ? Xml.Data(prev) : null;
            var following = anchor.NextSibling;
            foreach (var pkg in packages)
            {
                var clone = (XmlElement)anchor.CloneNode(true);
                fill(clone, pkg);
                if (indent != null) parent.InsertBefore(doc.CreateTextNode(indent), following);
                parent.InsertBefore(clone, following);
            }
        }

        private static void RemoveWithIndent(XmlElement el)
        {
            var parent = el.ParentNode;
            var prev = el.PreviousSibling;
            parent.RemoveChild(el);
            if (prev != null && Xml.IsTextNode(prev) && Xml.Data(prev).Trim().Length == 0) parent.RemoveChild(prev);
        }
    }
}
