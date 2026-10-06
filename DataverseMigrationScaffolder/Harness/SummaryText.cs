using System.Collections.Generic;
using System.Linq;

namespace DataverseMigrationScaffolder.HarnessGen
{
    /// <summary>The completion summary shown after a generation.</summary>
    internal static class SummaryText
    {
        /// <summary>Counts of what was generated; packages and tables are counted separately.</summary>
        public static string Headline(GenerationResult result)
        {
            var s = result.Summary;
            var text = s.Get("harnessPackages") + " harness and " + s.Get("stagingPackages") + " staging package(s) for "
                       + s.Get("migratedTables") + " of " + s.Get("tables") + " table(s)";
            if (System.Convert.ToInt32(s.Get("skippedTables")) != 0) text += " (" + s.Get("skippedTables") + " skipped)";
            text += ", " + s.Get("deferredPasses") + " deferred lookup update(s), entry point " + s.Get("entryPoint");
            return text + "; " + ((List<object>)s.Get("projectPackages")).Count + " package(s) in the project";
        }

        public static List<string> Lines(GenerationResult result)
        {
            var lines = new List<string> { "Generated project " + result.ProjectName + " in " + result.OutputDir, "  " + Headline(result) };
            lines.Add("  project: " + System.IO.Path.GetFileName(result.ProjectFile));
            var managers = ((List<object>)result.Summary.Get("projectConnectionManagers")).Cast<string>().ToList();
            lines.Add("  project connection managers: " + (managers.Count > 0 ? string.Join(", ", managers) : "(none)"));
            lines.Add("  setup: " + result.Setup.Get("file") + "  (" + string.Join(", ", ((List<object>)result.Setup.Get("tasks")).Cast<string>()) + ")");
            foreach (var pkg in result.Packages)
            {
                var loaded = ((List<object>)pkg.Get("tables")).Cast<JObj>().Count(t => t.Get("skipped") == null);
                var harness = pkg.Get("file") != null ? pkg.Get("file") + " <- " + loaded + " table(s)" : "no harness (all tables skipped)";
                lines.Add("  tier " + pkg.Get("tier") + " part " + pkg.Get("tierPart") + ": " + pkg.Get("stagingPackage") + ", " + harness);
                foreach (var warning in (List<object>)pkg.Get("warnings")) lines.Add("    warning: " + warning);
            }
            foreach (var pkg in result.Deferred)
            {
                var columns = ((List<object>)pkg.Get("columns")).Cast<JObj>().Select(c => (string)c.Get("column"));
                lines.Add("  deferred: " + pkg.Get("file") + " / " + pkg.Get("task") + "  <- " + string.Join(", ", columns));
                foreach (var warning in (List<object>)pkg.Get("warnings")) lines.Add("    warning: " + warning);
            }
            var stages = ((List<object>)result.Orchestration.Get("stages")).Cast<JObj>().ToList();
            var order = string.Join(" -> ", stages.Select(s => (string)s.Get("name")));
            var runsStaging = stages.Any(s => ((List<object>)s.Get("packages")).Cast<string>().Any(p => p.Contains("Staging")));
            lines.Add("  entry point: " + result.Orchestration.Get("file") + "  (" + order + "); "
                      + (runsStaging ? "each tier runs its staging then harness packages" : "staging packages run separately"));
            var staged = result.Packages.SelectMany(p => ((List<object>)p.Get("tables")).Cast<JObj>()).Count(t => t.Get("stage") != null);
            if (staged > 0) lines.Add("  staging SQL: " + staged + " file(s) in Queries/, one Stage task per table");
            foreach (JObj missing in (List<object>)result.Orchestration.Get("assumedLoaded"))
                lines.Add("    warning: " + missing.Get("table") + " depends on unselected " + missing.Get("dependency") + "; assumed already loaded");
            if (result.ProtectionChangedFrom != null)
                lines.Add("  protection: DontSaveSensitive (was " + result.ProtectionChangedFrom + "); no passwords or secrets are saved - supply them when deploying");
            lines.Add("  unresolved issues: " + result.Issues.Count + " (listed in the report)");
            lines.Add("  report:  " + HarnessProject.ReportName);
            lines.Add("  not built: open and build the project in Visual Studio with SSIS Projects and KingswaySoft before use");
            return lines;
        }
    }
}
