using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace DataverseMigrationScaffolder.HarnessGen
{
    /// <summary>A Run_Migration stage: its name and chains of package names run in order.</summary>
    internal sealed class RunStage
    {
        public RunStage(string name, List<List<string>> chains) { Name = name; Chains = chains; }
        public string Name { get; }
        public List<List<string>> Chains { get; }
        public List<string> Packages { get { return Chains.SelectMany(c => c).ToList(); } }
    }

    /// <summary>
    /// Explicit execution order. The orchestrator package runs the setup package, then one
    /// sequence container per dependency tier, chained by Success constraints, then the deferred
    /// update pass. Inside a tier each scaffolder group is a chain (its staging package, when it
    /// loads staging, then its harness package) and the groups of one tier run side by side.
    /// </summary>
    internal static class Orchestration
    {
        public const string OrchestratorName = "Run_Migration";
        public const string SetupStage = "Setup";
        public const string DeferredStage = "Deferred_Updates";

        /// <summary>Every kept (non-dropped) dependency must load in an earlier tier than its dependent.
        /// Returns the unselected dependencies assumed to be loaded already (only when allowed).</summary>
        public static List<JObj> CheckDependencies(Manifest manifest, List<Table> selected, List<Group> groups, bool allowMissing)
        {
            var known = new HashSet<string>(manifest.Tables.Select(t => t.LogicalName.ToLowerInvariant()), StringComparer.Ordinal);
            var tierOf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var g in groups) foreach (var t in g.Tables) tierOf[t.LogicalName.ToLowerInvariant()] = g.Tier;
            var problems = new List<string>();
            var missing = new List<JObj>();
            foreach (var table in selected)
            {
                var name = table.LogicalName.ToLowerInvariant();
                var dropped = new HashSet<string>(table.DroppedDependencies.Select(d => d.ToLowerInvariant()), StringComparer.Ordinal);
                foreach (var dep in table.Dependencies)
                {
                    var key = dep.ToLowerInvariant();
                    if (key == name) continue;   // a self-lookup cannot be ordered; the scaffolder loads it in one pass
                    if (!known.Contains(key))
                        problems.Add(table.LogicalName + " depends on " + dep + ", which is not in the manifest (out-of-scope tables belong in externalDependencies)");
                    else if (!tierOf.ContainsKey(key))
                        missing.Add(new JObj().Add("table", table.LogicalName).Add("dependency", dep).Add("deferred", dropped.Contains(key)));
                    else if (!dropped.Contains(key) && tierOf[key] >= tierOf[name])
                        problems.Add(table.LogicalName + " (tier " + tierOf[name] + ") depends on " + dep + " (tier " + tierOf[key] + ") without a deferred update");
                }
            }
            if (problems.Count > 0) throw new GeneratorException("Dependency order cannot be enforced: " + string.Join("; ", problems));
            if (missing.Count > 0 && !allowMissing)
            {
                var pairs = string.Join(", ", missing.Select(m => m.Get("table") + " -> " + m.Get("dependency")));
                throw new GeneratorException("The table selection omits dependencies (" + pairs + "); add them to the selection, or allow missing "
                                             + "dependencies if they are already loaded in the target");
            }
            return missing;
        }

        public static List<RunStage> ExecutionStages(List<Group> groups, List<List<string>> chains, List<string> deferred)
        {
            var stages = new List<RunStage>();
            for (var i = 0; i < groups.Count; i++)
            {
                var stage = "Tier_" + Py.D2(groups[i].Tier);
                if (stages.Count > 0 && stages.Last().Name == stage) stages.Last().Chains.Add(chains[i].ToList());
                else stages.Add(new RunStage(stage, new List<List<string>> { chains[i].ToList() }));
            }
            if (deferred.Count > 0) stages.Add(new RunStage(DeferredStage, deferred.Select(d => new List<string> { d }).ToList()));
            return stages;
        }

        public static RenderedPackage Render(byte[] templateXml, string templateLabel, Table reference, List<RunStage> stages, string packageName, string seed)
        {
            // Start from the template so package settings, protection level and shared package
            // parameters match the other packages in the project; table content is removed.
            var baseRendered = Package.Render(templateXml, templateLabel, reference, reference, packageName, seed);
            var doc = Xml.Parse(baseRendered.Xml);
            var root = doc.DocumentElement;
            foreach (var node in Xml.Children(root))
                if (new[] { "Variables", "Executables", "PrecedenceConstraints", "EventHandlers", "DesignTimeProperties", "ConnectionManagers" }.Contains(node.LocalName))
                    root.RemoveChild(node);
            if (Xml.Children(root, "PropertyExpression").Count > 0)
                throw new GeneratorException("The orchestrator cannot keep package-level property expressions from the template");
            root.SetAttribute("DTS:ObjectName", packageName);
            root.SetAttribute("DTS:DTSID", Package.DeterministicGuid(seed, packageName, "orchestrator"));
            root.SetAttribute("DTS:VersionGUID", Package.DeterministicGuid(seed, packageName, "orchestrator-version"));

            var executables = Xml.Dts(doc, "Executables");
            root.AppendChild(executables);
            var constraints = Xml.Dts(doc, "PrecedenceConstraints");
            string previous = null;
            foreach (var stage in stages)
            {
                var stageRef = "Package\\" + stage.Name;
                var container = Xml.Dts(doc, "Executable", "refId", stageRef, "CreationName", "STOCK:SEQUENCE",
                                        "DTSID", Package.DeterministicGuid(seed, packageName, stage.Name), "ExecutableType", "STOCK:SEQUENCE",
                                        "FailPackageOnFailure", "True", "LocaleID", "-1", "ObjectName", stage.Name);
                var tasks = Xml.Dts(doc, "Executables");
                var inner = Xml.Dts(doc, "PrecedenceConstraints");
                foreach (var chain in stage.Chains)
                    for (var i = 1; i < chain.Count; i++)
                    {
                        // Success constraint: a group's harness starts only after its staging load.
                        var label = "Run " + chain[i - 1] + " before Run " + chain[i];
                        inner.AppendChild(Xml.Dts(doc, "PrecedenceConstraint",
                            "refId", stageRef + ".PrecedenceConstraints[" + label + "]", "CreationName", "",
                            "DTSID", Package.DeterministicGuid(seed, packageName, stage.Name + "/" + label),
                            "From", stageRef + "\\Run " + chain[i - 1], "LogicalAnd", "True", "ObjectName", label, "To", stageRef + "\\Run " + chain[i]));
                    }
                foreach (var child in stage.Packages)
                {
                    var taskName = "Run " + child;
                    var task = Xml.Dts(doc, "Executable", "refId", stageRef + "\\" + taskName, "CreationName", "Microsoft.ExecutePackageTask",
                                       "Description", "Execute Package Task", "DTSID", Package.DeterministicGuid(seed, packageName, stage.Name + "/" + child),
                                       "ExecutableType", "Microsoft.ExecutePackageTask", "FailPackageOnFailure", "True",
                                       "LocaleID", "-1", "ObjectName", taskName);
                    var data = Xml.Dts(doc, "ObjectData");
                    var body = doc.CreateElement("ExecutePackageTask");
                    foreach (var pair in new[] { Tuple.Create("UseProjectReference", "True"), Tuple.Create("PackageName", child + ".dtsx") })
                    {
                        var element = doc.CreateElement(pair.Item1);
                        element.AppendChild(doc.CreateTextNode(pair.Item2));
                        body.AppendChild(element);
                    }
                    data.AppendChild(body);
                    task.AppendChild(data);
                    tasks.AppendChild(task);
                }
                container.AppendChild(tasks);
                if (inner.HasChildNodes) container.AppendChild(inner);
                executables.AppendChild(container);
                if (previous != null)
                {
                    // No DTS:Value means a Success constraint: the next stage starts only after every
                    // package in the previous stage succeeded.
                    var label = previous + " before " + stage.Name;
                    constraints.AppendChild(Xml.Dts(doc, "PrecedenceConstraint",
                        "refId", "Package.PrecedenceConstraints[" + label + "]", "CreationName", "",
                        "DTSID", Package.DeterministicGuid(seed, packageName, label), "From", "Package\\" + previous,
                        "LogicalAnd", "True", "ObjectName", label, "To", stageRef));
                }
                previous = stage.Name;
            }
            if (constraints.HasChildNodes) root.AppendChild(constraints);
            Harness.ValidateGroup(root, packageName);
            return new RenderedPackage(packageName, root.GetAttribute("DTS:DTSID"), root.GetAttribute("DTS:VersionGUID"), Xml.ToXml(doc));
        }

        /// <summary>PackageName values of the orchestrator's Execute Package tasks, in document order.</summary>
        public static List<string> ReferencedPackages(byte[] xml)
        {
            var doc = Xml.Parse(xml);
            return Xml.ByTag(doc, "PackageName").Where(n => n.FirstChild != null).Select(n => Xml.Data(n.FirstChild)).ToList();
        }
    }
}
